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

        // Enchantments the user created in the tool and that this ESP brings into existence.
        public int NewEnchantmentCount { get; set; }

        // Leveled lists the user created in the tool - empty records, filled at runtime by SkyPatcher
        // (docs/LeveledList-Create-Plan.md, L-P3).
        public int NewLeveledListCount { get; set; }

        // Spells the user created in the tool and that this ESP brings into existence.
        public int NewSpellCount { get; set; }

        // Recipes whose edited condition set was NOT written, because the real record carries
        // conditions the scan cannot represent and rewriting would have deleted them.
        public int ConditionRewriteSkippedCount { get; set; }

        // Unreadable conditions put back verbatim into a rewritten list in place of their "?"
        // placeholder row (docs/Conditions-Plan.md, C1). Before, each of those withheld the edit.
        public int PlaceholderRestoredCount { get; set; }

        // Recipes the user took out of the game: an override carrying the Deleted record flag.
        // Reported separately because it is the one COBJ operation that REMOVES content another mod
        // put there - the same reason RemovalStore reports its decisions on their own.
        public int DeletedCount { get; set; }

        // Recipes whose stored conditions are missing entries the real record has - the hallmark of
        // an item.db written before the scan understood those condition types. Needs a rescan.
        public int StaleConditionDataCount { get; set; }

        // ENCH records overridden because their worn-restriction FLST assignment changed. SkyPatcher
        // has no operation for that field, so the ESP is the only route - see E-P4 in
        // docs/EnchantmentPatch-Plan.md.
        public int EnchantmentOverrideCount { get; set; }

        // NPC records overridden for combat style, crime faction or a removed perk - the three NPC
        // edits no SkyPatcher operation can carry (N-P6).
        public int NpcOverrideCount { get; set; }

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

        // The five types the scan kept from the start (ConditionCatalog.Core). An item.db from before
        // the condition-scan fix holds only these, so fewer of the others than the real record has
        // means "stale database". Counted on the scanned snapshot, not the live rows - see BuildOne.
        private static bool IsCoreCondition(IConditionGetter cond)
        {
            var d = DataOf(cond);
            return d is IHasPerkConditionDataGetter
                || d is IGetIsSexConditionDataGetter
                || d is IGetActorValueConditionDataGetter
                || d is IGetLevelConditionDataGetter
                || d is IGetStageDoneConditionDataGetter;
        }

        // Which original condition each "?" placeholder row stands for, in placeholder order - or null
        // when they cannot be matched up (the rewrite is then withheld).
        //
        // Two ways, by what the scan stored:
        //  - Every placeholder carries its position in the scanned record (Extra, since 2026-10-24).
        //    Then each one names its own original, and placeholders may have been DELETED - the
        //    remaining ones still find theirs.
        //  - Older rows carry none. Then the k-th placeholder is the k-th unreadable condition, which
        //    only holds while none was removed, so the counts must agree.
        // Either way the function name is checked, so a plugin that changed since the scan is caught
        // instead of handing back someone else's condition.
        //
        // Which way is decided on the SCANNED rows (the pre-edit snapshot, or the live rows when there
        // is none): if the scan stored positions, deleting placeholders is fine - even all of them,
        // which leaves no live row to tell. If it did not, the count rule applies.
        internal static List<IConditionGetter>? ResolvePlaceholders(
            IReadOnlyList<Model.COBJConditionRecord> rows, IReadOnlyList<Model.COBJConditionRecord> scanned,
            IReadOnlyList<IConditionGetter> recordConditions)
        {
            var placeholders = rows.Where(r => ConditionCatalog.IsUnsupported(r.ConditionType)).ToList();
            var scannedPlaceholders = scanned.Where(r => ConditionCatalog.IsUnsupported(r.ConditionType)).ToList();
            var resolved = new List<IConditionGetter>();

            bool byPosition = scannedPlaceholders.Count > 0
                && scannedPlaceholders.All(p => ConditionCatalog.TryGetPlaceholderPosition(p, out _))
                && placeholders.All(p => ConditionCatalog.TryGetPlaceholderPosition(p, out _));

            if (byPosition)
            {
                var used = new HashSet<int>();
                foreach (var p in placeholders)
                {
                    ConditionCatalog.TryGetPlaceholderPosition(p, out int pos);
                    if (pos < 0 || pos >= recordConditions.Count || !used.Add(pos)) return null;
                    var original = recordConditions[pos];
                    // Name only, not "is unreadable": a type the builder learned since the scan (GetStage,
                    // 2026-10-24) is still a placeholder in an un-rescanned database, and its original
                    // is now representable.
                    if (!SameFunction(p, original)) return null;
                    resolved.Add(original);
                }
                return resolved;
            }

            // Unreadable conditions - plus those of a function a placeholder still names, for the same
            // reason as above.
            var placeholderFunctions = placeholders.Select(p => ConditionCatalog.FunctionName(p.ConditionType)).ToHashSet();
            var originals = recordConditions.Where(c =>
                !IsRepresentable(c)
                || (DataOf(c) is { } d && placeholderFunctions.Contains(ConditionCatalog.FunctionNameOf(d.GetType())))).ToList();
            if (placeholders.Count != originals.Count) return null;
            for (int i = 0; i < placeholders.Count; i++)
            {
                if (!SameFunction(placeholders[i], originals[i])) return null;
                resolved.Add(originals[i]);
            }
            return resolved;
        }

        private static bool SameFunction(Model.COBJConditionRecord placeholder, IConditionGetter original)
        {
            var data = DataOf(original);
            return data != null
                && ConditionCatalog.FunctionName(placeholder.ConditionType) == ConditionCatalog.FunctionNameOf(data.GetType());
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

            // Compared against a global variable ("Smithing >= the value of CC0262"): rebuilt as a
            // ConditionGlobal from the key the scan stores in Value (36 recipes of the real modlist,
            // 2026-10-24). Until then these kept their original conditions.
            if (d == null) return false;

            // Run on a specific reference: the scan keeps which one since 2026-10-24 (RunOnRef,
            // PlayerRef for 96% of them). Rows from an older scan are caught by the stale check in
            // BuildOne, not here. QuestAlias/PackageData/EventData pick their subject through
            // RunOnTypeIndex, which is not stored - those keep their original conditions.
            if (d.RunOnType == Condition.RunOnType.Reference && d.Reference.FormKey.IsNull) return false;
            if (d.RunOnType is Condition.RunOnType.QuestAlias
                or Condition.RunOnType.PackageData
                or Condition.RunOnType.EventData) return false;

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
                || d is IGetVMQuestVariableConditionDataGetter
                || d is IGetStageConditionDataGetter;   // editable since 2026-10-24
        }

        public CobjEspResult Build(
            IReadOnlyList<CobjPatchEntry> entries,
            PatchFormIdMapStore map,
            IReadOnlyList<ModKey> loadOrder,
            string outputDir,
            string espFileName,
            bool eslWhenPossible,
            WinningRecordResolver? resolver = null,
            IReadOnlyList<EnchantmentEspEntry>? enchantmentOverrides = null,
            IReadOnlyList<NewEnchantmentEspEntry>? newEnchantments = null,
            IReadOnlyList<NpcEspEntry>? npcOverrides = null,
            IReadOnlyList<NewLeveledListEspEntry>? newLeveledLists = null,
            IReadOnlyList<NewSpellEspEntry>? newSpells = null)
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

            foreach (var e in (newEnchantments ?? Array.Empty<NewEnchantmentEspEntry>())
                     .OrderBy(x => x.ToolKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildNewEnchantment(mod, modKey, e, map, espFileName, ref maxNewId, result);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.EditorId}: new enchantment skipped — {ex.Message}");
                    result.SkippedCount++;
                }
            }

            foreach (var e in (newLeveledLists ?? Array.Empty<NewLeveledListEspEntry>())
                     .OrderBy(x => x.ToolKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildNewLeveledList(mod, modKey, e, map, espFileName, ref maxNewId, result);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.EditorId}: new leveled list skipped — {ex.Message}");
                    result.SkippedCount++;
                }
            }

            foreach (var e in (newSpells ?? Array.Empty<NewSpellEspEntry>())
                     .OrderBy(x => x.ToolKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BuildNewSpell(mod, modKey, e, map, espFileName, ref maxNewId, result);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.EditorId}: new spell skipped — {ex.Message}");
                    result.SkippedCount++;
                }
            }

            // NPC overrides (N-P6). Last, and named one by one in the report: an override is the one
            // thing this tool writes that a later-loading mod cannot undo, so it is never silent.
            foreach (var e in (npcOverrides ?? Array.Empty<NpcEspEntry>())
                     .OrderBy(x => x.NpcKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (NpcEspBuilder.Build(mod, e, resolver, result.Warnings))
                    {
                        result.NpcOverrideCount++;
                        result.Warnings.Add(NpcEspBuilder.Describe(e));
                    }
                    else result.SkippedCount++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{e.NpcKey}: NPC override skipped — {ex.Message}");
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
                MastersContentCustomOverride = WithGameMaster,
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

        // Skyrim.esm belongs in the master list of every generated ESP, even when nothing in it
        // references the base game.
        //
        // MastersListContentOption.Iterate writes only the masters the records actually reach, so a
        // patch touching one mod ends up with that mod alone - at master index 0. A reference into
        // it then becomes the FormID 0x000000xx, and anything below 0x800 is the range reserved for
        // forms hardcoded in the engine: xEdit stops before the master list and shows "could not be
        // resolved, but is possibly hardcoded in the engine". ESL-flagged plugins routinely hold
        // records down there (BladeAndBlunt.esp's MAG_StaggerSlowEffect20 is 0x000072), so this is
        // not a corner case. With Skyrim.esm ahead of it the same reference is 0x010000xx and
        // resolves. When something already pulls Skyrim.esm in, this changes nothing.
        private static readonly ModKey GameMaster = ModKey.FromFileName("Skyrim.esm");

        private static IReadOnlyCollection<ModKey> WithGameMaster(IReadOnlyCollection<ModKey> masters)
            => masters.Contains(GameMaster)
                ? masters
                : masters.Prepend(GameMaster).ToList();

        // loadOrder first (real ordering), then any plugin referenced by the built records or by an
        // entry that isn't already in it.
        private static List<ModKey> MergeReferencedInto(
            IReadOnlyList<ModKey> loadOrder, SkyrimMod mod, IReadOnlyList<CobjPatchEntry> entries)
        {
            var known = new HashSet<ModKey>(loadOrder);
            var ordering = new List<ModKey>(loadOrder);

            // WithGameMaster adds it to the master list, and MastersListOrderingByLoadOrder throws
            // over a master the ordering doesn't know. Front, not back: the ordering is what decides
            // that Skyrim.esm comes first, which is the whole point of adding it.
            if (known.Add(GameMaster)) ordering.Insert(0, GameMaster);

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
                foreach (var c in e.Conditions) { AddString(c.Target); AddString(c.RunOnRef); }
            }

            return ordering;
        }


        // An enchantment whose worn-restriction FLST assignment the user re-pointed.
        //
        // This is the ONLY enchantment edit that cannot go through SkyPatcher - there is no
        // "wornRestrictions=" operation - so it is the one that forces an ESP override. Name, cost
        // and effects all travel as INI rules and must NOT be written here as well, or the ESP would
        // start winning over rules that are meant to compose with other mods.
        // An enchantment the user created in the tool. Unlike the override above there is nothing to
        // deep-copy: the record does not exist anywhere yet, so every field it gets is one the tool
        // tracks. CastType/TargetType came from its first effect (see EnchantmentMenuVM), which is
        // what the load order does too - 1,893 of 1,895 effect rows agree with their enchantment.
        public sealed record NewEnchantmentEspEntry(
            string ToolKey,
            string EditorId,
            string Name,
            string CastType,
            string TargetType,
            float Cost,
            string EnchantType,
            int Flags,
            float ChargeTime,
            int Amount,
            string WornRestrictionListKey,
            IReadOnlyList<NewEnchantmentEffect> Effects);

        public sealed record NewEnchantmentEffect(string MagicEffectKey, float Magnitude, int Duration, int Area);

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

        // Writes an enchantment that exists nowhere else. The FormID comes from the same persistent
        // map the new COBJ records use, so the record keeps its ID across regenerations - without
        // that, every patch run would hand the enchantment a new FormID and every save game that
        // carries it would lose it.
        private static void BuildNewEnchantment(
            SkyrimMod mod, ModKey modKey, NewEnchantmentEspEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result)
        {
            var id = map.Allocate(e.ToolKey, espFileName);
            maxNewId = Math.Max(maxNewId, id);

            var cost = (uint)Math.Max(0, Math.Round(e.Cost));

            var ench = new ObjectEffect(new FormKey(modKey, id), SkyrimRelease.SkyrimSE)
            {
                EditorID = string.IsNullOrWhiteSpace(e.EditorId) ? $"SCT_Ench_{id:X6}" : e.EditorId,
                Name = e.Name ?? "",
                EnchantmentCost = cost,

                // ENIT's enchant type knows exactly two values: 6 (Enchantment) and 12 (Staff
                // Enchantment). Left at Mutagen's default the record is an "<Unknown: 0>" that
                // matches no vanilla ENCH, so an unparseable or empty value falls back to
                // Enchantment rather than to zero.
                EnchantType = Enum.TryParse<ObjectEffect.EnchantTypeEnum>(e.EnchantType, ignoreCase: true, out var et)
                    ? et
                    : ObjectEffect.EnchantTypeEnum.Enchantment,

                Flags = (ObjectEffect.Flag)e.Flags,
                ChargeTime = e.ChargeTime,

                // Still mirrored when the user left it at 0 - vanilla does the same in 1862 of 1943
                // records, and an amount of 0 means an enchanted weapon spends nothing per hit.
                EnchantmentAmount = e.Amount != 0 ? e.Amount : (int)cost,
            };

            if (Enum.TryParse<CastType>(e.CastType, ignoreCase: true, out var castType))
                ench.CastType = castType;
            if (Enum.TryParse<TargetType>(e.TargetType, ignoreCase: true, out var targetType))
                ench.TargetType = targetType;

            // The keyword FLST that decides which slots the enchantment may be worn on. The picker
            // offers it for a tool-created enchantment exactly as it does for a scanned one, and the
            // record it lands on is written here - the override path in BuildEnchantmentOverride
            // only ever sees Original = 1 rows, so nothing else would pick this up.
            //
            // Unset is the normal case and means "no restriction", not a missing value.
            if (!KeyFactory.IsUnsetKey(e.WornRestrictionListKey))
                ench.WornRestrictions.SetTo(KeyFactory.ParseFormKey(e.WornRestrictionListKey));

            foreach (var effect in e.Effects ?? Array.Empty<NewEnchantmentEffect>())
            {
                var mgef = KeyFactory.ParseFormKey(effect.MagicEffectKey);
                if (mgef.IsNull)
                {
                    result.Warnings.Add($"{e.EditorId}: effect {effect.MagicEffectKey} could not be resolved — left out.");
                    continue;
                }

                ench.Effects.Add(new Effect
                {
                    BaseEffect = new FormLinkNullable<IMagicEffectGetter>(mgef),
                    Data = new EffectData
                    {
                        Magnitude = effect.Magnitude,
                        Duration = effect.Duration,
                        Area = Math.Max(0, effect.Area),
                    },
                });
            }

            if (ench.Effects.Count == 0)
            {
                // An enchantment without effects does nothing in the game and would only confuse
                // whoever finds it in the ESP later.
                result.Warnings.Add($"{ench.EditorID}: no effects — not written.");
                result.SkippedCount++;
                return;
            }

            mod.ObjectEffects.Add(ench);
            result.NewCount++;
            result.NewEnchantmentCount++;
        }

        // A spell the user created in the tool. Like the new enchantment above there is nothing to
        // deep-copy - the record exists nowhere yet, so every field it gets is one the tool tracks.
        //
        // It carries MORE fields than a patched spell can, and that is the point of the ESP route:
        // SpellType, TargetType, Range and CastDuration have no SkyPatcher operation, so they are
        // unreachable on a scanned record and perfectly writable on one built from scratch.
        public sealed record NewSpellEspEntry(
            string ToolKey,
            string EditorId,
            string Name,
            string Description,
            string SpellType,
            string CastType,
            string TargetType,
            int BaseCost,
            float ChargeTime,
            float CastDuration,
            float Range,
            string HalfCostPerkKey,
            int Flags,
            IReadOnlyList<NewSpellEffect> Effects);

        public sealed record NewSpellEffect(string MagicEffectKey, float Magnitude, int Duration, int Area);

        private static readonly FormKey VoiceEquipType = FormKey.Factory("025BEE:Skyrim.esm");
        private static readonly FormKey EitherHandEquipType = FormKey.Factory("013F44:Skyrim.esm");


        // A spell the user created in the tool (docs/Spells-Plan.md, S-P6).
        //
        // Built from scratch, like a new enchantment and unlike every override: the record exists
        // nowhere, so there is no winning copy to take and nothing to lose by writing every field.
        private static void BuildNewSpell(
            SkyrimMod mod, ModKey modKey, NewSpellEspEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result)
        {
            var id = map.Allocate(e.ToolKey, espFileName);
            maxNewId = Math.Max(maxNewId, id);

            var spell = new Spell(new FormKey(modKey, id), SkyrimRelease.SkyrimSE)
            {
                EditorID = string.IsNullOrWhiteSpace(e.EditorId) ? $"SCT_Spell_{id:X6}" : e.EditorId,
                Name = e.Name ?? "",
                Description = e.Description ?? "",
                BaseCost = (uint)Math.Max(0, e.BaseCost),
                ChargeTime = e.ChargeTime,
                CastDuration = e.CastDuration,
                Range = e.Range,
                Flags = (SpellDataFlag)e.Flags,
            };

            // SPIT's spell type knows eight values, and an unparseable one left at Mutagen's default
            // is a plain Spell - which is also the overwhelmingly common case (2.389 of 3.684
            // measured), so the fallback costs nothing and an "<Unknown: 0>" record would match no
            // vanilla SPEL at all.
            if (Enum.TryParse<SpellType>(e.SpellType, ignoreCase: true, out var spellType))
                spell.Type = spellType;

            // ETYP IS REQUIRED, AND NOTHING IN THE TOOL ASKS FOR IT. It says what the spell is
            // equipped to, and a record without it was what every created spell used to get. Derived
            // from the type, which is the only thing that decides it in practice. Measured on
            // 7.574 scanned spells: Voice for most powers and shouts, Either Hand for the rest.
            spell.EquipmentType.SetTo(spell.Type is SpellType.Power or SpellType.LesserPower or SpellType.Voice
                ? VoiceEquipType
                : EitherHandEquipType);

            if (Enum.TryParse<CastType>(e.CastType, ignoreCase: true, out var castType))
                spell.CastType = castType;
            if (Enum.TryParse<TargetType>(e.TargetType, ignoreCase: true, out var targetType))
                spell.TargetType = targetType;

            // Unset is the normal case and means "no perk halves this", not a missing value.
            if (!KeyFactory.IsUnsetKey(e.HalfCostPerkKey))
                spell.HalfCostPerk.SetTo(KeyFactory.ParseFormKey(e.HalfCostPerkKey));

            foreach (var effect in e.Effects ?? Array.Empty<NewSpellEffect>())
            {
                var mgef = KeyFactory.ParseFormKey(effect.MagicEffectKey);
                if (mgef.IsNull)
                {
                    result.Warnings.Add($"{e.EditorId}: effect {effect.MagicEffectKey} could not be resolved — left out.");
                    continue;
                }

                spell.Effects.Add(new Effect
                {
                    BaseEffect = new FormLinkNullable<IMagicEffectGetter>(mgef),
                    Data = new EffectData
                    {
                        Magnitude = effect.Magnitude,
                        Duration = effect.Duration,
                        Area = Math.Max(0, effect.Area),
                    },
                });
            }

            if (spell.Effects.Count == 0)
            {
                // A spell with no effects does nothing in the game and would only puzzle whoever
                // finds it in the ESP later. Same rule as a new enchantment.
                result.Warnings.Add($"{e.EditorId}: no effects — not written.");
                result.SkippedCount++;
                return;
            }

            mod.Spells.Add(spell);
            result.NewCount++;
            result.NewSpellCount++;
        }

        // A leveled list the user created in the tool (docs/LeveledList-Create-Plan.md, L-P3).
        public sealed record NewLeveledListEspEntry(string ToolKey, string EditorId);

        // AN EMPTY RECORD, AND THAT IS ALL OF IT. SkyPatcher's leveled-list patcher can do everything
        // to a list except create one, so the ESP's one job is the record's existence: no entries, a
        // chance of none of 0, no flags. What goes into it arrives at runtime like it arrives in any
        // other list - addOnceToLLs from an item's placement, and chanceNone / calc flags from the
        // property rules. One way in for every list, scanned or own.
        //
        // Unlike an effectless enchantment, an empty list is NOT held back: it is the normal state of
        // a list straight after it was created, and it does nothing in the game - nothing to hand out,
        // nothing to get wrong.
        //
        // Same FormID map as the recipes and enchantments, so the list keeps its ID across every
        // regeneration - a save that holds an item from it, or a mod that was pointed at it, would
        // otherwise lose it on the next patch run.
        private static void BuildNewLeveledList(
            SkyrimMod mod, ModKey modKey, NewLeveledListEspEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result)
        {
            var id = map.Allocate(e.ToolKey, espFileName);
            maxNewId = Math.Max(maxNewId, id);

            var list = new LeveledItem(new FormKey(modKey, id), SkyrimRelease.SkyrimSE)
            {
                EditorID = string.IsNullOrWhiteSpace(e.EditorId) ? $"SCT_LList_{id:X6}" : e.EditorId,
            };

            mod.LeveledItems.Add(list);
            result.NewCount++;
            result.NewLeveledListCount++;
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

        // A recipe the user took out of the game: an override of the winning record carrying the
        // Deleted record flag, and nothing else.
        //
        // COBJ is the one record type where this is safe and simple: NOTHING references a
        // ConstructibleObject by FormID - the crafting menu just enumerates them - so there is no
        // dangling reference to leave behind. The "deleted records crash the game" rule people
        // remember is about REFR, which is why xEdit's fix for those is called "Undelete and Disable
        // REFERENCES".
        //
        // Verified against Mutagen before this was built (2026-10-05): a deep copy with
        // IsDeleted = true writes, reads back as deleted, and keeps a correct master list.
        //
        // Deep-copying rather than building an empty stub keeps the record's own FormKey and masters
        // honest; the game ignores the payload of a deleted record either way.
        private static void BuildDeleted(
            SkyrimMod mod, CobjPatchEntry e, CobjEspResult result, WinningRecordResolver? resolver)
        {
            var formKey = KeyFactory.ParseFormKey(e.ToolKey);

            ConstructibleObject cobj;
            if (resolver != null && resolver.TryGetCobj(formKey, out var winner))
            {
                cobj = winner.DeepCopy();
            }
            else
            {
                // Not in the load order: the recipe's own plugin is gone or disabled, so there is
                // nothing left to delete. Writing an override anyway would resurrect the FormID as
                // an injected record.
                result.Warnings.Add(
                    $"{e.ToolKey}: marked for removal but not found in the load order — nothing to delete, skipped.");
                result.SkippedCount++;
                return;
            }

            cobj.IsDeleted = true;
            mod.ConstructibleObjects.Add(cobj);

            result.OverrideCount++;
            result.DeletedCount++;
        }

        private static void BuildOne(
            SkyrimMod mod, ModKey modKey, CobjPatchEntry e, PatchFormIdMapStore map,
            string espFileName, ref uint maxNewId, CobjEspResult result, WinningRecordResolver? resolver)
        {
            // A removal is handled first and on its own: it carries no fields at all, and the
            // created-item guard below would throw it away before it ever got here.
            if (e.IsDeleted)
            {
                BuildDeleted(mod, e, result, resolver);
                return;
            }

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
                    result.FromScratchCount++;
                }
            }

            cobj.CreatedObject.SetTo(KeyFactory.ParseFormKey(e.CreatedItemKey));

            // The created-object count (NAM1), and WHEN it may be written.
            //
            // It used to be a literal 1 in both branches above - invisible while the tool only made
            // forge and tempering recipes, which produce one item, but it is the central value of a
            // breakdown recipe ("gives 2 steel ingots") and was already quietly wrong for anything
            // producing a stack, arrows being the obvious case.
            //
            // A DEEP COPY is the case to be careful with. It already carries the winning record's
            // real count, and item.db only learns that number on the next scan - so writing the
            // stored value unconditionally would stamp a 1 over the truth for every row that
            // predates the column. Same rule as the conditions right below: only an edit the user
            // actually made is allowed to overwrite what the copy brought along.
            //
            // A new record and a from-scratch rebuild have nothing to preserve, so they take the
            // stored value (which defaults to 1, exactly the old behaviour).
            if (e.IsNew || winner == null || e.CreatedObjectCountEdited)
                cobj.CreatedObjectCount = (ushort)Math.Clamp(e.CreatedObjectCount, 1, ushort.MaxValue);

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
            // - Deep copy, but the real record holds a condition we can't represent -> write the
            //   user's set, with each "?" placeholder row replaced by the copied original it stands
            //   for (docs/Conditions-Plan.md, C1). If the placeholders don't line up with the real
            //   record, keep the copied ones and say so; rewriting would drop something.
            // - Otherwise -> write the user's set (this is also the from-scratch path).
            bool copiedConditions = winner != null;
            int unrepresentable = winner?.Conditions.Count(c => !IsRepresentable(c)) ?? 0;

            // Resolved against the deep copy BEFORE its list is cleared below: the originals the
            // placeholders get swapped back for, in placeholder order.
            // The rows as scanned (see the stale-database guard below for why not the live ones -
            // read-only rows can be deleted since 2026-10-24).
            var scanned = e.OriginalConditions.Count > 0 ? e.OriginalConditions : e.Conditions;

            var originalsForPlaceholders = ResolvePlaceholders(e.Conditions, scanned, cobj.Conditions.ToList());
            bool placeholdersMatch = originalsForPlaceholders != null;

            // A Global comparison stored as a plain number (a scan from before the key went into
            // Value) would be rebuilt as "compare against 0" - every such row must carry its key.
            int winnerGlobals = winner?.Conditions.Count(c => c is IConditionGlobalGetter && IsRepresentable(c)) ?? 0;
            int dbGlobals = scanned.Count(c =>
                !ConditionCatalog.IsUnsupported(c.ConditionType) && (c.Value ?? "").Contains('|'));
            bool globalsMissing = copiedConditions && dbGlobals < winnerGlobals;

            // Stale-database guard.
            //
            // Everything beyond the first five types (EPTemperingItemIsEnchanted, GetItemCount, "?"
            // placeholders, ...) was dropped by the scan before the condition-scan fix. If the scanned
            // rows hold fewer of them than the real record, the database predates that fix - and
            // rewriting from it would delete exactly the conditions that fix was about.
            //
            // Counted on the snapshot taken before the first edit (OriginalConditions), not on the
            // live rows: GetItemCount is editable since C3, so a live row the user deleted would look
            // exactly like one the old scan never stored. Without a snapshot the live rows stand in,
            // which is exact as long as nothing beyond the five was removable - true until C3.
            int winnerReadOnly = winner?.Conditions.Count(c => !IsCoreCondition(c)) ?? 0;
            int dbReadOnly = scanned.Count(c => !ConditionCatalog.IsCore(c.ConditionType));
            bool staleDb = copiedConditions && dbReadOnly < winnerReadOnly;

            // Reference rows without their reference: scanned before RunOnRef existed, or an edited
            // recipe (a rescan leaves its rows alone). Rebuilt as they are they would run on nothing.
            // When every Reference condition of the real record runs on the same reference - almost
            // always PlayerRef - that is the answer; otherwise the rewrite is withheld.
            bool missingRunOnRef = false;
            var refRowsWithoutRef = e.Conditions
                .Where(c => c.RunOn == nameof(Condition.RunOnType.Reference) && string.IsNullOrEmpty(c.RunOnRef))
                .ToList();
            if (copiedConditions && e.ConditionsEdited && refRowsWithoutRef.Count > 0)
            {
                var winnerRefs = winner!.Conditions
                    .Select(DataOf)
                    .Where(d => d != null && d.RunOnType == Condition.RunOnType.Reference && !d.Reference.FormKey.IsNull)
                    .Select(d => d!.Reference.FormKey)
                    .Distinct()
                    .ToList();
                if (winnerRefs.Count == 1)
                    foreach (var row in refRowsWithoutRef) row.RunOnRef = KeyFactory.BuildMasterKey(winnerRefs[0]);
                else
                    missingRunOnRef = true;
            }

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
            else if (missingRunOnRef)
            {
                result.ConditionRewriteSkippedCount++;
                result.Warnings.Add(
                    $"{e.ToolKey}: your condition edits were NOT written — {refRowsWithoutRef.Count} stored " +
                    "condition(s) run on a reference this database does not know, and the real recipe uses " +
                    "more than one. Reset this recipe's conditions, run Scan/Rescan, redo the edit and generate again. " +
                    "The original conditions were kept.");
            }
            else if (globalsMissing)
            {
                result.ConditionRewriteSkippedCount++;
                result.Warnings.Add(
                    $"{e.ToolKey}: your condition edits were NOT written — the real recipe compares " +
                    $"{winnerGlobals} condition(s) against a global variable, but the stored conditions " +
                    "only know " + dbGlobals + ". Reset this recipe's conditions, run Scan/Rescan, redo the " +
                    "edit and generate again. The original conditions were kept.");
            }
            else if (copiedConditions && !placeholdersMatch)
            {
                // Also catches placeholders WITHOUT an original - copied over from another recipe,
                // or the plugin dropped the condition since - which would otherwise index past the end.
                result.ConditionRewriteSkippedCount++;
                result.Warnings.Add(
                    $"{e.ToolKey}: your condition edits were NOT written — the real recipe has " +
                    $"{unrepresentable} condition(s) this tool cannot read, and the stored placeholders " +
                    "for them do not match it (plugin changed since the scan, or copied from another recipe). Run Scan/Rescan; " +
                    "if the recipe was edited, reset its conditions first. The original conditions were kept.");
            }
            else
            {
                cobj.Conditions.Clear();
                int nextOriginal = 0;
                foreach (var condRec in e.Conditions)
                {
                    // A placeholder: put back the original it stands for. Only reachable with
                    // placeholdersMatch, so there is one per placeholder and the function agrees.
                    //
                    // Verbatim except for the flags, which are the ROW's: deleting the last member of
                    // an OR group clears the OR of the row before it, and when that row is a
                    // placeholder the change has to reach the ESP too. Unedited, the row carries the
                    // flags the scan read, so this changes nothing.
                    if (ConditionCatalog.IsUnsupported(condRec.ConditionType) && copiedConditions)
                    {
                        var original = originalsForPlaceholders![nextOriginal++].DeepCopy();
                        // Operator and flags were added to the scan together (2026-10-23): a row without an
                        // operator predates both, and its empty Flags means "unknown", not "no OR".
                        if (!string.IsNullOrEmpty(condRec.CompareOperator))
                            original.Flags = ParseFlags(condRec.Flags, original.Flags);
                        cobj.Conditions.Add(original);
                        result.PlaceholderRestoredCount++;
                        continue;
                    }

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

                // The quest's CURRENT stage compared against the value - "Quest >= 20". Not
                // GetStageDone, which asks whether one particular stage was set and compares to 1.
                case "GetStage":
                {
                    var d = new GetStageConditionData { RunOnType = runOn };
                    d.Quest.Link.SetTo(KeyFactory.ParseFormKey(r.Target));
                    data = d;
                    fallbackOp = CompareOperator.GreaterThanOrEqualTo;   // 147 of the 227 real ones
                    break;
                }

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

            if (runOn == Condition.RunOnType.Reference)
            {
                // Only reachable without a reference for a recipe that was never deep-copied (BuildOne
                // fills or withholds the others). Running on nothing would make the condition fail
                // silently in game, so say so instead.
                if (string.IsNullOrEmpty(r.RunOnRef))
                {
                    result.Warnings.Add(
                        $"A '{r.ConditionType}' condition runs on a reference that is not stored — skipped. Rescan to fix.");
                    return null;
                }
                data.Reference.SetTo(KeyFactory.ParseFormKey(r.RunOnRef));
            }

            var op = Enum.TryParse<CompareOperator>(r.CompareOperator, out var storedOp)
                ? storedOp
                : fallbackOp;

            // Compared against a global: the scan stores the global's key as the value. Checked on the
            // RAW value - HasPerk/GetIsSex/GetStageDone normalise `value` above and would turn the key
            // into a number. Written as a float this was "compare against 0".
            Condition cond = (r.Value ?? "").Contains('|')
                ? Global(data, op, KeyFactory.ParseFormKey(r.Value!))
                : Float(data, op, ParseF(value));

            if (!string.IsNullOrWhiteSpace(r.Flags)
                && Enum.TryParse<Condition.Flag>(r.Flags, ignoreCase: true, out var flags))
            {
                cond.Flags = flags;
            }

            return cond;
        }

        private static ConditionGlobal Global(ConditionData data, CompareOperator op, FormKey global)
        {
            var cond = new ConditionGlobal { Data = data, CompareOperator = op };
            cond.ComparisonValue.SetTo(global);
            return cond;
        }

        private static ConditionFloat Float(ConditionData data, CompareOperator op, float value) => new()
        {
            Data = data,
            CompareOperator = op,
            ComparisonValue = value,
        };

        // A row's stored flags ("" = none, "OR", "OR,SwapSubjectAndTarget"). Text that does not parse keeps
        // what the plugin had rather than silently dropping every flag.
        private static Condition.Flag ParseFlags(string? stored, Condition.Flag fallback)
        {
            if (string.IsNullOrWhiteSpace(stored)) return 0;
            return Enum.TryParse<Condition.Flag>(stored, ignoreCase: true, out var flags) ? flags : fallback;
        }

        private static float ParseF(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static int ParseI(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
