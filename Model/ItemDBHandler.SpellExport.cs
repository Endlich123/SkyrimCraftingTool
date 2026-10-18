using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // TE-3: the Spells tab's export unit (docs/TabExport-Plan.md). Shaped like the enchantment
    // export it sits next to - a scanned spell carries its shadow columns and, when the effects were
    // edited, the whole effect list; a user-created spell carries its whole row (UserRecords).
    //
    // Effects travel in Rows["Effects"], the generic child-list slot, not in a sixth dedicated
    // property next to EffectRows.
    public partial class ItemDBHandler
    {
        internal const string SpellEffectRowsName = "Effects";

        private static readonly string[] SpellEffectColumns =
            { "MagicEffectKey", "EditorID", "Name", "Magnitude", "Duration", "Area" };

        internal static List<EditedItemDto> GetEditedSpells(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            // No Spells table, no spell edits - see GetEditedNpcs.
            if (!UserRecords.TableExists(connection, "Spells")) return new List<EditedItemDto>();

            var rows = new List<(string Key, bool EffectsEdited, bool Own, EditedItemDto Dto)>();
            string[] editedCols = SpellShadowColumns;

            using (var cmd = connection.CreateCommand())
            {
                // Flags, not LastChanged - same reasoning as GetEditedEnchantments. Original = 0
                // always exports: a spell the user created is new content whether or not it was
                // touched again after.
                cmd.CommandText = $@"SELECT Key, LastChanged, EffectsEdited, Name, Original,
                        {string.Join(", ", editedCols)}
                    FROM Spells
                    WHERE (IsEdited = 1 OR EffectsEdited = 1 OR Original = 0)" + BuildScopeWhere(scope, scopeValue, cmd);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    bool effectsEdited = !reader.IsDBNull(2) && reader.GetInt64(2) == 1;
                    bool own = !reader.IsDBNull(4) && reader.GetInt64(4) == 0;

                    var dto = new EditedItemDto { Table = "Spells", Key = key, LastChanged = SafeStr(reader, 1), DisplayName = SafeStr(reader, 3) };
                    for (int i = 0; i < editedCols.Length; i++)
                    {
                        int ordinal = 5 + i;
                        if (!reader.IsDBNull(ordinal))
                            dto.Fields[editedCols[i]] = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                    }
                    rows.Add((key, effectsEdited, own, dto));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var row in rows)
            {
                if (row.Own)
                {
                    row.Dto.Original = 0;
                    row.Dto.Fields = UserRecords.ReadRow(connection, "Spells", row.Key);
                    row.Dto.Uid = UserRecords.EnsureUid(connection, "Spells", row.Key);
                }
                if (row.Own || row.EffectsEdited)
                {
                    row.Dto.Rows = new Dictionary<string, List<Dictionary<string, string>>>
                    {
                        [SpellEffectRowsName] = GetSpellEffectRowsForExport(connection, row.Key),
                    };
                }
                result.Add(row.Dto);
            }
            return result;
        }

        // Invariant culture on purpose: a magnitude of 12.5 must not travel as "12,5" from a German
        // machine and arrive as text on an English one.
        private static List<Dictionary<string, string>> GetSpellEffectRowsForExport(SqliteConnection connection, string spellKey)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", SpellEffectColumns)} FROM SpellEffects WHERE SpellKey = @key";
            cmd.Parameters.AddWithValue("@key", spellKey);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, string>();
                for (int i = 0; i < SpellEffectColumns.Length; i++)
                    row[SpellEffectColumns[i]] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                rows.Add(row);
            }
            return rows;
        }

        internal static bool ApplySpellItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            bool wrote;
            if (item.Original == 0)
            {
                wrote = UserRecords.WriteRow(connection, transaction, "Spells", item, item.LastChanged);
                if (!wrote) return false;
            }
            else
            {
                // Preview already routed missing keys to ToSkipMissing — defensive guard.
                if (!RowExists(connection, transaction, "Spells", item.Key))
                    return false;
                wrote = ApplyFieldUpdate(connection, transaction, "Spells", item.Key, item.LastChanged, item.Fields);
            }

            if (item.Rows != null && item.Rows.TryGetValue(SpellEffectRowsName, out var effects) && effects != null)
            {
                // The same three steps SaveSpellEffects takes, in the import's transaction: snapshot
                // on the first edit (or Reset later restores from an empty _Original - the data loss
                // docs/ImportExport-Analyse.md B2 describes), replace, flag.
                SnapshotChildRowsIfFirstEdit(connection, transaction, ChildTables.SpellEffects, item.Key);

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM SpellEffects WHERE SpellKey = @key";
                    delete.Parameters.AddWithValue("@key", item.Key);
                    delete.ExecuteNonQuery();
                }

                foreach (var effect in effects)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    // OR IGNORE: PRIMARY KEY (SpellKey, MagicEffectKey), and a file that repeats an
                    // effect must not roll back the whole import.
                    insert.CommandText = @"INSERT OR IGNORE INTO SpellEffects (SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                                           VALUES (@key, @mgef, @editorId, @name, @magnitude, @duration, @area)";
                    insert.Parameters.AddWithValue("@key", item.Key);
                    insert.Parameters.AddWithValue("@mgef", effect.GetValueOrDefault("MagicEffectKey", ""));
                    insert.Parameters.AddWithValue("@editorId", effect.GetValueOrDefault("EditorID", ""));
                    insert.Parameters.AddWithValue("@name", effect.GetValueOrDefault("Name", ""));
                    insert.Parameters.AddWithValue("@magnitude", Number(effect.GetValueOrDefault("Magnitude", "0")));
                    insert.Parameters.AddWithValue("@duration", Number(effect.GetValueOrDefault("Duration", "0")));
                    insert.Parameters.AddWithValue("@area", Number(effect.GetValueOrDefault("Area", "0")));
                    insert.ExecuteNonQuery();
                }

                using (var flag = connection.CreateCommand())
                {
                    flag.Transaction = transaction;
                    // LastChanged too: a spell whose only edit is its effects otherwise arrives with
                    // LastChanged = NULL, its next export carries a blank timestamp, and an import of
                    // that file elsewhere rejects it as unreadable (found 2026-10-17).
                    flag.CommandText = "UPDATE Spells SET EffectsEdited = 1, IsEdited = 1, LastChanged = @t WHERE Key = @key";
                    flag.Parameters.AddWithValue("@key", item.Key);
                    flag.Parameters.AddWithValue("@t", string.IsNullOrEmpty(item.LastChanged) ? NowIso() : item.LastChanged);
                    flag.ExecuteNonQuery();
                }
                wrote = true;
            }

            return wrote;
        }

        // Invariant parse of an exported number; the raw text if it is not one, so SQLite's affinity
        // still gets a chance at it rather than the value being lost.
        private static object Number(string raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : raw;
    }
}
