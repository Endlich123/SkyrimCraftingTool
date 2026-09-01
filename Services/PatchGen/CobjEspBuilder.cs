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
        public bool EslFlagged { get; set; }
        public string OutputPath { get; set; } = "";
        public IReadOnlyList<string> Masters { get; set; } = Array.Empty<string>();
        public List<string> Warnings { get; } = new();
    }

    // Builds SkyrimCraftingTool.esp from CobjPatchEntry[]: new ConstructibleObject records for
    // tool-created recipes (compact 0x800+ FormIDs via PatchFormIdMapStore) and overrides for
    // edited master recipes. Conditions are rebuilt from the stored condition rows — see the
    // operator note in docs/PatchGenerator-Plan.md §3.
    public sealed class CobjEspBuilder
    {
        public CobjEspResult Build(
            IReadOnlyList<CobjPatchEntry> entries,
            PatchFormIdMapStore map,
            IReadOnlyList<ModKey> loadOrder,
            string outputDir,
            string espFileName,
            bool eslWhenPossible)
        {
            var result = new CobjEspResult();
            var modKey = ModKey.FromFileName(espFileName);
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);

            uint maxNewId = 0;

            foreach (var e in entries.OrderBy(x => x.ToolKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildOne(mod, modKey, e, map, espFileName, ref maxNewId, result);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.ToolKey}: skipped — {ex.Message}");
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
            // append every plugin the entries reference that isn't already in the load order.
            var ordering = MergeReferencedInto(loadOrder, entries);

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

        // loadOrder first (real ordering), then any plugin referenced by an entry that isn't in it.
        private static List<ModKey> MergeReferencedInto(IReadOnlyList<ModKey> loadOrder, IReadOnlyList<CobjPatchEntry> entries)
        {
            var known = new HashSet<ModKey>(loadOrder);
            var ordering = new List<ModKey>(loadOrder);

            void Add(string? key)
            {
                if (string.IsNullOrEmpty(key)) return;
                int bar = key.IndexOf('|');
                if (bar <= 0) return; // not a "Plugin|FormID" ref (e.g. "Male", an ActorValue name)
                if (ModKey.TryFromFileName(key[..bar], out var mk) && known.Add(mk))
                    ordering.Add(mk);
            }

            foreach (var e in entries)
            {
                if (!e.IsNew) Add(e.ToolKey);
                Add(e.CreatedItemKey);
                Add(e.WorkbenchKey);
                foreach (var (k, _) in e.Ingredients) Add(k);
                foreach (var c in e.Conditions) Add(c.Target);
            }

            return ordering;
        }

        private static void BuildOne(
            SkyrimMod mod, ModKey modKey, CobjPatchEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result)
        {
            if (string.IsNullOrWhiteSpace(e.CreatedItemKey))
            {
                result.Warnings.Add($"{e.ToolKey}: recipe has no created item — skipped.");
                result.SkippedCount++;
                return;
            }

            FormKey formKey;
            if (e.IsNew)
            {
                var id = map.Allocate(e.ToolKey, espFileName);
                maxNewId = Math.Max(maxNewId, id);
                formKey = new FormKey(modKey, id);
            }
            else
            {
                formKey = KeyFactory.ParseFormKey(e.ToolKey); // foreign FormKey => override
            }

            var cobj = new ConstructibleObject(formKey, SkyrimRelease.SkyrimSE);
            if (e.IsNew)
                cobj.EditorID = $"SCT_{formKey.ID:X6}";

            cobj.CreatedObject.SetTo(KeyFactory.ParseFormKey(e.CreatedItemKey));
            cobj.CreatedObjectCount = 1;

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

            foreach (var condRec in e.Conditions)
            {
                var cond = BuildCondition(condRec, result);
                if (cond != null) cobj.Conditions.Add(cond);
            }

            mod.ConstructibleObjects.Add(cobj);
            if (e.IsNew) result.NewCount++;
            else result.OverrideCount++;
        }

        // Stored condition rows carry no comparison operator (the scan doesn't persist one), so we
        // assume the vanilla-recipe convention: HasPerk/GetIsSex/GetStageDone == 1,
        // GetActorValue/GetLevel >= value. Fine for craft/temper recipes; a fidelity gap only for
        // exotic modded conditions on an edited override.
        private static Condition? BuildCondition(Model.COBJConditionRecord r, CobjEspResult result)
        {
            var runOn = r.RunOn == "Subject" ? Condition.RunOnType.Subject : Condition.RunOnType.Target;

            switch (r.ConditionType)
            {
                case "HasPerk":
                {
                    var data = new HasPerkConditionData { RunOnType = runOn };
                    data.Perk.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    return Float(data, CompareOperator.EqualTo, r.Value == "1" ? 1f : 0f);
                }
                case "GetIsSex":
                {
                    var data = new GetIsSexConditionData
                    {
                        RunOnType = runOn,
                        MaleFemaleGender = r.Target == "Male" ? MaleFemaleGender.Male : MaleFemaleGender.Female,
                    };
                    return Float(data, CompareOperator.EqualTo, 1f);
                }
                case "GetActorValue":
                {
                    if (!Enum.TryParse<ActorValue>(r.Target, out var av))
                    {
                        result.Warnings.Add($"Unknown ActorValue '{r.Target}' in a condition — skipped.");
                        return null;
                    }
                    var data = new GetActorValueConditionData { RunOnType = runOn, ActorValue = av };
                    return Float(data, CompareOperator.GreaterThanOrEqualTo, ParseF(r.Value));
                }
                case "GetLevel":
                {
                    var data = new GetLevelConditionData { RunOnType = runOn };
                    return Float(data, CompareOperator.GreaterThanOrEqualTo, ParseF(r.Value));
                }
                case "GetStageDone":
                {
                    var data = new GetStageDoneConditionData
                    {
                        RunOnType = runOn,
                        Stage = (ushort)ParseI(r.Value),
                    };
                    data.Quest.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    return Float(data, CompareOperator.EqualTo, 1f);
                }
                default:
                    result.Warnings.Add($"Unknown condition type '{r.ConditionType}' — skipped.");
                    return null;
            }
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
