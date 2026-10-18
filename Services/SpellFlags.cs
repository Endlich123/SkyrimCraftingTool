using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    // The SPIT flag bits, by both names they go under.
    //
    // WHY A TABLE AND NOT AN ENUM PARSE. SkyPatcher's flag names and Mutagen's do not correspond:
    //
    //     SkyPatcher          Mutagen (SpellDataFlag)      bit
    //     costoverride        ManualCostCalc               0x1
    //     pcstartspell        PCStartSpell                 0x20000
    //     ignoreloscheck      AreaEffectIgnoresLOS         0x80000
    //     ignoreresistance    IgnoreResistance             0x100000
    //     noabsorb            NoAbsorbOrReflect            0x200000
    //     nodualcastmods      NoDualCastModification       0x800000
    //
    // Four of the six differ by name alone. Parsing SkyPatcher's names through Mutagen's enum would
    // find two of them and silently drop the rest; writing Mutagen's names into an INI would emit
    // four the patcher does not know. Neither failure announces itself.
    //
    // And measured over a real load order, three bits are in genuine use that NEITHER side names:
    // 0x10000 on 187 spells, 0x40000 on 187, 0x400000 on 41. Reading flags through any enum would
    // have thrown away 415 settings without a word - which is why Spells.Flags is a raw dword.
    public static class SpellFlags
    {
        // SkyPatcher's name, the bit, and a label for the window. Only the bits SkyPatcher can
        // actually write are listed: a checkbox for a flag no rule can set is a lie.
        public static IReadOnlyList<(string PatcherName, int Bit, string Label)> Known { get; } = new[]
        {
            ("costoverride",     0x1,       "Manual cost calculation"),
            ("pcstartspell",     0x20000,   "Player start spell"),
            ("ignoreloscheck",   0x80000,   "Area effect ignores line of sight"),
            ("ignoreresistance", 0x100000,  "Ignores resistance"),
            ("noabsorb",         0x200000,  "No absorb or reflect"),
            ("nodualcastmods",   0x800000,  "No dual-cast modifications"),
        };

        // The three names docs/Spell_Patcher.txt lists that have no counterpart on a SPEL.
        //
        // NOT OFFERED, on purpose. 'fooditem' is visibly an ingestible flag that came along with the
        // shared documentation template; the other two have no bit anyone has pinned down. The
        // patcher doc's ORDER puts instantcast between pcstartspell (0x20000) and ignoreloscheck
        // (0x80000), which makes 0x40000 the obvious candidate - and 0x40000 is one of the unnamed
        // bits that 187 spells set. That is a hypothesis from a table of contents, not a
        // measurement, so it stays out until someone confirms it against a record.
        public static IReadOnlyList<string> DocumentedButUnmapped { get; } = new[]
        {
            "fooditem", "extendduration", "instantcast",
        };

        // A display label for one bit, or null when nothing names it. Null rather than "Unknown" so
        // the caller can decide to print the bit itself, which is more use than a word that says
        // nothing.
        public static string Name(int bit)
            => Known.FirstOrDefault(k => k.Bit == bit).Label;

        // The SkyPatcher name for one bit, for the rule builder in S-P4. Null when the bit cannot be
        // written - the rule builder must leave such a bit alone rather than invent a name.
        public static string PatcherName(int bit)
            => Known.FirstOrDefault(k => k.Bit == bit).PatcherName;

        // Every bit set in the value that SkyPatcher can name. The ones it cannot are deliberately
        // left out: setFlags/removeFlags work by name, so an unnameable bit is one the tool must not
        // claim to control.
        public static IEnumerable<string> PatcherNamesIn(int flags)
            => Known.Where(k => (flags & k.Bit) != 0).Select(k => k.PatcherName);

        // The bits a value carries that nothing names. Reported rather than hidden: they are real,
        // they are set on hundreds of records, and a window that shows a spell's flags as "none"
        // when three bits are on would be wrong.
        public static IEnumerable<int> UnnamedBitsIn(int flags)
        {
            for (int bit = 0; bit < 32; bit++)
            {
                int mask = 1 << bit;
                if ((flags & mask) != 0 && Name(mask) == null) yield return mask;
            }
        }
    }
}
