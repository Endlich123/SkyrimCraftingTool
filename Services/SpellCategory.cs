using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Where the game files a spell - and therefore where the Spells tab's tree puts it.
    //
    // TWO STEPS, the same two the magic menu takes (docs/Spells-Plan.md):
    //
    //   1. SpellType, which sits on the SPEL itself. Power and Lesser Power go to "Powers", Voice to
    //      "Shouts", Ability never reaches the menu at all (it is an active effect), Disease/Poison/
    //      Addiction likewise. None of them has a school.
    //   2. Only a plain Spell gets a school, and a SPEL has no school field: the game takes the
    //      COSTLIEST effect and files the spell under that effect's MGEF.MagicSkill. A spell whose
    //      costliest effect has no school shows only under "All" in the game - "No school" here.
    //
    // The first Spells tab named every school a spell's effects touched ("Destruction / Illusion").
    // The game shows one category, and the tree shows what the game shows.
    public static class SpellCategory
    {
        public const string NoSchool = "No school";
        public const string Powers = "Powers";
        public const string Shouts = "Shouts";
        public const string Abilities = "Abilities";
        public const string Other = "Other";

        // Alphabetical, as the game's menu lists them.
        private static readonly string[] Schools =
            { "Alteration", "Conjuration", "Destruction", "Illusion", "Restoration" };

        public static string Of(SpellRecord spell)
        {
            if (spell == null) return Other;

            switch (spell.SpellType)
            {
                case "Spell":
                    var school = SchoolOf(spell.Effects);
                    return string.IsNullOrEmpty(school) ? NoSchool : school;
                case "Power":
                case "LesserPower":
                    return Powers;
                case "Voice":
                    return Shouts;
                case "Ability":
                    return Abilities;
                default:
                    return Other;
            }
        }

        // The tree's fixed order: the five schools, then the rest roughly by how often anyone goes
        // looking. Unknown labels last, alphabetically.
        public static int Order(string category)
        {
            int school = Array.IndexOf(Schools, category);
            if (school >= 0) return school;

            return category switch
            {
                NoSchool => 5,
                Powers => 6,
                Shouts => 7,
                Abilities => 8,
                Other => 9,
                _ => 10,
            };
        }

        // The school of the costliest effect, or "" when it has none. "None" is what Mutagen calls
        // an effect without a school, and it means the same as no value at all.
        public static string SchoolOf(IEnumerable<SpellEffectRecord> effects)
        {
            var costliest = Costliest(effects);
            if (costliest == null) return "";

            var school = costliest.School ?? "";
            return school == "None" || Array.IndexOf(Schools, school) < 0 ? "" : school;
        }

        // The effect the game files the spell under. First one wins a tie, as list order is the only
        // other thing the record says about them.
        public static SpellEffectRecord? Costliest(IEnumerable<SpellEffectRecord> effects)
        {
            SpellEffectRecord? best = null;
            double bestCost = double.MinValue;

            foreach (var e in effects ?? Enumerable.Empty<SpellEffectRecord>())
            {
                double cost = EffectCost(e.EffectBaseCost, e.Magnitude, e.Duration);
                if (cost > bestCost)
                {
                    best = e;
                    bestCost = cost;
                }
            }

            return best;
        }

        // The UESP formula, without the area term: BaseCost x Magnitude^1.1 x Duration/10, where a
        // magnitude or duration of 0 contributes a factor of 1 rather than zeroing the cost. Good
        // enough to pick the costliest effect; not meant to show a magicka number.
        public static double EffectCost(double baseCost, double magnitude, int duration)
        {
            double mag = magnitude > 0 ? Math.Pow(magnitude, 1.1) : 1;
            double dur = duration > 0 ? duration / 10.0 : 1;
            return baseCost * mag * dur;
        }
    }
}
