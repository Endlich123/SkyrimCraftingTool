using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // The NPC half of ItemDBHandler's read side (Prio 8 / N-P1, docs/NPC-Plan.md).
    //
    // Deliberately NOT part of LoadCacheCore: that cache is built at startup and again after every
    // scan, and it exists because the item tree needs it immediately. The NPC tab may never be
    // opened in a session, and its data is the biggest single block in the database - 6.580 records
    // with roughly 15.000 faction rows and 100.000 skill rows behind them. It is read when the tab
    // asks for it and not before.
    public partial class ItemDBHandler
    {
        // One SELECT per table rather than a three-way join: joining would multiply the NPC row by
        // its faction and skill counts and hand back ~100.000 wide rows to be de-duplicated in
        // memory. Three flat reads and a dictionary lookup is both less code and less work.
        public List<NpcRecord> LoadNpcs()
        {
            EnsureDatabaseSchema();
            return LoadNpcsFrom(ItemdbPath);
        }

        // The path is a parameter so this can be run against a throwaway database in a test.
        // THE REASON IT IS: a SELECT and the indexes that read it are two lists that have to agree,
        // and nothing checks that they do. Adding a column to the reader without adding it to the
        // query compiles perfectly and throws "ordinal 4" on the first click - which is exactly what
        // happened here, for the second time in this codebase (see OriginalRecordReadTests for the
        // first). NpcLoadTests now reads every field back and compares it with what was written.
        // editedOnly is for the patch generator, which only cares about records the user touched.
        // A flag on the one query rather than a second copy of it: a SELECT and the indexes that
        // read it are two lists that have to agree, and two SELECTs would be four.
        internal static List<NpcRecord> LoadNpcsFrom(string dbPath, bool editedOnly = false)
        {
            var npcs = new List<NpcRecord>();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return npcs;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            // An item.db written before the NPC tables existed has none of them. That is not an
            // error: EnsureDatabaseSchema adds them on the next cache load, and until then "no NPCs"
            // is the honest answer. Same guard LeveledListOdds uses for the Globals table.
            if (!TableExists(connection, "Npc")) return npcs;

            var byKey = new Dictionary<string, NpcRecord>(System.StringComparer.OrdinalIgnoreCase);

            // Which NPCs own an edited list. Read from the flag rather than inferred from the
            // snapshot tables, because an empty snapshot and a missing one look the same.
            var factionsEdited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var listsEdited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            using (var cmd = connection.CreateCommand())
            {
                // The shadow-column contract, the same one Armor and Weapons are read through: the
                // base column is what the scan found, IsEdited<X> is what the user set, and the
                // user's value only counts while the record is actually flagged as edited.
                cmd.CommandText = @"
                    SELECT
                        Key,
                        EditorID,
                        CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL THEN IsEditedName ELSE Name END,
                        CASE WHEN IsEdited = 1 AND IsEditedShortName IS NOT NULL THEN IsEditedShortName ELSE ShortName END,
                        CASE WHEN IsEdited = 1 AND IsEditedClassKey IS NOT NULL THEN IsEditedClassKey ELSE ClassKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedRaceKey IS NOT NULL THEN IsEditedRaceKey ELSE RaceKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedUsesPcLevelMult IS NOT NULL THEN IsEditedUsesPcLevelMult ELSE UsesPcLevelMult END,
                        CASE WHEN IsEdited = 1 AND IsEditedLevel IS NOT NULL THEN IsEditedLevel ELSE Level END,
                        LevelMult,
                        CASE WHEN IsEdited = 1 AND IsEditedCalcMinLevel IS NOT NULL THEN IsEditedCalcMinLevel ELSE CalcMinLevel END,
                        CASE WHEN IsEdited = 1 AND IsEditedCalcMaxLevel IS NOT NULL THEN IsEditedCalcMaxLevel ELSE CalcMaxLevel END,
                        CASE WHEN IsEdited = 1 AND IsEditedHealth IS NOT NULL THEN IsEditedHealth ELSE Health END,
                        CASE WHEN IsEdited = 1 AND IsEditedMagicka IS NOT NULL THEN IsEditedMagicka ELSE Magicka END,
                        CASE WHEN IsEdited = 1 AND IsEditedStamina IS NOT NULL THEN IsEditedStamina ELSE Stamina END,
                        HealthOffset,
                        MagickaOffset,
                        StaminaOffset,
                        CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL THEN IsEditedFlags ELSE Flags END,
                        CASE WHEN IsEdited = 1 AND IsEditedTemplateFlags IS NOT NULL THEN IsEditedTemplateFlags ELSE TemplateFlags END,
                        TemplateKey,
                        CASE WHEN IsEdited = 1 AND IsEditedVoiceKey IS NOT NULL THEN IsEditedVoiceKey ELSE VoiceKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedDefaultOutfitKey IS NOT NULL THEN IsEditedDefaultOutfitKey ELSE DefaultOutfitKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedSleepOutfitKey IS NOT NULL THEN IsEditedSleepOutfitKey ELSE SleepOutfitKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedDeathItemKey IS NOT NULL THEN IsEditedDeathItemKey ELSE DeathItemKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedSkinKey IS NOT NULL THEN IsEditedSkinKey ELSE SkinKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedCombatStyleKey IS NOT NULL THEN IsEditedCombatStyleKey ELSE CombatStyleKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedCrimeFactionKey IS NOT NULL THEN IsEditedCrimeFactionKey ELSE CrimeFactionKey END,
                        CASE WHEN IsEdited = 1 AND IsEditedWeight IS NOT NULL THEN IsEditedWeight ELSE Weight END,
                        CASE WHEN IsEdited = 1 AND IsEditedHeight IS NOT NULL THEN IsEditedHeight ELSE Height END,
                        CASE WHEN IsEdited = 1 AND IsEditedAggression IS NOT NULL THEN IsEditedAggression ELSE Aggression END,
                        CASE WHEN IsEdited = 1 AND IsEditedConfidence IS NOT NULL THEN IsEditedConfidence ELSE Confidence END,
                        CASE WHEN IsEdited = 1 AND IsEditedAssistance IS NOT NULL THEN IsEditedAssistance ELSE Assistance END,
                        CASE WHEN IsEdited = 1 AND IsEditedMorality IS NOT NULL THEN IsEditedMorality ELSE Morality END,
                        CASE WHEN IsEdited = 1 AND IsEditedMood IS NOT NULL THEN IsEditedMood ELSE Mood END,
                        EnergyLevel,
                        CASE WHEN IsEdited = 1 AND IsEditedKeywords IS NOT NULL THEN IsEditedKeywords ELSE Keywords END,
                        IsEdited,

                        -- The same fields again, unfiltered: what the scan read. The editor marks a
                        -- field as changed by comparing the two, and a reset has to know what it
                        -- goes back to without re-reading the plugin.
                        UsesPcLevelMult, Level, CalcMinLevel, CalcMaxLevel, Health, Magicka, Stamina,
                        Keywords,
                        ClassKey, RaceKey, VoiceKey, DefaultOutfitKey, SleepOutfitKey, DeathItemKey,
                        SkinKey, Flags, TemplateFlags, Weight, Height,
                        Aggression, Confidence, Assistance, Morality, Mood,
                        CombatStyleKey, CrimeFactionKey,

                        -- Which lists the user has taken over. The snapshot tables cannot answer
                        -- that on their own: a list that is EMPTY in the plugins snapshots to no
                        -- rows, which looks exactly like no snapshot at all.
                        FactionsEdited, ListsEdited
                    FROM Npc
                    WHERE Active = 1" + (editedOnly ? " AND IsEdited = 1" : "") + ";";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int i = 0;
                    var npc = new NpcRecord
                    {
                        Key = Str(reader, i++),
                        EditorID = Str(reader, i++),
                        Name = Str(reader, i++),
                        ShortName = Str(reader, i++),
                        ClassKey = Str(reader, i++),
                        RaceKey = Str(reader, i++),
                        UsesPcLevelMult = Int(reader, i++) != 0,
                        Level = Int(reader, i++),
                        LevelMult = (float)Dbl(reader, i++),
                        CalcMinLevel = Int(reader, i++),
                        CalcMaxLevel = Int(reader, i++),
                        Health = Int(reader, i++),
                        Magicka = Int(reader, i++),
                        Stamina = Int(reader, i++),
                        HealthOffset = Int(reader, i++),
                        MagickaOffset = Int(reader, i++),
                        StaminaOffset = Int(reader, i++),
                        Flags = (uint)Lng(reader, i++),
                        TemplateFlags = (uint)Lng(reader, i++),
                        TemplateKey = Str(reader, i++),
                        VoiceKey = Str(reader, i++),
                        DefaultOutfitKey = Str(reader, i++),
                        SleepOutfitKey = Str(reader, i++),
                        DeathItemKey = Str(reader, i++),
                        SkinKey = Str(reader, i++),
                        CombatStyleKey = Str(reader, i++),
                        CrimeFactionKey = Str(reader, i++),
                        Weight = (float)Dbl(reader, i++),
                        Height = (float)Dbl(reader, i++),
                        Aggression = Str(reader, i++),
                        Confidence = Str(reader, i++),
                        Assistance = Str(reader, i++),
                        Morality = Str(reader, i++),
                        Mood = Str(reader, i++),
                        EnergyLevel = Int(reader, i++),
                    };

                    npc.Keywords = SplitKeys(Str(reader, i++));

                    npc.IsEdited = Int(reader, i++) != 0;

                    npc.Scanned = new NpcScannedValues
                    {
                        UsesPcLevelMult = Int(reader, i++) != 0,
                        Level = Int(reader, i++),
                        CalcMinLevel = Int(reader, i++),
                        CalcMaxLevel = Int(reader, i++),
                        Health = Int(reader, i++),
                        Magicka = Int(reader, i++),
                        Stamina = Int(reader, i++),
                    };

                    npc.ScannedKeywords = SplitKeys(Str(reader, i++));

                    npc.Scanned.ClassKey = Str(reader, i++);
                    npc.Scanned.RaceKey = Str(reader, i++);
                    npc.Scanned.VoiceKey = Str(reader, i++);
                    npc.Scanned.DefaultOutfitKey = Str(reader, i++);
                    npc.Scanned.SleepOutfitKey = Str(reader, i++);
                    npc.Scanned.DeathItemKey = Str(reader, i++);
                    npc.Scanned.SkinKey = Str(reader, i++);
                    npc.Scanned.Flags = (uint)Lng(reader, i++);
                    npc.Scanned.TemplateFlags = (uint)Lng(reader, i++);
                    npc.Scanned.Weight = (float)Dbl(reader, i++);
                    npc.Scanned.Height = (float)Dbl(reader, i++);
                    npc.Scanned.Aggression = Str(reader, i++);
                    npc.Scanned.Confidence = Str(reader, i++);
                    npc.Scanned.Assistance = Str(reader, i++);
                    npc.Scanned.Morality = Str(reader, i++);
                    npc.Scanned.Mood = Str(reader, i++);
                    npc.Scanned.CombatStyleKey = Str(reader, i++);
                    npc.Scanned.CrimeFactionKey = Str(reader, i++);

                    if (Int(reader, i++) != 0) factionsEdited.Add(npc.Key);
                    if (Int(reader, i++) != 0) listsEdited.Add(npc.Key);

                    npcs.Add(npc);
                    byKey[npc.Key] = npc;
                }
            }

            // The two child tables. A row whose parent is gone is skipped rather than kept: it can
            // only be left over from a plugin that has since been deactivated.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT NpcKey, FactionKey, Rank FROM NpcFactions;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!byKey.TryGetValue(Str(reader, 0), out var npc)) continue;
                    npc.Factions.Add(new NpcFactionRecord { FactionKey = Str(reader, 1), Rank = Int(reader, 2) });
                }
            }

            // The snapshot of what the scan found, written once when an NPC.s faction list is first
            // edited. An NPC that has never been edited has no snapshot - its live list IS what the
            // scan found, so that is what gets copied in below. Same lazy pattern as
            // WornRestrictionKeywords_Original.
            if (TableExists(connection, "NpcFactions_Original"))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT NpcKey, FactionKey, Rank FROM NpcFactions_Original;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!byKey.TryGetValue(Str(reader, 0), out var npc)) continue;
                    npc.ScannedFactions.Add(new NpcFactionRecord { FactionKey = Str(reader, 1), Rank = Int(reader, 2) });
                }
            }

            foreach (var npc in npcs)
            {
                if (factionsEdited.Contains(npc.Key)) continue;
                npc.ScannedFactions = npc.Factions
                    .Select(f => new NpcFactionRecord { FactionKey = f.FactionKey, Rank = f.Rank })
                    .ToList();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT NpcKey, Skill,
                           CASE WHEN IsEditedValue IS NOT NULL THEN IsEditedValue ELSE Value END,
                           Offset,
                           -- Unfiltered, for the change marker and the reset: what the scan read.
                           Value
                    FROM NpcSkills;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!byKey.TryGetValue(Str(reader, 0), out var npc)) continue;
                    npc.Skills.Add(new NpcSkillRecord
                    {
                        Skill = Str(reader, 1),
                        Value = Int(reader, 2),
                        Offset = Int(reader, 3),
                        ScannedValue = Int(reader, 4),
                    });
                }
            }

            // The three N-P3 lists, each with its lazy snapshot - the same arrangement as factions.
            // A local function, because the shape is identical three times over and the only
            // differences are the table and what a row carries.
            void ReadList(string table, string snapshotTable,
                          Action<NpcRecord, SqliteDataReader> live,
                          Action<NpcRecord, SqliteDataReader> snapshot,
                          Action<NpcRecord> copyLiveIntoScanned,
                          string columns)
            {
                if (!TableExists(connection, table)) return;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"SELECT NpcKey, {columns} FROM {table};";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        if (byKey.TryGetValue(Str(reader, 0), out var npc)) live(npc, reader);
                }

                if (TableExists(connection, snapshotTable))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT NpcKey, {columns} FROM {snapshotTable};";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!byKey.TryGetValue(Str(reader, 0), out var npc)) continue;
                        snapshot(npc, reader);
                    }
                }

                // Nobody has edited these lists, so the live rows ARE what the scan found. Decided
                // by the flag, not by whether the snapshot table happened to have rows: a list the
                // plugins left empty snapshots to nothing, and copying the live rows in that case
                // would present the user.s own additions as the plugin.s.
                foreach (var npc in npcs)
                    if (!listsEdited.Contains(npc.Key))
                        copyLiveIntoScanned(npc);
            }

            ReadList("NpcSpells", "NpcSpells_Original",
                (npc, r) => npc.Spells.Add(new NpcSpellRecord { SpellKey = Str(r, 1), Kind = Str(r, 2) }),
                (npc, r) => npc.ScannedSpells.Add(new NpcSpellRecord { SpellKey = Str(r, 1), Kind = Str(r, 2) }),
                npc => npc.ScannedSpells = npc.Spells
                    .Select(s => new NpcSpellRecord { SpellKey = s.SpellKey, Kind = s.Kind }).ToList(),
                "SpellKey, Kind");

            ReadList("NpcPerks", "NpcPerks_Original",
                (npc, r) => npc.Perks.Add(new NpcPerkRecord { PerkKey = Str(r, 1), Rank = Int(r, 2) }),
                (npc, r) => npc.ScannedPerks.Add(new NpcPerkRecord { PerkKey = Str(r, 1), Rank = Int(r, 2) }),
                npc => npc.ScannedPerks = npc.Perks
                    .Select(p => new NpcPerkRecord { PerkKey = p.PerkKey, Rank = p.Rank }).ToList(),
                "PerkKey, Rank");

            ReadList("NpcItems", "NpcItems_Original",
                (npc, r) => npc.Items.Add(new NpcItemRecord { ItemKey = Str(r, 1), Count = Int(r, 2) }),
                (npc, r) => npc.ScannedItems.Add(new NpcItemRecord { ItemKey = Str(r, 1), Count = Int(r, 2) }),
                npc => npc.ScannedItems = npc.Items
                    .Select(i => new NpcItemRecord { ItemKey = i.ItemKey, Count = i.Count }).ToList(),
                "ItemKey, Count");

            return npcs;
        }

        // Key -> readable name for everything an NPC points at. Six catalogue tables plus three
        // that already existed for other reasons: NPC names (for the template link), leveled lists
        // (death item) and armor (skin).
        //
        // An NPC's template usually points at a leveled character rather than another NPC, and those
        // are not scanned - the key is then all there is to show, which is what NpcLabels does with
        // an unresolved key. Better than a dash: a dash would claim there is nothing there.
        // Both public entry points migrate first. The startup cache already does this, so on a
        // normal launch it is a no-op - but "the NPC tab happens to be opened after the cache was
        // built" is an ordering assumption, not a guarantee, and this is the third time a column
        // added late has thrown at a reader that trusted one. It costs one connection on a tab the
        // user opens by hand; the alternative costs a crash on an item.db from before the column.
        // The levelled character lists, as list key -> the keys of what it holds, in list order.
        //
        // For NpcStatResolver: 1.650 NPCs take their stats from one of these, and the list's contents
        // are what turns "no single value" into a range over the candidates. Entries may point at
        // another LVLN - stored as they stand, followed by the reader.
        //
        // Small enough to load whole: measured on the real load order, the lists and their entries are
        // a fraction of what the NPC tables weigh.
        internal static Dictionary<string, List<string>> LoadLeveledNpcsFrom(string dbPath)
        {
            var byList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return byList;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            // A database scanned before the LVLN tables existed has none of them. Not an error: the
            // next scan fills them, and until then every case-D NPC keeps saying it has no single
            // value, which is what it said before.
            if (!TableExists(connection, "LeveledNpcEntry")) return byList;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT e.ListKey, e.Reference
                  FROM LeveledNpcEntry e
                  JOIN LeveledNpc l ON l.Key = e.ListKey
                 WHERE l.Active = 1
                 ORDER BY e.ListKey, e.Ordinal;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string listKey = reader.GetString(0);
                if (!byList.TryGetValue(listKey, out var entries))
                    byList[listKey] = entries = new List<string>();

                entries.Add(reader.GetString(1));
            }

            return byList;
        }

        public Dictionary<string, List<string>> LoadLeveledNpcs()
        {
            EnsureDatabaseSchema();
            return LoadLeveledNpcsFrom(ItemdbPath);
        }

        public NpcLabels LoadNpcLabels()
        {
            EnsureDatabaseSchema();
            return LoadNpcLabelsFrom(ItemdbPath);
        }

        internal static NpcLabels LoadNpcLabelsFrom(string dbPath)
        {
            var labels = new NpcLabels();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return labels;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            void Fill(Dictionary<string, string> into, string table, string sql)
            {
                // Skipped rather than thrown on, for the same reason as above: an older database
                // simply has fewer catalogues, and an unresolved key still shows as itself.
                if (!TableExists(connection, table)) return;

                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = Str(reader, 0);
                    if (key.Length == 0) continue;

                    // EditorID first, name second: EditorIDs are unique and the names are not -
                    // several hundred factions are called the same thing, and a list of duplicates
                    // cannot be navigated. Same reasoning as the enchantment picker's label.
                    string editorId = Str(reader, 1);
                    string name = reader.FieldCount > 2 ? Str(reader, 2) : "";

                    into[key] = editorId.Length > 0 ? editorId
                              : name.Length > 0 ? name
                              : key;
                }
            }

            // The numbers behind the names, for the auto-calc prediction. Read here rather than
            // in a second pass: the labels are already the one thing the NPC tab loads on open.
            if (TableExists(connection, "Races"))
            {
                using var raceCmd = connection.CreateCommand();
                raceCmd.CommandText =
                    "SELECT Key, StartingHealth, StartingMagicka, StartingStamina FROM Races WHERE Active = 1;";
                using var raceReader = raceCmd.ExecuteReader();
                while (raceReader.Read())
                {
                    string key = Str(raceReader, 0);
                    if (key.Length == 0) continue;
                    labels.RaceBase[key] = ((float)Dbl(raceReader, 1), (float)Dbl(raceReader, 2), (float)Dbl(raceReader, 3));
                }
            }

            if (TableExists(connection, "Classes"))
            {
                using var classCmd = connection.CreateCommand();
                classCmd.CommandText =
                    "SELECT Key, HealthWeight, MagickaWeight, StaminaWeight FROM Classes WHERE Active = 1;";
                using var classReader = classCmd.ExecuteReader();
                while (classReader.Read())
                {
                    string key = Str(classReader, 0);
                    if (key.Length == 0) continue;
                    labels.ClassWeights[key] = ((int)Lng(classReader, 1), (int)Lng(classReader, 2), (int)Lng(classReader, 3));
                }
            }

            Fill(labels.Factions, "Factions", "SELECT Key, EditorID, Name FROM Factions WHERE Active = 1;");
            Fill(labels.Classes, "Classes", "SELECT Key, EditorID, Name FROM Classes WHERE Active = 1;");
            Fill(labels.Races, "Races", "SELECT Key, EditorID, Name FROM Races WHERE Active = 1;");
            Fill(labels.Outfits, "Outfits", "SELECT Key, EditorID FROM Outfits WHERE Active = 1;");
            Fill(labels.VoiceTypes, "VoiceTypes", "SELECT Key, EditorID FROM VoiceTypes WHERE Active = 1;");
            Fill(labels.CombatStyles, "CombatStyles", "SELECT Key, EditorID FROM CombatStyles WHERE Active = 1;");
            Fill(labels.Npcs, "Npc", "SELECT Key, EditorID, Name FROM Npc WHERE Active = 1;");
            Fill(labels.LeveledItems, "LeveledList", "SELECT Key, EditorID FROM LeveledList WHERE Active = 1;");
            Fill(labels.LeveledNpcs, "LeveledNpc", "SELECT Key, EditorID FROM LeveledNpc WHERE Active = 1;");
            Fill(labels.Armors, "Armor", "SELECT Key, EditorID, Name FROM Armor WHERE Active = 1;");
            Fill(labels.Spells, "Spells", "SELECT Key, EditorID, Name FROM Spells WHERE Active = 1;");
            Fill(labels.Shouts, "Shouts", "SELECT Key, EditorID, Name FROM Shouts WHERE Active = 1;");
            Fill(labels.LeveledSpells, "LeveledSpells", "SELECT Key, EditorID FROM LeveledSpells WHERE Active = 1;");
            Fill(labels.Perks, "NpcPerkCatalogue", "SELECT Key, EditorID, Name FROM NpcPerkCatalogue WHERE Active = 1;");
            Fill(labels.Weapons, "Weapons", "SELECT Key, EditorID, Name FROM Weapons WHERE Active = 1;");

            return labels;
        }

        private static List<string> SplitKeys(string joined) =>
            string.IsNullOrWhiteSpace(joined)
                ? new List<string>()
                : joined.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

        private static string Str(SqliteDataReader reader, int index) =>
            reader.IsDBNull(index) ? "" : reader.GetString(index);

        private static int Int(SqliteDataReader reader, int index) =>
            reader.IsDBNull(index) ? 0 : reader.GetInt32(index);

        private static long Lng(SqliteDataReader reader, int index) =>
            reader.IsDBNull(index) ? 0L : reader.GetInt64(index);

        private static double Dbl(SqliteDataReader reader, int index) =>
            reader.IsDBNull(index) ? 0d : reader.GetDouble(index);
    }
}
