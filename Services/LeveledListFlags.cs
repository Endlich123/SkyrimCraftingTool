using System;

namespace SkyrimCraftingTool.Services
{
    public enum CalcFlagMode
    {
        // No flag operation is written: the list keeps whatever it has. The default for every list
        // nobody edited, and different from None, which actively clears the flags.
        Unchanged,

        // calcForLevel: "Calculate from all levels <= player's level"
        ForLevel,

        // calcEachItem: "Calculate for each item in count"
        EachItem,

        // calcForLevelAndEachItem: both of the above
        ForLevelAndEachItem,

        // calcUseAll: "Use All" - every entry is handed out, nothing is drawn
        UseAll,

        // clearFlags: no calculation flags at all
        None,
    }

    public static class LeveledListFlags
    {
        // Mutagen writes the record's flags as their enum names, which is what the scan stores
        // (ItemDBHandler.Scan.cs). Read back by name rather than by bit value because that is the
        // form in the database - and because a name survives a Mutagen version bump that renumbers
        // nothing but would still break a hand-rolled bit mask.
        private const string AllLevelsName = "CalculateFromAllLevels";
        private const string EachItemName = "CalculateForEachItemInCount";
        private const string UseAllName = "UseAll";
        private const string SpecialLootName = "SpecialLoot";

        public static bool HasAllLevels(string? flags) => Contains(flags, AllLevelsName);
        public static bool HasEachItem(string? flags) => Contains(flags, EachItemName);
        public static bool HasUseAll(string? flags) => Contains(flags, UseAllName);

        public static bool HasSpecialLoot(string? flags) => Contains(flags, SpecialLootName);

        private static bool Contains(string? flags, string name)
            => !string.IsNullOrWhiteSpace(flags)
               && flags.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;

        // What the list has right now, expressed in the patcher's vocabulary. Used as the starting
        // value of the editor, so "unchanged" and "what it already is" are the same thing on screen.
        public static CalcFlagMode FromRecord(string? flags)
        {
            if (HasUseAll(flags)) return CalcFlagMode.UseAll;

            bool all = HasAllLevels(flags), each = HasEachItem(flags);

            if (all && each) return CalcFlagMode.ForLevelAndEachItem;
            if (all) return CalcFlagMode.ForLevel;
            if (each) return CalcFlagMode.EachItem;

            return CalcFlagMode.None;
        }

        // The operation SkyPatcher wants, or "" for Unchanged - which writes nothing at all rather
        // than writing a no-op, because a rule that sets a flag to what it already was still counts
        // as this tool touching that list.
        public static string ToOperation(CalcFlagMode mode) => mode switch
        {
            CalcFlagMode.ForLevel => "calcForLevel=true",
            CalcFlagMode.EachItem => "calcEachItem=true",
            // "calcLevelAndEachItem", NOT "calcForLevelAndEachItem" as docs/Leveled_List_Patcher.txt
            // spells it. The installed SkyPatcher 6.4.1 contains the first spelling in its parser
            // table and the second one nowhere at all - so the documented form matches nothing and
            // the operation is dropped without a word. The other four tokens agree with the doc.
            // Worth confirming in the play test along with the flag semantics.
            CalcFlagMode.ForLevelAndEachItem => "calcLevelAndEachItem=true",
            CalcFlagMode.UseAll => "calcUseAll=true",
            CalcFlagMode.None => "clearFlags=true",
            _ => "",
        };

        // For the dropdown and for the report. Plain language, because "CalculateFromAllLevels
        // LessThanOrEqualPlayer" tells a modder something and a player nothing.
        public static string Describe(CalcFlagMode mode) => mode switch
        {
            CalcFlagMode.ForLevel => "From all levels up to yours",
            CalcFlagMode.EachItem => "Roll separately for each item in the count",
            CalcFlagMode.ForLevelAndEachItem => "From all levels up to yours, and per item in the count",
            CalcFlagMode.UseAll => "Use all entries (no draw at all)",
            CalcFlagMode.None => "No calculation flags",
            _ => "Leave as it is",
        };

        // What the odds engine needs: the two bits that change how a roll is calculated. Unchanged
        // means "read them off the record", which is why the record's own flags come in as well.
        public static (bool AllLevels, bool EachItem, bool UseAll) Resolve(CalcFlagMode mode, string? recordFlags)
            => mode switch
            {
                CalcFlagMode.ForLevel => (true, false, false),
                CalcFlagMode.EachItem => (false, true, false),
                CalcFlagMode.ForLevelAndEachItem => (true, true, false),
                CalcFlagMode.UseAll => (false, false, true),
                CalcFlagMode.None => (false, false, false),
                _ => (HasAllLevels(recordFlags), HasEachItem(recordFlags), HasUseAll(recordFlags)),
            };
    }
}
