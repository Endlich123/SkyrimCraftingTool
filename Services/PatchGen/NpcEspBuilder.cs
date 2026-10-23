using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // What an NPC override is being asked to change (Prio 8 / N-P6, docs/NPC-Plan.md §3.2).
    public sealed record NpcEspEntry(
        string NpcKey,
        string Name,
        string? CombatStyleKey,
        string? CrimeFactionKey,
        IReadOnlyList<string> PerksToRemove);

    // The three NPC edits no SkyPatcher operation can carry, written as an ESP override instead.
    //
    // THE PRICE, and the reason this is the last phase rather than the first: an override copies the
    // whole record and wins by load order. Whatever a later-installed mod does to that NPC's other
    // fields is then invisible, because our plugin is further down the list. This tool avoids that
    // everywhere it can - which is why everything else about an NPC travels as a rule.
    //
    // What it does NOT do is break the face. The override is a DEEP COPY of the winning record, so
    // HeadParts, TintLayers, FaceMorph, HairColor and HeadTexture carry over byte for byte and the
    // FaceGen files that go with them still match. Building one from scratch would blank all five,
    // and 65 % of NPCs have them (NPC-Plan.md §1). A record that cannot be found in the load order is
    // therefore skipped rather than assembled - a grey-faced NPC is worse than an unapplied edit.
    public static class NpcEspBuilder
    {
        // Which NPCs need one at all. Read from the same rows the rule builder uses, so an NPC whose
        // only edits are ordinary ones never reaches the ESP.
        public static List<NpcEspEntry> EntriesFor(IEnumerable<NpcRecord> edited)
        {
            var entries = new List<NpcEspEntry>();

            foreach (var npc in edited ?? Enumerable.Empty<NpcRecord>())
            {
                string? combatStyle = Changed(npc.CombatStyleKey, npc.Scanned.CombatStyleKey);
                string? crimeFaction = Changed(npc.CrimeFactionKey, npc.Scanned.CrimeFactionKey);

                // There is no perksToRemove operation, so a perk the plugins gave an NPC can only go
                // this way. A perk ADDED here is not in the list: perksToAdd handles that, and a rule
                // is always preferable to an override.
                var current = new HashSet<string>(
                    (npc.Perks ?? new List<NpcPerkRecord>()).Select(p => p.PerkKey),
                    StringComparer.OrdinalIgnoreCase);

                var removedPerks = (npc.ScannedPerks ?? new List<NpcPerkRecord>())
                    .Where(p => !current.Contains(p.PerkKey))
                    .Select(p => p.PerkKey)
                    .ToList();

                if (combatStyle == null && crimeFaction == null && removedPerks.Count == 0) continue;

                entries.Add(new NpcEspEntry(
                    npc.Key,
                    string.IsNullOrWhiteSpace(npc.Name) ? npc.EditorID : npc.Name,
                    combatStyle,
                    crimeFaction,
                    removedPerks));
            }

            return entries;
        }

        // null means "not touched". An emptied field is a real change - clearing an NPC's combat
        // style is a thing someone might want - so it comes back as the empty string, not as null.
        private static string? Changed(string edited, string scanned) =>
            string.Equals(edited ?? "", scanned ?? "", StringComparison.OrdinalIgnoreCase)
                ? null
                : edited ?? "";

        // Writes one override. Returns false when the record could not be found, which the caller
        // reports - the edit is then simply not applied, and that is the safe failure.
        public static bool Build(SkyrimMod mod, NpcEspEntry entry, WinningRecordResolver? resolver,
                                 ICollection<string> warnings)
        {
            var formKey = KeyFactory.ParseFormKey(entry.NpcKey);

            if (resolver == null || !resolver.TryGetNpc(formKey, out var winner))
            {
                warnings.Add(
                    $"{entry.NpcKey} ({entry.Name}): needs an ESP override, but the record could not " +
                    "be found in the load order - nothing was written. Building one from scratch " +
                    "would have blanked its face, inventory and everything else this tool does not " +
                    "track.");
                return false;
            }

            var npc = winner.DeepCopy();

            if (entry.CombatStyleKey != null)
            {
                if (entry.CombatStyleKey.Length == 0) npc.CombatStyle.SetToNull();
                else npc.CombatStyle.SetTo(KeyFactory.ParseFormKey(entry.CombatStyleKey));
            }

            if (entry.CrimeFactionKey != null)
            {
                if (entry.CrimeFactionKey.Length == 0) npc.CrimeFaction.SetToNull();
                else npc.CrimeFaction.SetTo(KeyFactory.ParseFormKey(entry.CrimeFactionKey));
            }

            if (entry.PerksToRemove.Count > 0 && npc.Perks != null)
            {
                var drop = new HashSet<FormKey>(entry.PerksToRemove.Select(KeyFactory.ParseFormKey));
                var kept = npc.Perks.Where(p => !drop.Contains(p.Perk.FormKey)).ToList();

                npc.Perks.Clear();
                foreach (var perk in kept) npc.Perks.Add(perk);
            }

            mod.Npcs.Add(npc);
            return true;
        }

        // What the report says about an override that WAS written. Every one is named: an override
        // is the one thing this tool does that another mod cannot undo by loading later, so it is
        // never a silent side effect of an edit.
        public static string Describe(NpcEspEntry entry)
        {
            var changes = new List<string>();

            if (entry.CombatStyleKey != null) changes.Add("combat style");
            if (entry.CrimeFactionKey != null) changes.Add("crime faction");
            if (entry.PerksToRemove.Count > 0) changes.Add($"{entry.PerksToRemove.Count} perk(s) removed");

            return $"{entry.NpcKey} ({entry.Name}): ESP override written for {string.Join(", ", changes)}. " +
                   "No SkyPatcher operation can reach these, so the record is overridden - the copy " +
                   "keeps its face and everything else untouched, but our plugin now wins over any " +
                   "mod loaded before it for this NPC.";
        }
    }
}
