using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Taking one placement back out of an item's ContainerString.
    //
    // THE STRING IS THE RULE. "{ContainerKey: {LVLiKey,Level[,Count]; ...}}" says two different
    // things depending on the levels, and LeveledListRuleBuilder.ParsePlacements is what turns each
    // into a patch rule:
    //
    //   a raised level  -> the item goes into THOSE leveled lists  (filterByLLs + addOnceToLLs)
    //   no raised level -> the item goes into the CONTAINER itself (filterByContainers + addOnceToContainers)
    //
    // Which is why removing a placement is not just "delete the entry that mentions the key", and
    // why this is a file of its own rather than three lines at a call site:
    //
    //   THE EMPTY-ENTRY TRAP. Taking the last leveled list out of a container entry leaves
    //   "{ContainerKey: {}}" - and that is not "nothing", that is "put the item in the chest
    //   itself". Un-placing an item from the one list it was in would silently place it in the
    //   chest. So an entry that had placements and has none left is dropped with them.
    //
    // Written as a string-to-string transformation on purpose: both callers need it - the one that
    // holds a live view model for the item and the one that only has the database row - and one
    // rule implemented twice is one rule that stops being the same rule.
    public static class ContainerPlacementEditor
    {
        public static string WithoutPlacement(string containerString, string ownerKey, bool ownerIsContainer)
        {
            if (string.IsNullOrWhiteSpace(containerString)) return containerString ?? "";
            if (string.IsNullOrWhiteSpace(ownerKey)) return containerString;

            List<ParsedContainerEntry> parsed;
            try
            {
                parsed = ContainerStringParser.Parse(containerString);
            }
            catch (Exception ex)
            {
                // Refuse rather than guess. Rewriting a string we could not read would be the one
                // way to lose placements the user never asked to touch.
                AppLogger.LogError($"ContainerPlacementEditor: unreadable ContainerString, left alone", ex);
                return containerString;
            }

            // NOTHING TO READ, SO NOTHING TO REMOVE. The parser is tolerant rather than strict - it
            // skips what it cannot make sense of and hands back an empty list - so a string it did
            // not understand would otherwise be "rebuilt" as "{}" and every placement on that item
            // would be gone. An empty result means the input is either genuinely empty or not a
            // placement string, and in both cases the right answer is to leave it exactly as it is.
            if (parsed.Count == 0) return containerString;

            var kept = new List<ContainerStringEntry>();
            bool removedSomething = false;

            foreach (var entry in parsed)
            {
                bool isTheContainer = string.Equals(entry.ContainerKey, ownerKey, StringComparison.OrdinalIgnoreCase);

                // Taken out of the container itself: the whole entry goes, placements and all. An
                // entry that still carries leveled lists is NOT a placement into the container (see
                // the header), so it is left standing.
                if (ownerIsContainer)
                {
                    bool placesIntoTheContainer = !entry.Placements.Any(p => p.Value.Level > 0);
                    if (isTheContainer && placesIntoTheContainer)
                    {
                        removedSomething = true;
                        continue;
                    }

                    kept.Add(Unchanged(entry));
                    continue;
                }

                // Taken out of a leveled list: drop that list wherever it appears. Enumerated once
                // and filtered into a new list rather than removed from the dictionary - the order
                // is the string's, and a dictionary stops promising one as soon as it is mutated.
                var remaining = entry.Placements
                    .Where(p => !string.Equals(p.Key, ownerKey, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (p.Key, p.Value.Level, p.Value.Count))
                    .ToList();

                if (remaining.Count == entry.Placements.Count)
                {
                    kept.Add(Unchanged(entry));
                    continue;
                }

                removedSomething = true;

                // THE EMPTY-ENTRY TRAP, see the header: this entry had leveled lists and has none
                // left, so keeping it would turn it into a placement into the container itself.
                if (!remaining.Any(p => p.Level > 0)) continue;

                kept.Add(new ContainerStringEntry(entry.ContainerKey, remaining));
            }

            // Handed back untouched when nothing matched, rather than rebuilt into the canonical
            // form. The parser accepts spacing the writer does not produce, so rewriting a string we
            // did not change would mark the item as edited for a difference nobody made - which is
            // the byte stability ContainerStringBuilder's own comment is about.
            if (!removedSomething) return containerString;

            return ContainerStringBuilder.Build(kept);
        }

        private static ContainerStringEntry Unchanged(ParsedContainerEntry entry)
            => new(entry.ContainerKey,
                   entry.Placements.Select(p => (p.Key, p.Value.Level, p.Value.Count)).ToList());
    }
}
