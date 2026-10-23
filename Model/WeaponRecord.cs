namespace SkyrimCraftingTool.Model
{
    public class WeaponRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";
        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";
        public float Weight { get; set; }
        public int Value { get; set; }
        public int Damage { get; set; }

        // NEW
        public float Speed { get; set; }
        public float Reach { get; set; }
        public float Stagger { get; set; }

        // Damage of a critical hit (CRDT). Vanilla: half the base damage, rounded down - 1,858 of
        // Skyrim.esm's 2,484 weapons. 0 in a database scanned before the column existed.
        public int CritDamage { get; set; }

        // Multiplier on the game's critical-hit chance (CRDT). Almost always 1; Skyrim.esm's Iron
        // Sword has 0, so it never crits outside perk effects.
        public float CritPercentMult { get; set; }

        // The ObjectEffect (ENCH) this weapon wears, as Plugin|FormID - empty when it has none.
        // See ArmorRecord.ObjectEffectKey.
        public string ObjectEffectKey { get; set; } = "";

        // The weapon's own charge pool (EAMT). See ArmorRecord.EnchantAmount - the weapon is where
        // the missing-charge case was actually found.
        public int EnchantAmount { get; set; }

        // Liste von Plugin|FormID
        public List<string> Keywords { get; set; } = new();
        public string ContainerString { get; set; } = "";
    }
}
