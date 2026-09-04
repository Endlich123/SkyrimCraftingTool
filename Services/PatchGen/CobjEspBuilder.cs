using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed class CobjEspResult
    {
        public int NewCount { get; set; }
        public int OverrideCount { get; set; }
        public int SkippedCount { get; set; }

        // Overrides deep-copied from the winning record in the load order (good) vs. assembled from
        // scratch because the record wasn't found (lossy - see the WinningRecordResolver class note).
        public int DeepCopiedCount { get; set; }
        public int FromScratchCount { get; set; }

        // Recipes whose edited condition set was NOT written, because the real record carries
        // conditions the scan cannot represent and rewriting would have deleted them.
        public int ConditionRewriteSkippedCount { get; set; }

        // Recipes whose stored conditions are missing entries the real record has - the hallmark of
        // an item.db written before the scan understood those condition types. Needs a rescan.
        public int StaleConditionDataCount { get; set; }

        // ENCH records overridden because their worn-restriction FLST assignment changed. SkyPatcher
        // has no operation for that field, so the ESP is the only route - see E-P4 in
        // docs/EnchantmentPatch-Plan.md.
        public int EnchantmentOverrideCount { get; set; }

        public bool EslFlagged { get; set; }
        public string OutputPath { get; set; } = "";
        public IReadOnlyList<string> Masters { get; set; } = Array.Empty<string>();
        public List<string> Warnings { get; } = new();
    }

    // Builds SkyrimCraftingTool.esp from CobjPatchEntry[]: new ConstructibleObject records for
    // tool-created recipes (compact 0x800+ FormIDs via PatchFormIdMapStore) and overrides for
    // edited master recipes.
    //
    // Overrides are DEEP COPIES of the winning record whenever a WinningRecordResolver is supplied,
    // with only the tracked fields written over the top. Building them from scratch (the old
    // behaviour, still the fallback when the record can't be found) defaults every untracked field -
    // most damagingly the conditions, of which the scan understands only about a third.
    public sealed class CobjEspBuilder
    {
        private static IConditionDataGetter? DataOf(IConditionGetter cond) => cond switch
        {
            IConditionFloatGetter cf => cf.Data,
            IConditionGlobalGetter cg => cg.Data,
            _ => null,
        };

        // The five types with an editor in the UI. Everything else is preserved but read-only, and
        // so can neither be added nor removed by the user - which is what makes a count mismatch a
        // reliable "this database is stale" signal.
        private static bool IsEditableCondition(IConditionGetter cond)
        {
            var d = DataOf(cond);
            return d is IHasPerkConditionDataGetter
                || d is IGetIsSexConditionDataGetter
                || d is IGetActorValueConditionDataGetter
                || d is IGetLevelConditionDataGetter
                || d is IGetStageDoneConditionDataGetter;
        }

        // The condition data types ItemDBHandler.Scan.cs knows how to persist. Anything else never
        // reaches item.db, so a rebuilt condition list would silently be missing it.
        private static bool IsRepresentable(IConditionGetter cond)
        {
            IConditionDataGetter? d = cond switch
            {
                IConditionFloatGetter cf => cf.Data,
                IConditionGlobalGetter cg => cg.Data,
                _ => null,
            };

            return d is IHasPerkConditionDataGetter
                || d is IGetIsSexConditionDataGetter
                || d is IGetActorValueConditionDataGetter
                || d is IGetLevelConditionDataGetter
                || d is IGetStageDoneConditionDataGetter
                // read-only types: not editable, but scanned and rebuilt faithfully
                || d is IGetItemCountConditionDataGetter
                || d is IEPTemperingItemIsEnchantedConditionDataGetter
                || d is IGetGlobalValueConditionDataGetter
                || d is IHasSpellConditionDataGetter
                || d is IHasKeywordConditionDataGetter
                || d is IGetQuestCompletedConditionDataGetter
                || d is IGetInCurrentLocConditionDataGetter
                || d is IGetVMQuestVariableConditionDataGetter;
        }

        public CobjEspResult Build(
            IReadOnlyList<CobjPatchEntry> entries,
            PatchFormIdMapStore map,
            IReadOnlyList<ModKey> loadOrder,
            string outputDir,
            string espFileName,
            bool eslWhenPossible,
            WinningRecordResolver? resolver = null,
            IReadOnlyList<EnchantmentEspEntry>? enchantmentOverrides = null)
        {
            var result = new CobjEspResult();
            var modKey = ModKey.FromFileName(espFileName);
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);

            uint maxNewId = 0;

            foreach (var e in entries.OrderBy(x => x.ToolKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildOne(mod, modKey, e, map, espFileName, ref maxNewId, result, resolver);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.ToolKey}: skipped — {ex.Message}");
                    result.SkippedCount++;
                }
            }

            foreach (var e in (enchantmentOverrides ?? Array.Empty<EnchantmentEspEntry>())
                     .OrderBy(x => x.EnchantmentKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildEnchantmentOverride(mod, e, result, resolver);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.EnchantmentKey}: enchantment override skipped — {ex.Message}");
                    result.SkippedCount++;
                }
            }

            // ESL: new records must sit in 0x000-0xFFF; overrides keep the master's FormID and
            // don't count. A pure-override patch is always ESL-safe.
            bool eslOk = eslWhenPossible && (result.NewCount == 0 || maxNewId <= 0xFFF);
            if (eslOk && (result.NewCount + result.OverrideCount) > 0)
            {
                mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small; // "Small" == the ESL flag
                result.EslFlagged = true;
            }

            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, espFileName);
            result.OutputPath = path;

            // MastersListOrderingByLoadOrder throws if a record references a plugin missing from the
            // ordering list. plugins.txt can lag reality (a mod disabled after the last scan), so
            // append every plugin the finished records reference that isn't already in the load
            // order. Reading the links off the built mod (not just off the entries) is what makes
            // deep copies safe: a copied record can reference plugins no tracked field mentions.
            var ordering = MergeReferencedInto(loadOrder, mod, entries);

            mod.WriteToBinary(path, new BinaryWriteParameters
            {
                MastersListContent = MastersListContentOption.Iterate,
                MastersListOrdering = new MastersListOrderingByLoadOrder(ordering),
                ModKey = ModKeyOption.NoCheck,
            });

            // WriteToBinary computes the on-disk master list without back-populating the in-memory
            // header, so re-read the file to report the real masters (and prove it round-trips).
            using (var written = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
                result.Masters = written.ModHeader.MasterReferences
                    .Select(m => m.Master.FileName.String).ToList();

            return result;
        }

        // loadOrder first (real ordering), then any plugin referenced by the built records or by an
        // entry that isn't already in it.
        private static List<ModKey> MergeReferencedInto(
            IReadOnlyList<ModKey> loadOrder, SkyrimMod mod, IReadOnlyList<CobjPatchEntry> entries)
        {
            var known = new HashSet<ModKey>(loadOrder);
            var ordering = new List<ModKey>(loadOrder);

            void AddKey(ModKey mk)
            {
                if (!mk.IsNull && known.Add(mk)) ordering.Add(mk);
            }

            void AddString(string? key)
            {
                if (string.IsNullOrEmpty(key)) return;
                int bar = key.IndexOf('|');
                if (bar <= 0) return; // not a "Plugin|FormID" ref (e.g. "Male", an ActorValue name)
                if (ModKey.TryFromFileName(key[..bar], out var mk)) AddKey(mk);
            }

            foreach (var link in mod.EnumerateFormLinks())
                AddKey(link.FormKey.ModKey);

            // Every record we wrote, not just the recipes: an ENCH override contributes its own
            // plugin as a master too, and MastersListOrderingByLoadOrder throws if it is missing.
            foreach (var rec in mod.EnumerateMajorRecords())
                AddKey(rec.FormKey.ModKey);

            foreach (var e in entries)
            {
                if (!e.IsNew) AddString(e.ToolKey);
                AddString(e.CreatedItemKey);
                AddString(e.WorkbenchKey);
                foreach (var (k, _) in e.Ingredients) AddString(k);
                foreach (var c in e.Conditions) AddString(c.Target);
            }

            return ordering;
        }


        // An enchantment whose worn-restriction FLST assignment the user re-pointed.
        //
        // This is the ONLY enchantment edit that cannot go through SkyPatcher - there is no
        // "wornRestrictions=" operation - so it is the one that forces an ESP override. Name, cost
        // and effects all travel as INI rules and must NOT be written here as well, or the ESP would
        // start winning over rules that are meant to compose with other mods.
        public sealed record EnchantmentEspEntry(string EnchantmentKey, string NewListKey)
        {
            // The plugin the ENCH itself comes from - used for the per-source-plugin ESP split.
            public string SourcePlugin
            {
                get
                {
                    int bar = EnchantmentKey.IndexOf('|');
                    return bar > 0 ? EnchantmentKey[..bar] : "";
                }
            }
        }

        // Deep-copies the winning ObjectEffect and changes exactly one field.
        //
        // Building this record from scratch the way COBJ does would be destructive: ObjectEffect
        // carries EnchantType, ChargeTime, Flags, EnchantmentAmount, ObjectBounds, BaseEnchantment,
        // CastType, TargetType, Effects and Name, none of which item.db tracks. So unlike COBJ there
        // is no from-scratch fallback here - without the winning record we skip and say so, because
        // a blanked enchantment is far worse than a missing override.
        private static void BuildEnchantmentOverride(
            SkyrimMod mod, EnchantmentEspEntry e, CobjEspResult result, WinningRecordResolver? resolver)
        {
            var formKey = KeyFactory.ParseFormKey(e.EnchantmentKey);

            if (resolver == null || !resolver.TryGetEnchantment(formKey, out var winner))
            {
                result.Warnings.Add(
                    $"{e.EnchantmentKey}: the worn-restriction list assignment was changed, but the " +
                    "enchantment could not be found in the load order — no ESP override was written. " +
                    "Rebuilding it from scratch would have blanked its effects, name and cast type.");
                result.SkippedCount++;
                return;
            }

            var ench = winner.DeepCopy();

            // "Assignment cleared" is a real choice: an empty key means the enchantment should no
            // longer be restricted to any keyword list at all.
            if (string.IsNullOrWhiteSpace(e.NewListKey))
                ench.WornRestrictions.SetToNull();
            else
                ench.WornRestrictions.SetTo(KeyFactory.ParseFormKey(e.NewListKey));

            mod.ObjectEffects.Add(ench);
            result.EnchantmentOverrideCount++;
        }

        private static void BuildOne(
            SkyrimMod mod, ModKey modKey, CobjPatchEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result, WinningRecordResolver? resolver)
        {
            if (string.IsNullOrWhiteSpace(e.CreatedItemKey))
            {
                result.Warnings.Add($"{e.ToolKey}: recipe has no created item — skipped.");
                result.SkippedCount++;
                return;
            }

            ConstructibleObject cobj;
            IConstructibleObjectGetter? winner = null;

            if (e.IsNew)
            {
                var id = map.Allocate(e.ToolKey, espFileName);
                maxNewId = Math.Max(maxNewId, id);
                cobj = new ConstructibleObject(new FormKey(modKey, id), SkyrimRelease.SkyrimSE);
                cobj.EditorID = $"SCT_{id:X6}";
                cobj.CreatedObjectCount = 1;
            }
            else
            {
                var formKey = KeyFactory.ParseFormKey(e.ToolKey); // foreign FormKey => override

                if (resolver != null && resolver.TryGetCobj(formKey, out var found))
                {
                    // Deep copy: everything the tool doesn't track (CreatedObjectCount, EditorID,
                    // record flags, and every condition type the scan can't read) survives untouched.
                    winner = found;
                    cobj = found.DeepCopy();
                    result.DeepCopiedCount++;
                }
                else
                {
                    if (resolver != null)
                        result.Warnings.Add(
                            $"{e.ToolKey}: not found in the load order, so the override was rebuilt " +
                            "from the tracked fields only — untracked fields (CreatedObjectCount, " +
                            "EditorID, unsupported condition types) fall back to defaults.");

                    cobj = new ConstructibleObject(formKey, SkyrimRelease.SkyrimSE);
                    cobj.CreatedObjectCount = 1;
                    result.FromScratchCount++;
                }
            }

            cobj.CreatedObject.SetTo(KeyFactory.ParseFormKey(e.CreatedItemKey));

            if (!string.IsNullOrWhiteSpace(e.WorkbenchKey))
                cobj.WorkbenchKeyword.SetTo(KeyFactory.ParseFormKey(e.WorkbenchKey));

            cobj.Items = new ExtendedList<ContainerEntry>();
            foreach (var (key, count) in e.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                cobj.Items.Add(new ContainerEntry
                {
                    Item = new ContainerItem
                    {
                        Item = new FormLink<IItemGetter>(KeyFactory.ParseFormKey(key)),
                        Count = count,
                    },
                });
            }

            // Conditions are the one field where "write back what we tracked" is actively dangerous,
            // because the scan understands only 5 of the condition types that occur in practice.
            //
            // - Deep copy, conditions untouched by the user -> keep the copied ones verbatim.
            // - Deep copy, but the real record holds a condition we can't represent -> keep the
            //   copied ones and say so; rewriting would drop it.
            // - Otherwise -> write the user's set (this is also the from-scratch path).
            bool copiedConditions = winner != null;
            int unrepresentable = winner?.Conditions.Count(c => !IsRepresentable(c)) ?? 0;

            // Stale-database guard.
            //
            // Read-only conditions (GetItemCount, EPTemperingItemIsEnchanted, ...) cannot be added or
            // removed in the UI, so their count in item.db must match the real record's. If the DB
            // has fewer, it was written by a scan from before those types were understood - and
            // rewriting from it would delete exactly the conditions that fix was about.
            //
            // This matters on upgrade: before, those types were "unrepresentable" and the check
            // below withheld the rewrite. Now that they ARE representable, an un-rescanned database
            // would sail straight through and lose them.
            int winnerReadOnly = winner?.Conditions.Count(c => !IsEditableCondition(c)) ?? 0;
            int dbReadOnly = e.Conditions.Count(c => !ConditionCatalog.IsEditable(c.ConditionType));
            bool staleDb = copiedConditions && dbReadOnly < winnerReadOnly;

            if (copiedConditions && !e.ConditionsEdited)
            {
                // nothing to do - the deep copy already carries the original conditions
            }
            else if (staleDb)
            {
                // Checked before the unrepresentable case on purpose: both can be true at once, and
                // this one is the actionable half. A rescan fixes it; if the recipe still holds a
                // genuinely unreadable condition afterwards, the next run says so instead.
                result.ConditionRewriteSkippedCount++;
                result.StaleConditionDataCount++;
                result.Warnings.Add(
                    $"{e.ToolKey}: your condition edits were NOT written — the stored conditions are " +
                    $"missing {winnerReadOnly - dbReadOnly} entr(y/ies) the real recipe has, so this " +
                    "database predates the condition-scan fix. Run Scan/Rescan, then generate again. " +
                    "The original conditions were kept.");
            }
            else if (copiedConditions && unrepresentable > 0)
            {
                result.ConditionRewriteSkippedCount++;
                result.Warnings.Add(
                    $"{e.ToolKey}: your condition edits were NOT written — the real recipe has " +
                    $"{unrepresentable} condition(s) of a type this tool cannot read yet, and " +
                    "rewriting the list would have deleted them. The original conditions were kept.");
            }
            else
            {
                cobj.Conditions.Clear();
                foreach (var condRec in e.Conditions)
                {
                    var cond = BuildCondition(condRec, result);
                    if (cond != null) cobj.Conditions.Add(cond);
                }
            }

            mod.ConstructibleObjects.Add(cobj);
            if (e.IsNew) result.NewCount++;
            else result.OverrideCount++;
        }

        // Rebuilds a stored condition row into a real Mutagen condition.
        //
        // The comparison operator and the flags now come from the database. They used to be guessed
        // per type, which measured exact against the vanilla masters but had no safe answer for the
        // OR flag: rebuilding an OR-chained pair as AND turns "either perk" into "both perks", and
        // the recipe disappears from the crafting menu. Rows written before those columns existed
        // still fall back to the old guess.
        private static Condition? BuildCondition(Model.COBJConditionRecord r, CobjEspResult result)
        {
            var runOn = Enum.TryParse<Condition.RunOnType>(r.RunOn, out var parsedRunOn)
                ? parsedRunOn
                : Condition.RunOnType.Subject;

            ConditionData? data;
            CompareOperator fallbackOp;
            string value = r.Value;

            switch (r.ConditionType)
            {
                case "HasPerk":
                {
                    var d = new HasPerkConditionData { RunOnType = runOn };
                    d.Perk.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    value = r.Value == "1" ? "1" : "0";
                    break;
                }
                case "GetIsSex":
                {
                    data = new GetIsSexConditionData
                    {
                        RunOnType = runOn,
                        MaleFemaleGender = r.Target == "Male" ? MaleFemaleGender.Male : MaleFemaleGender.Female,
                    };
                    fallbackOp = CompareOperator.EqualTo;
                    value = "1";
                    break;
                }
                case "GetActorValue":
                {
                    if (!Enum.TryParse<ActorValue>(r.Target, out var av))
                    {
                        result.Warnings.Add($"Unknown ActorValue '{r.Target}' in a condition — skipped.");
                        return null;
                    }
                    data = new GetActorValueConditionData { RunOnType = runOn, ActorValue = av };
                    fallbackOp = CompareOperator.GreaterThanOrEqualTo;
                    break;
                }
                case "GetLevel":
                    data = new GetLevelConditionData { RunOnType = runOn };
                    fallbackOp = CompareOperator.GreaterThanOrEqualTo;
                    break;

                case "GetStageDone":
                {
                    var d = new GetStageDoneConditionData
                    {
                        RunOnType = runOn,
                        Stage = (ushort)ParseI(r.Value),
                    };
                    d.Quest.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    value = "1";
                    break;
                }

                // ---- read-only types: scanned and rebuilt, never edited ----

                case "GetItemCount":
                {
                    var d = new GetItemCountConditionData { RunOnType = runOn };
                    d.ItemOrList.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.GreaterThanOrEqualTo;
                    break;
                }
                case "EPTemperingItemIsEnchanted":
                    data = new EPTemperingItemIsEnchantedConditionData { RunOnType = runOn };
                    fallbackOp = CompareOperator.EqualTo;
                    break;

                case "GetGlobalValue":
                {
                    var d = new GetGlobalValueConditionData { RunOnType = runOn };
                    d.Global.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }
                case "HasSpell":
                {
                    var d = new HasSpellConditionData { RunOnType = runOn };
                    d.Spell.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }
                case "HasKeyword":
                {
                    var d = new HasKeywordConditionData { RunOnType = runOn };
                    d.Keyword.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }
                case "GetQuestCompleted":
                {
                    var d = new GetQuestCompletedConditionData { RunOnType = runOn };
                    d.Quest.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }
                case "GetInCurrentLoc":
                {
                    var d = new GetInCurrentLocConditionData { RunOnType = runOn };
                    d.Location.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }
                case "GetVMQuestVariable":
                {
                    var d = new GetVMQuestVariableConditionData
                    {
                        RunOnType = runOn,
                        VariableName = r.Extra ?? "",
                    };
                    d.Quest.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.EqualTo;
                    break;
                }

                default:
                    // Either a "?Xxx" row (the scan saw a function it can't map) or a type from a
                    // newer scan than this builder. Callers must never reach here for a record they
                    // are rewriting - BuildOne refuses to rewrite conditions when the real record
                    // holds anything unrepresentable - so this is a genuine last-resort report.
                    result.Warnings.Add(
                        $"Condition type '{ConditionCatalog.FunctionName(r.ConditionType)}' cannot be " +
                        "rebuilt and was skipped.");
                    return null;
            }

            var op = Enum.TryParse<CompareOperator>(r.CompareOperator, out var storedOp)
                ? storedOp
                : fallbackOp;

            var cond = Float(data, op, ParseF(value));

            if (!string.IsNullOrWhiteSpace(r.Flags)
                && Enum.TryParse<Condition.Flag>(r.Flags, ignoreCase: true, out var flags))
            {
                cond.Flags = flags;
            }

            return cond;
        }

        private static ConditionFloat Float(ConditionData data, CompareOperator op, float value) => new()
        {
            Data = data,
            CompareOperator = op,
            ComparisonValue = value,
        };

        private static float ParseF(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static int ParseI(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
