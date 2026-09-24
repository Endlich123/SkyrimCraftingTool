using System;
using DynamicData;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Globalization;
using SkyrimCraftingTool.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    // Scan half of ItemDBHandler: PutIntoDataBank (parallel parse -> sequential write), the per-plugin Mutagen parse, the Parsed* row carriers and the column/param arrays + batch-insert helpers they feed.
    // Split out of ItemDBHandler.cs purely for navigability - no logic changed.
    public partial class ItemDBHandler
    {
        // ============================
        //        DB ERSTELLEN
        // ============================

        public void PutIntoDataBank(List<PluginInfo> allgamePathfromDB)
        {
            Directory.CreateDirectory(ItemFolder);

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(connection);
            CreateTables(connection);

            // Which COBJ/Enchantment records has the user hand-edited Conditions/Effects/
            // WornRestrictionKeywords for? Unlike Armor/Weapon/COBJ/Enchantment's own fields (which
            // have IsEdited* shadow columns on the SAME row), these three child tables have no
            // shadow-column mechanism — a user edit is a full replace of the base child rows. So the
            // only way to protect a hand-edited child set from being silently overwritten by this scan
            // is a flag on the PARENT row, checked before we touch its children below.
            var conditionsEditedKeys = ReadFlaggedKeys(connection, "SELECT Key FROM COBJ WHERE ConditionsEdited = 1;");
            var effectsEditedKeys = ReadFlaggedKeys(connection, "SELECT Key FROM Enchantments WHERE EffectsEdited = 1;");
            // E3: FLST content-edit protection is per-list now (WornRestrictionListState), not per
            // enchantment. The legacy Enchantments.KeywordsEdited term is unioned in so a DB not yet
            // migrated by RepairBlankWornRestrictionEdits still protects a hand-edited list.
            var listContentEditedKeys = ReadFlaggedKeys(connection,
                @"SELECT ListKey FROM WornRestrictionListState WHERE IsEdited = 1
                  UNION
                  SELECT WornRestrictionListKey FROM Enchantments
                  WHERE KeywordsEdited = 1 AND WornRestrictionListKey IS NOT NULL AND WornRestrictionListKey <> '';");

            using var insertArmor = PrepareUpsert(connection, "Armor", ArmorColumnNames, ArmorParamNames);
            using var insertWeapon = PrepareUpsert(connection, "Weapons", WeaponColumnNames, WeaponParamNames);
            using var insertCOBJ = PrepareUpsert(connection, "COBJ", CobjColumnNames, CobjParamNames);
            using var insertCOBJCondition = PrepareInsert(connection, "COBJ_Conditions", CobjConditionColumnNames, CobjConditionParamNames);
            using var insertEnch = PrepareUpsert(connection, "Enchantments", EnchantmentColumnNames, EnchantmentParamNames);
            using var insertEnchEff = PrepareInsert(connection, "EnchantmentEffects", EnchantmentEffectColumnNames, EnchantmentEffectParamNames);
            using var insertWRK = PrepareInsert(connection, "WornRestrictionKeywords", WornRestrictionKeywordColumnNames, WornRestrictionKeywordParamNames);
            using var insertContainer = PrepareUpsert(connection, "Container", ContainerColumnNames, ContainerParamNames);
            using var insertContainerLVLI = PrepareInsert(connection, "ContainerLVLI", ContainerLvliColumnNames, ContainerLvliParamNames);
            using var insertContainerEntry = PrepareInsert(connection, "ContainerEntry", ContainerEntryColumnNames, ContainerEntryParamNames);
            using var insertMagicEffects = PrepareUpsert(connection, "MagicEffects", MagicEffectColumnNames, MagicEffectParamNames);
            using var insertGlobals = PrepareUpsert(connection, "Globals", GlobalColumnNames, GlobalParamNames);
            using var insertLeveledList = PrepareUpsert(connection, "LeveledList", LeveledListColumnNames, LeveledListParamNames);
            using var insertLeveledListEntry = PrepareInsert(connection, "LeveledListEntry", LeveledListEntryColumnNames, LeveledListEntryParamNames);
            using var insertLeveledListLost = PrepareInsert(connection, "LeveledListLostEntry", LeveledListLostColumnNames, LeveledListLostParamNames);

            // Multi-row "batch" counterparts of the commands above. At the row counts a full scan
            // produces (100k+), one ExecuteNonQuery() per row was the dominant cost (~140k round
            // trips). These insert BatchSize rows per statement instead; the tail that doesn't fill a
            // full batch falls back to the single-row commands above. Row count (<=90) and per-row
            // column count (<=10) keep every batch statement's parameter count well under SQLite's
            // historical 999 host-parameter limit and its 500-row multi-VALUES limit.
            using var insertArmorBatch = PrepareUpsertBatch(connection, "Armor", ArmorColumnNames, ArmorParamNames, BatchSize);
            using var insertWeaponBatch = PrepareUpsertBatch(connection, "Weapons", WeaponColumnNames, WeaponParamNames, BatchSize);
            using var insertCOBJBatch = PrepareUpsertBatch(connection, "COBJ", CobjColumnNames, CobjParamNames, BatchSize);
            using var insertCOBJConditionBatch = PrepareInsertBatch(connection, "COBJ_Conditions", CobjConditionColumnNames, CobjConditionParamNames, BatchSize);
            using var insertEnchBatch = PrepareUpsertBatch(connection, "Enchantments", EnchantmentColumnNames, EnchantmentParamNames, BatchSize);
            using var insertEnchEffBatch = PrepareInsertBatch(connection, "EnchantmentEffects", EnchantmentEffectColumnNames, EnchantmentEffectParamNames, BatchSize);
            using var insertWRKBatch = PrepareInsertBatch(connection, "WornRestrictionKeywords", WornRestrictionKeywordColumnNames, WornRestrictionKeywordParamNames, BatchSize);
            using var insertContainerBatch = PrepareUpsertBatch(connection, "Container", ContainerColumnNames, ContainerParamNames, BatchSize);
            using var insertContainerLVLIBatch = PrepareInsertBatch(connection, "ContainerLVLI", ContainerLvliColumnNames, ContainerLvliParamNames, BatchSize);
            using var insertContainerEntryBatch = PrepareInsertBatch(connection, "ContainerEntry", ContainerEntryColumnNames, ContainerEntryParamNames, BatchSize);
            using var insertMagicEffectsBatch = PrepareUpsertBatch(connection, "MagicEffects", MagicEffectColumnNames, MagicEffectParamNames, BatchSize);
            using var insertGlobalsBatch = PrepareUpsertBatch(connection, "Globals", GlobalColumnNames, GlobalParamNames, BatchSize);
            using var insertLeveledListBatch = PrepareUpsertBatch(connection, "LeveledList", LeveledListColumnNames, LeveledListParamNames, BatchSize);
            using var insertLeveledListEntryBatch = PrepareInsertBatch(connection, "LeveledListEntry", LeveledListEntryColumnNames, LeveledListEntryParamNames, BatchSize);
            using var insertLeveledListLostBatch = PrepareInsertBatch(connection, "LeveledListLostEntry", LeveledListLostColumnNames, LeveledListLostParamNames, BatchSize);

            using var transaction = connection.BeginTransaction();
            insertArmor.Transaction = transaction;
            insertWeapon.Transaction = transaction;
            insertCOBJ.Transaction = transaction;
            insertCOBJCondition.Transaction = transaction;
            insertEnch.Transaction = transaction;
            insertEnchEff.Transaction = transaction;
            insertWRK.Transaction = transaction;
            insertContainer.Transaction = transaction;
            insertContainerLVLI.Transaction = transaction;
            insertContainerEntry.Transaction = transaction;
            insertMagicEffects.Transaction = transaction;
            insertGlobals.Transaction = transaction;
            insertLeveledList.Transaction = transaction;
            insertLeveledListEntry.Transaction = transaction;
            insertLeveledListLost.Transaction = transaction;
            insertArmorBatch.Transaction = transaction;
            insertWeaponBatch.Transaction = transaction;
            insertCOBJBatch.Transaction = transaction;
            insertCOBJConditionBatch.Transaction = transaction;
            insertEnchBatch.Transaction = transaction;
            insertEnchEffBatch.Transaction = transaction;
            insertWRKBatch.Transaction = transaction;
            insertContainerBatch.Transaction = transaction;
            insertContainerLVLIBatch.Transaction = transaction;
            insertContainerEntryBatch.Transaction = transaction;
            insertMagicEffectsBatch.Transaction = transaction;
            insertGlobalsBatch.Transaction = transaction;
            insertLeveledListBatch.Transaction = transaction;
            insertLeveledListEntryBatch.Transaction = transaction;
            insertLeveledListLostBatch.Transaction = transaction;

            // Parse phase runs in parallel across plugins (CPU-bound, no DB access); a second,
            // strictly sequential phase then does all SQLite writes (one connection/transaction).
            //
            // Results go into an array indexed by plugin position, not a ConcurrentBag: with
            // "INSERT OR REPLACE", the last write for a given key wins, so override precedence
            // between plugins depends on write order matching allgamePathfromDB's load order
            // exactly. Parallel.For writes each result into its own reserved slot, preserving that
            // order regardless of thread scheduling.
            _formIDDB.EnsureCacheLoaded();

            var pluginFiles = allgamePathfromDB
                .SelectMany(plugin => plugin.FullPaths.Select(fullPath => (plugin.FileName, fullPath)))
                .ToList();

            var parseSw = Stopwatch.StartNew();
            var parsedPlugins = new ParsedPluginData[pluginFiles.Count];
            Parallel.For(0, pluginFiles.Count, i =>
            {
                parsedPlugins[i] = ParsePluginForItemDB(pluginFiles[i].FileName, pluginFiles[i].fullPath);
            });
            parseSw.Stop();

            int armorCount = 0, weaponCount = 0, cobjCount = 0, condCount = 0, enchCount = 0, effCount = 0, containerCount = 0, lvliCount = 0, mgefCount = 0;
            foreach (var p in parsedPlugins)
            {
                armorCount += p.ArmorRows.Count;
                weaponCount += p.WeaponRows.Count;
                cobjCount += p.Cobjs.Count;
                condCount += p.Cobjs.Sum(c => c.ConditionRows.Count);
                enchCount += p.Enchantments.Count;
                effCount += p.Enchantments.Sum(e => e.EffectRows.Count);
                containerCount += p.Containers.Count;
                lvliCount += p.Containers.Sum(c => c.LvliRows.Count);
                mgefCount += p.MagicEffectRows.Count;
            }
            Debug.WriteLine($"[ItemDB] Parse phase: {parseSw.ElapsedMilliseconds} ms ({pluginFiles.Count} plugins, " +
                $"Armor={armorCount} Weapon={weaponCount} COBJ={cobjCount} COBJCond={condCount} " +
                $"Ench={enchCount} EnchEff={effCount} Container={containerCount} LVLI={lvliCount} MGEF={mgefCount})");

            // --- Flatten: gather every row across all plugins per table before writing ---
            // parsedPlugins is index-ordered (see above), so this walks plugins in exactly
            // allgamePathfromDB's given order — matching the original single-threaded loop's write
            // order, which is what "INSERT OR REPLACE" override resolution (last write for a given
            // key wins) depends on for records overridden by more than one plugin.
            //
            // COBJ/Enchantment/Container each own a set of child rows (Conditions/Effects+
            // WornRestrictionKeywords/LVLI). A Bethesda plugin override replaces a record as a whole
            // — including its child rows — not incrementally, so when more than one plugin touches
            // the same parent key, only the LAST plugin's full child set should survive. Naively
            // AddRange-ing every plugin's child rows here doesn't do that: for COBJ_Conditions (no
            // matching unique constraint on its columns, only a meaningless autoincrement Id) it
            // straight up duplicates every earlier plugin's conditions alongside the new ones; for
            // the other three tables (which do have a composite key matching child identity) it
            // still leaves rows behind that a later plugin's version had actually removed, since nothing
            // ever deletes a child row that simply stops reappearing. So COBJ/Enchantment/Container are
            // reduced to "last plugin per parent key" first, and only that plugin's child rows are used.
            var allArmor = new List<object[]>();
            var allWeapon = new List<object[]>();
            var allMagicEffects = new List<object[]>();
            var allGlobals = new List<object[]>();

            var latestCobjByKey = new Dictionary<string, ParsedCobj>();
            var latestEnchantmentByKey = new Dictionary<string, ParsedEnchantment>();
            var latestContainerByKey = new Dictionary<string, ParsedContainer>();
            var latestLeveledListByKey = new Dictionary<string, ParsedLeveledList>();

            foreach (var parsed in parsedPlugins)
            {
                allArmor.AddRange(parsed.ArmorRows);
                allWeapon.AddRange(parsed.WeaponRows);
                allMagicEffects.AddRange(parsed.MagicEffectRows);
                allGlobals.AddRange(parsed.GlobalRows);

                foreach (var cobj in parsed.Cobjs)
                    latestCobjByKey[(string)cobj.Values[0]] = cobj;

                foreach (var ench in parsed.Enchantments)
                    latestEnchantmentByKey[(string)ench.Values[0]] = ench;

                foreach (var container in parsed.Containers)
                    latestContainerByKey[(string)container.Values[0]] = container;

                foreach (var list in parsed.LeveledLists)
                    latestLeveledListByKey[(string)list.Values[0]] = list;
            }

            // What the overrides threw away.
            //
            // The loop above keeps one version per list - the last one in load order, which is the
            // one the game uses. Every earlier version has just been walked past and dropped, and
            // any item that only existed in one of those is now gone from the game with nothing
            // recording that it was ever there. See LeveledListLostEntry's schema comment for why
            // this is reported rather than repaired.
            //
            // Second pass rather than collecting during the first: the winner is only known once
            // every plugin has been seen, and parsedPlugins is already in memory, so walking it
            // again costs nothing and saves holding every version of every list.
            var lostEntriesByList = new Dictionary<string, Dictionary<string, object[]>>(StringComparer.OrdinalIgnoreCase);
            var winnerRefsByList = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var parsed in parsedPlugins)
            {
                foreach (var list in parsed.LeveledLists)
                {
                    string listKey = (string)list.Values[0];
                    if (!latestLeveledListByKey.TryGetValue(listKey, out var winner) || ReferenceEquals(winner, list))
                        continue;   // this plugin IS the winner - nothing of its own was lost

                    if (!winnerRefsByList.TryGetValue(listKey, out var winnerRefs))
                    {
                        winnerRefs = new HashSet<string>(
                            winner.EntryRows.Select(r => (string)r[2]), StringComparer.OrdinalIgnoreCase);
                        winnerRefsByList[listKey] = winnerRefs;
                    }

                    foreach (var row in list.EntryRows)
                    {
                        string reference = (string)row[2];
                        if (winnerRefs.Contains(reference))
                            continue;   // still in the list, possibly at another level - not lost

                        if (!lostEntriesByList.TryGetValue(listKey, out var lost))
                            lostEntriesByList[listKey] = lost = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);

                        // Later plugin wins the description: if two overrides both carried the item,
                        // the values from the one closest to the winner are the most relevant ones.
                        //
                        // Where those versions DISAGREE about level or count, that is a guess and
                        // the row is flagged as one. Putting such an entry back restores one of
                        // several states it genuinely had, and the window says so rather than
                        // presenting the pick as a reading - see LeveledListLostEntry's schema
                        // comment. Sticky: once ambiguous, always ambiguous, however many further
                        // versions agree afterwards.
                        bool ambiguous = lost.TryGetValue(reference, out var previous)
                                         && (!Equals(previous[2], row[3])
                                             || !Equals(previous[3], row[4])
                                             || Convert.ToInt32(previous[5]) == 1);

                        lost[reference] = new object[]
                        {
                            listKey, reference, row[3], row[4], parsed.PluginName, ambiguous ? 1 : 0,
                        };
                    }
                }
            }

            // Records whose ConditionsEdited/EffectsEdited/KeywordsEdited flag is set keep their
            // existing child rows untouched — excluded from both the rewrite-key lists (used for the
            // DELETE below) and the flattened row lists (so nothing gets added back either).
            var allCobj = new List<object[]>();
            var allCobjConditions = new List<object[]>();
            var cobjConditionRewriteKeys = new List<string>();
            foreach (var kv in latestCobjByKey)
            {
                allCobj.Add(kv.Value.Values);
                if (conditionsEditedKeys.Contains(kv.Key))
                    continue;
                cobjConditionRewriteKeys.Add(kv.Key);
                allCobjConditions.AddRange(kv.Value.ConditionRows);
            }

            var allEnchantments = new List<object[]>();
            var allEnchantmentEffects = new List<object[]>();
            var latestEffectRow = new Dictionary<(string EnchantmentKey, string MagicEffectKey), object[]>();
            var enchantmentEffectRewriteKeys = new List<string>();
            foreach (var kv in latestEnchantmentByKey)
            {
                allEnchantments.Add(kv.Value.Values);

                if (!effectsEditedKeys.Contains(kv.Key))
                {
                    enchantmentEffectRewriteKeys.Add(kv.Key);
                    // A single enchantment record's own Effects list can legitimately reference the
                    // same base MagicEffect more than once (e.g. two entries with different
                    // magnitude/duration) — this table has PRIMARY KEY(EnchantmentKey, MagicEffectKey),
                    // which can't represent that distinction. Dedupe here (last entry in the record's
                    // own list wins), matching what the previous INSERT OR REPLACE write path did
                    // silently instead of the plain INSERT crashing on the collision.
                    foreach (var row in kv.Value.EffectRows)
                        latestEffectRow[((string)row[0], (string)row[1])] = row;
                }
            }
            allEnchantmentEffects.AddRange(latestEffectRow.Values);

            // WornRestrictionKeywords rows for EVERY FLST (E3 — was previously only enchant-referenced
            // lists, populated inside the enchant loop). Last plugin per list key wins its whole
            // member set. A list the user has hand-edited (WornRestrictionListState.IsEdited=1, or the
            // legacy per-enchant flag) is skipped entirely so the scan never clobbers it.
            var allWornRestrictionKeywords = new List<object[]>();
            var wornRestrictionListKeysToRewrite = new List<string>();
            var latestFormListByKey = new Dictionary<string, ParsedFormList>();
            foreach (var parsed in parsedPlugins)
                foreach (var fl in parsed.FormLists)
                    latestFormListByKey[fl.ListKey] = fl;
            foreach (var kv in latestFormListByKey)
            {
                if (string.IsNullOrEmpty(kv.Key) || listContentEditedKeys.Contains(kv.Key))
                    continue;
                wornRestrictionListKeysToRewrite.Add(kv.Key);
                // FLSTs can legally list the same member twice; PRIMARY KEY(ListKey, KeywordKey)
                // can't. Keep first occurrence (matches the old INSERT-OR-REPLACE-silently behaviour).
                var seenMembers = new HashSet<string>();
                foreach (var row in kv.Value.MemberRows)
                    if (seenMembers.Add((string)row[1]))
                        allWornRestrictionKeywords.Add(row);
            }

            var allContainers = new List<object[]>();
            var allContainerLvli = new List<object[]>();
            var latestLvliRow = new Dictionary<(string ContainerKey, string LvliKey), object[]>();
            foreach (var container in latestContainerByKey.Values)
            {
                allContainers.Add(container.Values);
                // Same rationale as EnchantmentEffects above — ContainerLVLI has
                // PRIMARY KEY(ContainerKey, LVLiKey), but a container's own item list can list the same
                // leveled item more than once (e.g. duplicate entries from a patch).
                foreach (var row in container.LvliRows)
                    latestLvliRow[((string)row[0], (string)row[1])] = row;
            }
            allContainerLvli.AddRange(latestLvliRow.Values);

            // No dedupe here, unlike ContainerLVLI above: a container listing the same thing twice is
            // data, and Ordinal keeps the rows apart.
            var allContainerEntries = new List<object[]>();
            foreach (var container in latestContainerByKey.Values)
                allContainerEntries.AddRange(container.EntryRows);

            // Leveled lists: parent row plus its entries, taken only from the winning plugin's
            // version. No per-key dedupe of the entries, unlike ContainerLVLI above - here a repeated
            // reference is real data, and Ordinal keeps the rows apart.
            var allLeveledLists = new List<object[]>();
            var allLeveledListEntries = new List<object[]>();
            foreach (var list in latestLeveledListByKey.Values)
            {
                allLeveledLists.Add(list.Values);
                allLeveledListEntries.AddRange(list.EntryRows);
            }

            // --- Write phase: strictly sequential — do not parallelize SQLite writes ---
            var writeSw = Stopwatch.StartNew();

            // Child tables: delete existing rows for the parent keys we're about to rewrite, then
            // insert the freshly parsed set. Necessary because COBJ_Conditions has no unique
            // constraint to upsert against, and even for the two tables that do (EnchantmentEffects,
            // WornRestrictionKeywords) an upsert alone would never remove a row a later plugin's
            // version had actually dropped. Keys protected by an *Edited flag were already excluded
            // above, so their rows are never deleted or reinserted here.
            DeleteChildRowsForKeys(connection, transaction, "COBJ_Conditions", "COBJKey", cobjConditionRewriteKeys);
            DeleteChildRowsForKeys(connection, transaction, "EnchantmentEffects", "EnchantmentKey", enchantmentEffectRewriteKeys);
            DeleteChildRowsForKeys(connection, transaction, "WornRestrictionKeywords", "ListKey", wornRestrictionListKeysToRewrite.Distinct().ToList());
            // Container has no *Edited protection flag (it's a pure reference table), so every
            // scanned container's LVLI rows are simply cleared and rewritten each scan — otherwise a
            // rescan's insert collides with the previous scan's still-present rows for the same
            // (ContainerKey, LVLiKey), and a removed item would linger as a stale row.
            DeleteChildRowsForKeys(connection, transaction, "ContainerLVLI", "ContainerKey", latestContainerByKey.Keys.ToList());
            // Same reasoning for a leveled list: its entries are positional and have no identity, so
            // the only correct refresh is "drop this list's rows, insert the ones just parsed".
            // Without the delete, a rescan collides on (ListKey, Ordinal) and an entry the winning
            // plugin had removed would survive as a stale row.
            DeleteChildRowsForKeys(connection, transaction, "ContainerEntry", "ContainerKey", latestContainerByKey.Keys.ToList());
            DeleteChildRowsForKeys(connection, transaction, "LeveledListEntry", "ListKey", latestLeveledListByKey.Keys.ToList());
            // Same rewrite discipline as the entries above: this is scanned data, so the scan owns
            // it outright. Cleared for every list this scan saw, then refilled - a list whose
            // conflict was resolved by removing a mod must not keep reporting a loss.
            DeleteChildRowsForKeys(connection, transaction, "LeveledListLostEntry", "ListKey", latestLeveledListByKey.Keys.ToList());

            ExecuteRowsBatched(insertArmor, insertArmorBatch, ArmorParamNames, allArmor, BatchSize);
            ExecuteRowsBatched(insertWeapon, insertWeaponBatch, WeaponParamNames, allWeapon, BatchSize);
            ExecuteRowsBatched(insertCOBJ, insertCOBJBatch, CobjParamNames, allCobj, BatchSize);
            ExecuteRowsBatched(insertCOBJCondition, insertCOBJConditionBatch, CobjConditionParamNames, allCobjConditions, BatchSize);
            ExecuteRowsBatched(insertWRK, insertWRKBatch, WornRestrictionKeywordParamNames, allWornRestrictionKeywords, BatchSize);
            ExecuteRowsBatched(insertEnch, insertEnchBatch, EnchantmentParamNames, allEnchantments, BatchSize);
            ExecuteRowsBatched(insertEnchEff, insertEnchEffBatch, EnchantmentEffectParamNames, allEnchantmentEffects, BatchSize);
            ExecuteRowsBatched(insertContainer, insertContainerBatch, ContainerParamNames, allContainers, BatchSize);
            ExecuteRowsBatched(insertContainerLVLI, insertContainerLVLIBatch, ContainerLvliParamNames, allContainerLvli, BatchSize);
            ExecuteRowsBatched(insertContainerEntry, insertContainerEntryBatch, ContainerEntryParamNames, allContainerEntries, BatchSize);
            ExecuteRowsBatched(insertMagicEffects, insertMagicEffectsBatch, MagicEffectParamNames, allMagicEffects, BatchSize);
            ExecuteRowsBatched(insertGlobals, insertGlobalsBatch, GlobalParamNames, allGlobals, BatchSize);
            ExecuteRowsBatched(insertLeveledList, insertLeveledListBatch, LeveledListParamNames, allLeveledLists, BatchSize);
            ExecuteRowsBatched(insertLeveledListEntry, insertLeveledListEntryBatch, LeveledListEntryParamNames, allLeveledListEntries, BatchSize);
            ExecuteRowsBatched(insertLeveledListLost, insertLeveledListLostBatch, LeveledListLostParamNames,
                lostEntriesByList.Values.SelectMany(v => v.Values).ToList(), BatchSize);

            // Parent tables: anything not touched by this scan is no longer defined by any currently
            // active plugin — mark it inactive (hidden from Load*) instead of deleting, so its
            // IsEdited*/Original data survives in case the plugin comes back. COBJ additionally
            // excludes Original=0 (user-created recipes, which never appear in a scan's key-set by
            // definition — without this exemption every user recipe would be marked inactive on every
            // single scan, since nothing ever "scans" them).
            MarkInactiveExcept(connection, transaction, "Armor", "Key", allArmor.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "Weapons", "Key", allWeapon.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "COBJ", "Key", latestCobjByKey.Keys, extraWhere: "Original = 1");
            // Original = 1 only: a user-created enchantment exists in no plugin, so a scan must
            // never retire it. Same guard as COBJ above.
            MarkInactiveExcept(connection, transaction, "Enchantments", "Key", latestEnchantmentByKey.Keys, extraWhere: "Original = 1");
            MarkInactiveExcept(connection, transaction, "Container", "ContainerKey", latestContainerByKey.Keys);
            MarkInactiveExcept(connection, transaction, "MagicEffects", "Key", allMagicEffects.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "Globals", "Key", allGlobals.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "LeveledList", "Key", latestLeveledListByKey.Keys);

            writeSw.Stop();
            Debug.WriteLine($"[ItemDB] Write phase: {writeSw.ElapsedMilliseconds} ms");

            var commitSw = Stopwatch.StartNew();
            transaction.Commit();
            commitSw.Stop();
            Debug.WriteLine($"[ItemDB] Commit: {commitSw.ElapsedMilliseconds} ms");

            InvalidateCache();
        }

        private static HashSet<string> ReadFlaggedKeys(SqliteConnection connection, string sql)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                keys.Add(reader.GetString(0));
            return keys;
        }

        // Creates a fresh TEMP TABLE holding `keys` (one column, "Key") and returns its name. Batches
        // the inserts (same reasoning as BatchSize elsewhere in this file — one row per statement was
        // the original scan's dominant cost) so this stays fast even for tens of thousands of keys.
        // Caller is responsible for dropping the returned table when done with it.
        private static string PopulateKeyTempTable(SqliteConnection connection, SqliteTransaction transaction, string tempTableName, IReadOnlyList<string> keys)
        {
            using (var createCmd = connection.CreateCommand())
            {
                createCmd.Transaction = transaction;
                // COLLATE NOCASE, because every Key column this table is compared against is NOCASE.
                // Without it the comparison in MarkInactiveExcept runs BINARY (SQLite takes the left
                // operand.s collation, which is this column.s) and a key that differs only in casing
                // silently fails to match - which deactivates a record the scan had just written.
                createCmd.CommandText = $"CREATE TEMP TABLE {tempTableName} (Key TEXT PRIMARY KEY COLLATE NOCASE);";
                createCmd.ExecuteNonQuery();
            }

            const int chunkSize = 400;
            for (int i = 0; i < keys.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, keys.Count - i);
                var placeholders = string.Join(", ", Enumerable.Range(0, count).Select(r => $"(@k{r})"));

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"INSERT OR IGNORE INTO {tempTableName} (Key) VALUES {placeholders};";
                for (int r = 0; r < count; r++)
                    cmd.Parameters.AddWithValue($"@k{r}", keys[i + r]);
                cmd.ExecuteNonQuery();
            }

            return tempTableName;
        }

        private static void DeleteChildRowsForKeys(SqliteConnection connection, SqliteTransaction transaction, string table, string parentKeyColumn, IReadOnlyList<string> keys)
        {
            if (keys.Count == 0) return;

            var tempTable = PopulateKeyTempTable(connection, transaction, "_DeleteKeys", keys);
            try
            {
                using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = $"DELETE FROM {table} WHERE {parentKeyColumn} IN (SELECT Key FROM {tempTable});";
                deleteCmd.ExecuteNonQuery();
            }
            finally
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = $"DROP TABLE {tempTable};";
                dropCmd.ExecuteNonQuery();
            }
        }

        // Marks rows in `table` inactive when their key wasn't among `scannedKeys` — i.e. no currently
        // active plugin defines/touches them anymore, whether because the whole plugin was removed or
        // just that one record was dropped from an updated version of it. Verified against a real
        // SQLite instance before wiring in (temp table + NOT EXISTS, not a giant IN-list, to stay fast
        // at the tens-of-thousands-of-keys scale this app's scans run at).
        // internal for the regression test: the casing rule below is invisible in the SQL and cost a
        // record that had just been written (see ScanKeyCasingTests).
        internal static void MarkInactiveExcept(SqliteConnection connection, SqliteTransaction transaction, string table, string keyColumn, IEnumerable<string> scannedKeys, string extraWhere = null)
        {
            var keys = scannedKeys.Distinct().ToList();
            var tempTable = PopulateKeyTempTable(connection, transaction, "_ActiveKeys", keys);
            try
            {
                var where = $"Active = 1 AND NOT EXISTS (SELECT 1 FROM {tempTable} WHERE {tempTable}.Key = {table}.{keyColumn})";
                if (!string.IsNullOrEmpty(extraWhere))
                    where += $" AND {extraWhere}";

                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = $"UPDATE {table} SET Active = 0 WHERE {where};";
                updateCmd.ExecuteNonQuery();
            }
            finally
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = $"DROP TABLE {tempTable};";
                dropCmd.ExecuteNonQuery();
            }
        }

        private sealed class ParsedPluginData
        {
            // Which file these rows came from. Needed for the lost-entry pass, which has to say
            // WHERE an entry was last seen - and to tell a plugin.s own version of a list from the
            // one that won.
            public string PluginName = "";

            public List<object[]> ArmorRows = new();
            public List<object[]> WeaponRows = new();
            public List<ParsedCobj> Cobjs = new();
            public List<ParsedEnchantment> Enchantments = new();
            public List<ParsedContainer> Containers = new();
            public List<object[]> MagicEffectRows = new();
            public List<object[]> GlobalRows = new();
            public List<ParsedFormList> FormLists = new();
            public List<ParsedLeveledList> LeveledLists = new();

        }

        // E3: every FLST in the plugin (not just enchant-referenced ones) and its members. Feeds the
        // WornRestrictionKeywords table, which E3 treats as a general "FLST contents" table.
        private sealed class ParsedFormList
        {
            public string ListKey;
            public List<object[]> MemberRows = new();   // { ListKey, MemberKey }
        }

        private sealed class ParsedCobj
        {
            public object[] Values;
            public List<object[]> ConditionRows = new();
        }

        private sealed class ParsedEnchantment
        {
            public object[] Values;
            public List<object[]> EffectRows = new();
        }

        private sealed class ParsedContainer
        {
            public object[] Values;
            public List<object[]> LvliRows = new();
            public List<object[]> EntryRows = new();   // every entry, not just the leveled lists
        }

        // A leveled list plus its entries. Same parent/child shape as ParsedContainer, and for the
        // same reason: only the winning plugin's version of a list may contribute entries, or a
        // later plugin that REMOVED an entry would leave the earlier one behind forever.
        private sealed class ParsedLeveledList
        {
            public object[] Values;
            public List<object[]> EntryRows = new();
        }

        private static readonly string[] ArmorParamNames =
            { "@key", "@editorID", "@name", "@weight", "@val", "@armorRating", "@slotMask", "@armorType", "@keywords", "@objectEffect" };
        private static readonly string[] WeaponParamNames =
            { "@key", "@editorID", "@name", "@weight", "@val", "@dmg", "@speed", "@reach", "@stagger", "@keywords", "@objectEffect" };
        private static readonly string[] CobjParamNames =
            { "@key", "@name", "@createdItem", "@workbench", "@ingredients" };
        private static readonly string[] CobjConditionParamNames =
            { "@cobjKey", "@extra", "@runOn", "@type", "@target", "@value", "@op", "@flags" };
        private static readonly string[] EnchantmentParamNames =
            { "@key", "@editorID", "@name", "@cast", "@target", "@cost", "@wrestr", "@baseEnch",
              "@enchType", "@flags", "@chargeTime", "@enchAmount" };
        private static readonly string[] EnchantmentEffectParamNames =
            { "@ench", "@mgef", "@editorID", "@name", "@mag", "@dur", "@area" };
        private static readonly string[] WornRestrictionKeywordParamNames = { "@list", "@kw" };
        private static readonly string[] ContainerParamNames = { "@key", "@name" };
        private static readonly string[] ContainerLvliParamNames = { "@containerKey", "@lvliKey", "@lvliName" };
        private static readonly string[] ContainerEntryParamNames = { "@containerKey", "@ordinal", "@reference", "@count" };
        private static readonly string[] LeveledListParamNames = { "@key", "@editorID", "@chanceNone", "@flags", "@globalKey" };
        private static readonly string[] LeveledListEntryParamNames = { "@listKey", "@ordinal", "@reference", "@level", "@count" };
        private static readonly string[] LeveledListLostParamNames = { "@listKey", "@reference", "@level", "@count", "@lostFrom", "@ambiguous" };
        private static readonly string[] MagicEffectParamNames =
            { "@key", "@editorID", "@name", "@hasMag", "@hasDur", "@hasAre", "@castType", "@targetType" };
        private static readonly string[] GlobalParamNames = { "@key", "@editorID", "@value" };

        // Real table column names, in the same order as the matching *ParamNames array above.
        // Several of the single-row PrepareInsertX commands bind a parameter name that doesn't match
        // its column name (e.g. column "Value" <- @val, "BodySlotMask" <- @slotMask) — PrepareBatchInsert
        // needs the actual column names for its generated INSERT column list; deriving them from the
        // param names (stripping "@") is wrong wherever they differ and was the cause of the
        // "table Armor has no column named val" crash.
        internal static readonly string[] ArmorColumnNames =
            { "Key", "EditorID", "Name", "Weight", "Value", "ArmorRating", "BodySlotMask", "ArmorType", "Keywords", "ObjectEffectKey" };
        internal static readonly string[] WeaponColumnNames =
            { "Key", "EditorID", "Name", "Weight", "Value", "Damage", "Speed", "Reach", "Stagger", "Keywords", "ObjectEffectKey" };
        internal static readonly string[] CobjColumnNames =
            { "Key", "Name", "CreatedItem", "WorkbenchKeyword", "Ingredients" };
        private static readonly string[] CobjConditionColumnNames =
            { "COBJKey", "Extra", "RunOn", "ConditionType", "Target", "Value", "CompareOperator", "Flags" };
        internal static readonly string[] EnchantmentColumnNames =
            { "Key", "EditorID", "Name", "CastType", "TargetType", "EnchantmentCost", "WornRestrictionListKey", "BaseEnchantmentKey",
              "EnchantType", "Flags", "ChargeTime", "EnchantmentAmount" };
        private static readonly string[] EnchantmentEffectColumnNames =
            { "EnchantmentKey", "MagicEffectKey", "EditorID", "Name", "Magnitude", "Duration", "Area" };
        private static readonly string[] WornRestrictionKeywordColumnNames = { "ListKey", "KeywordKey" };
        internal static readonly string[] ContainerColumnNames = { "ContainerKey", "Name" };
        private static readonly string[] ContainerLvliColumnNames = { "ContainerKey", "LVLiKey", "LVLiName" };
        private static readonly string[] ContainerEntryColumnNames = { "ContainerKey", "Ordinal", "Reference", "Count" };
        private static readonly string[] LeveledListColumnNames = { "Key", "EditorID", "ChanceNone", "Flags", "GlobalKey" };
        private static readonly string[] LeveledListEntryColumnNames = { "ListKey", "Ordinal", "Reference", "Level", "Count" };
        private static readonly string[] LeveledListLostColumnNames = { "ListKey", "Reference", "Level", "Count", "LostFrom", "Ambiguous" };
        internal static readonly string[] MagicEffectColumnNames =
            { "Key", "EditorID", "Name", "HasMagnitude", "HasDuration", "HasArea", "CastType", "TargetType" };
        private static readonly string[] GlobalColumnNames = { "Key", "EditorID", "Value" };

        private static void ApplyRowAndExecute(SqliteCommand cmd, string[] paramNames, object[] values)
        {
            for (int i = 0; i < paramNames.Length; i++)
                cmd.Parameters[paramNames[i]].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        // Rows per batch statement. Bounded so that even the widest table (Weapon, 10 columns) stays
        // at 900 bound parameters — comfortably under SQLite's historical 999 host-parameter limit —
        // and 90 rows is well under the ~500-row limit for a multi-row VALUES list.
        private const int BatchSize = 90;

        // Plain multi-row INSERT (no OR REPLACE / no ON CONFLICT) for the 4 child tables
        // (COBJ_Conditions/EnchantmentEffects/WornRestrictionKeywords/ContainerLVLI). PutIntoDataBank
        // always DELETEs the parent keys it's about to rewrite children for first (see
        // DeleteChildRowsForKeys), so a plain insert never conflicts — and if it somehow did, that
        // would mean a real bug, which a plain INSERT surfaces as an error instead of silently
        // replacing/duplicating rows the way "OR REPLACE" used to.
        private static SqliteCommand PrepareInsertBatch(SqliteConnection connection, string table, string[] columnNames, string[] paramNames, int batchSize)
        {
            var columns = string.Join(", ", columnNames);

            var rowGroups = new string[batchSize];
            for (int r = 0; r < batchSize; r++)
                rowGroups[r] = "(" + string.Join(", ", paramNames.Select(p => $"{p}_{r}")) + ")";

            var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({columns}) VALUES {string.Join(", ", rowGroups)}";

            for (int r = 0; r < batchSize; r++)
                foreach (var p in paramNames)
                    cmd.Parameters.Add(new SqliteParameter($"{p}_{r}", DBNull.Value));

            return cmd;
        }

        // Multi-row UPSERT for the 6 "parent" tables (Armor/Weapons/COBJ/Enchantments/Container/
        // MagicEffects) — columnNames/paramNames only ever list the scanned base columns (never
        // IsEdited*/Original/*Edited), so the ON CONFLICT DO UPDATE clause built from them can never
        // touch those columns. That's the whole point: a rescan updates the plugin-derived data and
        // flips Active back to 1, but never overwrites a user's manual edits. columnNames[0] is always
        // that table's natural key (Key, or ContainerKey for Container) and is used as the conflict
        // target. Verified against a real SQLite instance (3.49.1, same as this app) before wiring in.
        private static SqliteCommand PrepareUpsertBatch(SqliteConnection connection, string table, string[] columnNames, string[] paramNames, int batchSize)
        {
            var columns = string.Join(", ", columnNames) + ", Active";
            var updateSet = string.Join(", ", columnNames.Skip(1).Select(c => $"{c} = excluded.{c}")) + ", Active = 1";

            var rowGroups = new string[batchSize];
            for (int r = 0; r < batchSize; r++)
                rowGroups[r] = "(" + string.Join(", ", paramNames.Select(p => $"{p}_{r}")) + ", 1)";

            var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {table} ({columns}) VALUES {string.Join(", ", rowGroups)} " +
                $"ON CONFLICT({columnNames[0]}) DO UPDATE SET {updateSet}";

            for (int r = 0; r < batchSize; r++)
                foreach (var p in paramNames)
                    cmd.Parameters.Add(new SqliteParameter($"{p}_{r}", DBNull.Value));

            return cmd;
        }

        // Writes `rows` using `batchCmd` (BatchSize rows per ExecuteNonQuery) for as many full
        // batches as fit, then falls back to `singleCmd` (the plain one-row-per-call command) for
        // the remainder — avoids re-preparing a differently-sized batch statement for the tail.
        private static void ExecuteRowsBatched(SqliteCommand singleCmd, SqliteCommand batchCmd, string[] paramNames, List<object[]> rows, int batchSize)
        {
            int i = 0;
            for (; i + batchSize <= rows.Count; i += batchSize)
            {
                for (int r = 0; r < batchSize; r++)
                {
                    var row = rows[i + r];
                    for (int c = 0; c < paramNames.Length; c++)
                        batchCmd.Parameters[$"{paramNames[c]}_{r}"].Value = row[c] ?? DBNull.Value;
                }
                batchCmd.ExecuteNonQuery();
            }

            for (; i < rows.Count; i++)
                ApplyRowAndExecute(singleCmd, paramNames, rows[i]);
        }

        // An unset link is an empty string, never the text form of FormKey.Null. That distinction was
        // paid for once already: an FLST-less enchantment stored "Null|000000", ~1100 records ended
        // up sharing that value, and editing one of them touched them all.
        private static string LinkKey(FormKey formKey) =>
            formKey.IsNull ? "" : KeyFactory.BuildMasterKey(formKey);

        // Pure parsing — no DB access — so this is safe to call concurrently from Parallel.ForEach.
        // Mirrors the original single-threaded loop body exactly, just capturing values into rows
        // instead of writing straight to a shared SqliteCommand's parameters.
        private ParsedPluginData ParsePluginForItemDB(string pluginName, string fullPath)
        {
            var result = new ParsedPluginData { PluginName = pluginName };
            var mod = SkyrimMod.CreateFromBinaryOverlay(
                fullPath, SkyrimRelease.SkyrimSE, Services.PluginReadParams.ForScan());

            // ARMOR
            foreach (var armor in mod.Armors.Records)
            {
                string key = KeyFactory.BuildMasterKey(armor.FormKey);

                // BodySlotMask (bitmask) — direct typed access, no reflection
                uint slotMask = (uint)(armor.BodyTemplate?.FirstPersonFlags ?? 0);

                // BOD2 ArmorType - the actual Light/Heavy/Clothing switch. NOT the ArmorLight
                // keyword, which only drives perks, material and vendors: an item with every armor
                // keyword stays clothing (no armor rating shown, cannot be tempered) until this is
                // set. Empty when a record has no BOD2 at all; no vanilla ARMO does, but mods vary.
                string armorType = armor.BodyTemplate?.ArmorType.ToString() ?? "";

                var kw = (armor.Keywords?
                    .Select(k =>
                    {
                        var fk = k.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}";
                    })
                    ?? Enumerable.Empty<string>())
                    .ToList();

                // Override-order trace, disabled — re-enable (and adjust the keyword suffixes) if a
                // similar load-order investigation is needed again.
                //if (kw.Any(k => k.EndsWith("6BBD9", StringComparison.OrdinalIgnoreCase)
                //              || k.EndsWith("6BBE6", StringComparison.OrdinalIgnoreCase)))
                //{
                //    Debug.WriteLine($"[TRACE Armor] plugin={pluginName} key={key} editorID={armor.EditorID} keywords={string.Join(",", kw)}");
                //}

                result.ArmorRows.Add(new object[]
                {
                    key,
                    armor.EditorID ?? "",
                    armor.Name?.ToString() ?? "",
                    (float?)armor.Weight ?? 0f,
                    (int?)armor.Value ?? 0,
                    (float?)armor.ArmorRating ?? 0f,
                    (long)slotMask,
                    armorType,
                    string.Join(",", kw),
                    // Which enchantment the record wears. Empty rather than a null key when it has
                    // none: "no enchantment" is a value the editor has to be able to show and set.
                    armor.ObjectEffect.FormKey.IsNull ? "" : KeyFactory.BuildMasterKey(armor.ObjectEffect.FormKey)
                });
            }

            // WEAPONS
            foreach (var weap in mod.Weapons.Records)
            {
                string key = KeyFactory.BuildMasterKey(weap.FormKey);

                var kw = weap.Keywords?
                    .Select(k =>
                    {
                        var fk = k.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}";
                    })
                    ?? Enumerable.Empty<string>();

                result.WeaponRows.Add(new object[]
                {
                    key,
                    weap.EditorID ?? "",
                    weap.Name?.ToString() ?? "",
                    (weap.BasicStats?.Weight) ?? 0f,
                    (int?)weap.BasicStats?.Value ?? 0,
                    weap.BasicStats?.Damage ?? 0,
                    weap.Data?.Speed ?? 0f,
                    weap.Data?.Reach ?? 0f,
                    weap.Data?.Stagger ?? 0f,
                    string.Join(",", kw),
                    weap.ObjectEffect.FormKey.IsNull ? "" : KeyFactory.BuildMasterKey(weap.ObjectEffect.FormKey)
                });
            }

            // COBJ
            foreach (var cobj in mod.ConstructibleObjects.Records)
            {
                string key = KeyFactory.BuildMasterKey(cobj.FormKey);
                string createdKey = KeyFactory.BuildMasterKey(cobj.CreatedObject.FormKey);

                string workbench = "";
                if (cobj.WorkbenchKeyword != null)
                {
                    var fk = cobj.WorkbenchKeyword.FormKey;
                    workbench = $"{fk.ModKey.FileName}|{fk.IDString()}";
                }

                var ingredients = cobj.Items?
                    .Select(e =>
                    {
                        var fk = e.Item.Item.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}*{e.Item.Count}";
                    })
                    ?? Enumerable.Empty<string>();

                var parsedCobj = new ParsedCobj
                {
                    Values = new object[] { key, cobj.EditorID ?? "", createdKey, workbench, string.Join(",", ingredients) }
                };

                // --------------------------------
                // Conditions → COBJ_Conditions
                // --------------------------------
                foreach (var cond in cobj.Conditions)
                {
                    // ConditionFloat compares against a fixed float value, ConditionGlobal compares
                    // against a Global. Both get produced by ESP/ESL/ESM files, so we need to read
                    // both. When reading from plugin files, Mutagen returns overlay types (e.g.
                    // ConditionFloatBinaryOverlay) that only implement the getter interfaces, not the
                    // concrete classes. So match against the interfaces instead of against
                    // ConditionFloat/ConditionGlobal.
                    IConditionDataGetter data;
                    string comparisonValue;

                    if (cond is IConditionFloatGetter cf)
                    {
                        data = cf.Data;
                        comparisonValue = cf.ComparisonValue.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (cond is IConditionGlobalGetter cg)
                    {
                        data = cg.Data;
                        var globalFk = cg.ComparisonValue.FormKey;
                        comparisonValue = !globalFk.IsNull
                            ? $"{globalFk.ModKey.FileName}|{globalFk.IDString()}"
                            : "";
                    }
                    else
                    {
                        Debug.WriteLine($"Unknown condition wrapper: {cond.GetType().Name}");
                        continue;
                    }

                    // The comparison operator and the flags used to be guessed at ESP build time.
                    // The OR flag has no safe default: rebuilding an OR-chained pair as AND turns
                    // "either perk" into "both perks" and the recipe vanishes from the crafting menu.
                    string op = cond.CompareOperator.ToString();
                    string flags = cond.Flags == 0 ? "" : cond.Flags.ToString().Replace(" ", "");
                    string runOn = data.RunOnType.ToString();
                    string extra = "";

                    // Single-FormLink parameter, shared by most of the types below.
                    static string LinkKey<T>(IFormLinkOrIndexGetter<T> link)
                        where T : class, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter
                    {
                        if (!link.UsesLink()) return "";
                        var fk = link.Link.FormKey;
                        return fk.IsNull ? "" : $"{fk.ModKey.FileName}|{fk.IDString()}";
                    }

                    void Add(string type, string target, string value) =>
                        parsedCobj.ConditionRows.Add(new object[]
                            { key, extra, runOn, type, target, value, op, flags });

                    // ---- editable types (each has a ViewModel + XAML template) ----

                    if (data is IHasPerkConditionDataGetter perkData)
                    {
                        Add("HasPerk", LinkKey(perkData.Perk), comparisonValue);
                        continue;
                    }

                    if (data is IGetIsSexConditionDataGetter sexData)
                    {
                        Add("GetIsSex", sexData.MaleFemaleGender.ToString(), comparisonValue);
                        continue;
                    }

                    if (data is IGetActorValueConditionDataGetter avData)
                    {
                        Add("GetActorValue", avData.ActorValue.ToString(), comparisonValue);
                        continue;
                    }

                    if (data is IGetLevelConditionDataGetter)
                    {
                        Add("GetLevel", "", comparisonValue); // no Target
                        continue;
                    }

                    if (data is IGetStageDoneConditionDataGetter stageData)
                    {
                        Add("GetStageDone", LinkKey(stageData.Quest), stageData.Stage.ToString());
                        continue;
                    }

                    // ---- read-only types: stored and rebuilt verbatim, not editable ----
                    //
                    // These used to be dropped on the floor. Measured against the vanilla masters
                    // that discarded 69% of all COBJ conditions, and left 556 recipes with none at
                    // all - an override of one of those became craftable unconditionally.
                    // EPTemperingItemIsEnchanted is the Arcane Blacksmith gate for tempering
                    // enchanted gear and occurs 523 times on its own.

                    if (data is IGetItemCountConditionDataGetter itemCount)
                    {
                        Add("GetItemCount", LinkKey(itemCount.ItemOrList), comparisonValue);
                        continue;
                    }

                    if (data is IEPTemperingItemIsEnchantedConditionDataGetter)
                    {
                        Add("EPTemperingItemIsEnchanted", "", comparisonValue); // no parameter
                        continue;
                    }

                    if (data is IGetGlobalValueConditionDataGetter globalValue)
                    {
                        Add("GetGlobalValue", LinkKey(globalValue.Global), comparisonValue);
                        continue;
                    }

                    if (data is IHasSpellConditionDataGetter hasSpell)
                    {
                        Add("HasSpell", LinkKey(hasSpell.Spell), comparisonValue);
                        continue;
                    }

                    if (data is IHasKeywordConditionDataGetter hasKeyword)
                    {
                        Add("HasKeyword", LinkKey(hasKeyword.Keyword), comparisonValue);
                        continue;
                    }

                    if (data is IGetQuestCompletedConditionDataGetter questDone)
                    {
                        Add("GetQuestCompleted", LinkKey(questDone.Quest), comparisonValue);
                        continue;
                    }

                    if (data is IGetInCurrentLocConditionDataGetter inLoc)
                    {
                        Add("GetInCurrentLoc", LinkKey(inLoc.Location), comparisonValue);
                        continue;
                    }

                    if (data is IGetVMQuestVariableConditionDataGetter vmVar)
                    {
                        extra = vmVar.VariableName ?? "";
                        Add("GetVMQuestVariable", LinkKey(vmVar.Quest), comparisonValue);
                        continue;
                    }

                    // ---- anything else ----
                    //
                    // Recorded rather than silently discarded, so the UI can show that the recipe
                    // has conditions the tool doesn't understand and CobjEspBuilder knows never to
                    // rewrite this recipe's condition list. Only the function name survives.
                    var fn = data.GetType().Name
                        .Replace("ConditionDataBinaryOverlay", "")
                        .Replace("ConditionData", "");
                    Add(ConditionCatalog.UnsupportedPrefix + fn, "", comparisonValue);
                }

                result.Cobjs.Add(parsedCobj);
            }

            // ENCHANTMENTS
            foreach (var ench in mod.ObjectEffects.Records)
            {
                // Master-correct, like every other record key in this scan (see the FLST block
                // below for the same rule spelled out). It used to be the name of the plugin
                // being READ, and an override therefore produced a SECOND row instead of
                // replacing the record: measured against the real load order, 117 enchantments
                // and 542 magic effects existed two or three times over, and the copies
                // disagreed - USSEP adds the BaseEnchantment link that Bethesda left off the
                // Articulation tiers, so the same enchantment showed a base on one row and none
                // on the other. Reported by the user as "die Kinder werden nicht als Kinder
                // erkannt". Every LINK into this table was master-keyed all along
                // (Armor.ObjectEffectKey, BaseEnchantmentKey, EnchantmentEffects.MagicEffectKey),
                // so only the row keys were out of step.
                string enchKey = KeyFactory.BuildMasterKey(ench.FormKey);
                var parsedEnch = new ParsedEnchantment();

                // WornRestrictions (FLST). The FormLink is non-null even when it points at nothing —
                // guard on FormKey.IsNull or every FLST-less enchantment gets listKey "Null|000000"
                // (a value ~1100 of them then share, so one keyword edit mass-marks them all).
                // E3: only the *pointer* is recorded here; the list's member rows come from the
                // dedicated FLST loop below (all FLSTs, not just enchant-referenced ones).
                string listKey = "";
                if (ench.WornRestrictions != null && !ench.WornRestrictions.FormKey.IsNull)
                {
                    var fk = ench.WornRestrictions.FormKey;
                    listKey = $"{fk.ModKey.FileName}|{fk.IDString()}";
                }

                // BaseEnchantment (ENCH the ObjectEffect inherits from — set on magnitude/duration
                // tier variants). Same non-null-FormLink caveat as WornRestrictions: guard on
                // FormKey.IsNull. Read-only — a tag only, never a shadow column.
                string baseEnchKey = "";
                if (ench.BaseEnchantment != null && !ench.BaseEnchantment.FormKey.IsNull)
                {
                    var bfk = ench.BaseEnchantment.FormKey;
                    baseEnchKey = $"{bfk.ModKey.FileName}|{bfk.IDString()}";
                }

                parsedEnch.Values = new object[]
                {
                    enchKey,
                    ench.EditorID ?? "",
                    ench.Name?.ToString() ?? "",
                    ench.CastType.ToString(),
                    ench.TargetType.ToString(),
                    (float)ench.EnchantmentCost,
                    listKey,
                    baseEnchKey,
                    ench.EnchantType.ToString(),
                    // The raw dword, not the enum's ToString - see the Enchantments schema comment.
                    (int)ench.Flags,
                    ench.ChargeTime,
                    ench.EnchantmentAmount
                };

                // Effects
                foreach (var eff in ench.Effects)
                {
                    var fk = eff.BaseEffect.FormKey;
                    string mgefKey = $"{fk.ModKey.FileName}|{fk.IDString()}";

                    string editorid = "";
                    string name = "";

                    // MagicEffect
                    if (mod.MagicEffects.TryGetValue(fk, out var magicEffect))
                    {
                        editorid = magicEffect.EditorID ?? "";
                        name = magicEffect.Name?.ToString() ?? "";
                    }

                    parsedEnch.EffectRows.Add(new object[]
                    {
                        enchKey, mgefKey, editorid, name,
                        eff.Data?.Magnitude ?? 0,
                        eff.Data?.Duration ?? 0,
                        eff.Data?.Area ?? 0
                    });
                }

                result.Enchantments.Add(parsedEnch);
            }

            // FORMLISTS — every FLST defined or overridden in this plugin, with its members. The
            // key is master-correct (fl.FormKey.ModKey), so a Skyrim.esm FLST overridden in a patch
            // produces rows under the Skyrim.esm key from whichever plugin last defines it; the
            // write phase keeps the last (winning) plugin's member set per list key.
            foreach (var fl in mod.FormLists.Records)
            {
                string flKey = $"{fl.FormKey.ModKey.FileName}|{fl.FormKey.IDString()}";
                var pf = new ParsedFormList { ListKey = flKey };
                foreach (var entry in fl.Items)
                {
                    var mfk = entry.FormKey;
                    pf.MemberRows.Add(new object[] { flKey, $"{mfk.ModKey.FileName}|{mfk.IDString()}" });
                }
                result.FormLists.Add(pf);
            }

            // CONTAINER + LVLI
            foreach (var container in mod.Containers.Records)
            {
                string containerKey = KeyFactory.BuildMasterKey(container.FormKey);
                var parsedContainer = new ParsedContainer
                {
                    Values = new object[] { containerKey, container.EditorID ?? "" }
                };

                // LVLI inside Container
                if (container.Items != null)
                {
                    int ordinal = 0;
                    foreach (var entry in container.Items)
                    {
                        var fk = entry.Item.Item.FormKey;
                        string refKey = KeyFactory.BuildMasterKey(fk);

                        // Every entry, whatever it points at. Only about a quarter of a container's
                        // entries are leveled lists; the rest are items, and those are exactly what
                        // "where does this item already appear" has to be able to find.
                        parsedContainer.EntryRows.Add(new object[]
                        {
                            containerKey, ordinal++, refKey, entry.Item.Count,
                        });

                        // Check whether the LVLI exists
                        var lvliRecord = _formIDDB.GetByKey(refKey);
                        if (lvliRecord != null && lvliRecord.Type == "LVLi")
                        {
                            parsedContainer.LvliRows.Add(new object[] { containerKey, refKey, lvliRecord.Name });
                        }
                    }
                }

                result.Containers.Add(parsedContainer);
            }

            // LEVELED LISTS (the lists themselves, with their contents)
            //
            // The Container block above only records WHICH leveled lists hang in a container. This
            // records what is IN those lists - which is what answers "where does this item already
            // appear" and "what is in this list" without opening xEdit.
            //
            // Entries are stored positionally: an entry has no identity of its own, and in the real
            // load order 3,982 of them share (list, reference, level) with a sibling. Keying on the
            // reference would merge those and silently change the odds the list rolls.
            foreach (var list in mod.LeveledItems.Records)
            {
                string listKey = KeyFactory.BuildMasterKey(list.FormKey);

                var parsedList = new ParsedLeveledList
                {
                    Values = new object[]
                    {
                        listKey,
                        list.EditorID ?? "",
                        // Stored as 0-100, which is what SkyPatcher's chanceNone= expects and what
                        // xEdit shows. Mutagen's Noggog.Percent holds a 0-1 double, so a plain (int)
                        // cast silently floors every list below 100% to zero - 512 lists in
                        // Skyrim.esm have a chance-none, and only 61 survived that cast.
                        (int)Math.Round(list.ChanceNone.Value * 100),
                        // Flags as their names rather than a bit value: the patcher addresses them by
                        // name too (calcForLevel, calcEachItem, ...), and a stored number would have
                        // to be translated on both sides.
                        list.Flags == 0 ? "" : list.Flags.ToString(),
                        list.Global.FormKey.IsNull ? "" : KeyFactory.BuildMasterKey(list.Global.FormKey),
                    },
                };

                if (list.Entries != null)
                {
                    int ordinal = 0;
                    foreach (var entry in list.Entries)
                    {
                        var data = entry.Data;
                        if (data == null) continue;   // an entry with no payload carries nothing to show

                        parsedList.EntryRows.Add(new object[]
                        {
                            listKey,
                            ordinal++,
                            KeyFactory.BuildMasterKey(data.Reference.FormKey),
                            (int)data.Level,
                            (int)data.Count,
                        });
                    }
                }

                result.LeveledLists.Add(parsedList);
            }

            // GLOBALS
            //
            // Only interesting because a leveled list can take its chance-none from one instead of
            // from a fixed number: 223 lists in the real load order do, across 125 different globals,
            // and without a name they are unpickable and unreadable ("Skyrim.esm|0F258D" tells the
            // user nothing).
            //
            // The VALUE is scanned too, and that matters for the calculator: a list with a global has
            // no fixed chance at all, so the only honest number to compute with is what the global
            // holds right now in the load order. It is a starting value - quests and scripts change
            // it while playing - which is why the window says where the number came from.
            //
            // GLOB is one record with three payload shapes (float, int, short). Mutagen models them
            // as separate types, so the value is read through the getter interfaces rather than a
            // common property - there is none.
            foreach (var glob in mod.Globals.Records)
            {
                float? value = glob switch
                {
                    IGlobalFloatGetter f => f.Data,
                    IGlobalIntGetter i => i.Data,
                    IGlobalShortGetter s => s.Data,
                    _ => null,
                };

                result.GlobalRows.Add(new object[]
                {
                    KeyFactory.BuildMasterKey(glob.FormKey),
                    glob.EditorID ?? "",
                    // NULL rather than 0 for a shape this scan does not know: 0 is a legitimate
                    // chance ("always rolls something") and must not be invented.
                    value.HasValue ? (object)(double)value.Value : DBNull.Value,
                });
            }

            // MAGIC EFFECTS
            foreach (var mgef in mod.MagicEffects.Records)
            {
                // Master-correct — same fix and the same reason as the enchantment key above.
                string mgefKey = KeyFactory.BuildMasterKey(mgef.FormKey);

                bool hasMagnitude = !mgef.Flags.HasFlag(MagicEffect.Flag.NoMagnitude);
                bool hasDuration = !mgef.Flags.HasFlag(MagicEffect.Flag.NoDuration);
                int hasArea = (mgef.TargetType == TargetType.Aimed || mgef.TargetType == TargetType.TargetLocation) ? 1 : 0;

                result.MagicEffectRows.Add(new object[]
                {
                    mgefKey,
                    mgef.EditorID ?? "",
                    mgef.Name?.ToString() ?? "",
                    hasMagnitude ? 1 : 0,
                    hasDuration ? 1 : 0,
                    hasArea,
                    mgef.CastType.ToString(),
                    mgef.TargetType.ToString()
                });
            }


            return result;
        }
        public struct ParsedIngredient
        {
            public string Plugin;
            public string FormID;
            public int Count;
        }
        public static ParsedIngredient ParseIngredient(string raw)
        {
            // raw = "Skyrim.esm|05ACE5*2"

            var parts = raw.Split('*');
            string key = parts[0];          // "Skyrim.esm|05ACE5"
            int count = parts.Length > 1 ? int.Parse(parts[1]) : 1;

            var keyParts = key.Split('|');
            string plugin = keyParts[0];
            string formID = keyParts[1];

            return new ParsedIngredient
            {
                Plugin = plugin,
                FormID = formID,
                Count = count
            };
        }
    }
}
