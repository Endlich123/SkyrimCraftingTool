using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // One effect entry of a spell. The same four numbers an enchantment effect carries, because
    // Mutagen gives both record types the SAME effect type - there is nothing to translate.
    public class SpellEffectRecord
    {
        // Plugin|FormID of the SPEL
        public string SpellKey { get; set; } = "";

        // Plugin|FormID of the MGEF
        public string MagicEffectKey { get; set; } = "";

        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";

        public float Magnitude { get; set; }
        public int Duration { get; set; }
        public int Area { get; set; }

        public List<MagicEffectsRecords> AllMagicEffects { get; set; }

        // The magic SCHOOL this effect belongs to, and the skill tier it sits at. Read-only here:
        // both are properties of the shared MGEF record, so changing one would change the effect in
        // every spell and every enchantment that uses it - the filterByMgefs scope that was
        // explicitly not chosen for this tab.
        public string School { get; set; } = "";
        public int MinimumSkillLevel { get; set; }

        // The MGEF's base cost - what decides which effect is the spell's costliest and so which
        // school the game files the spell under (Services/SpellCategory).
        public double EffectBaseCost { get; set; }

        // Novice / Apprentice / Adept / Expert / Master, the names the game shows. The record holds
        // 0/25/50/75/100; measured over Skyrim.esm those are the only five values in use.
        public string SkillTier => MinimumSkillLevel switch
        {
            >= 100 => "Master",
            >= 75 => "Expert",
            >= 50 => "Adept",
            >= 25 => "Apprentice",
            _ => "Novice",
        };

        // "Destruction (Adept)", or just the school where it has none worth naming.
        public string SchoolLabel =>
            string.IsNullOrEmpty(School) || School == "None" ? "" : $"{School} ({SkillTier})";

        // What the row shows: the magic effect's name, its EditorID, or the bare key. A spell's
        // effect has no name of its own - it borrows the MGEF's.
        public string Label =>
            !string.IsNullOrWhiteSpace(Name) ? Name :
            !string.IsNullOrWhiteSpace(EditorID) ? EditorID : MagicEffectKey;
    }
}
