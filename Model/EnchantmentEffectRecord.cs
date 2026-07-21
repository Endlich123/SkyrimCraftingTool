namespace SkyrimCraftingTool.Model
{
    public class EnchantmentEffectRecord
    {
        // Plugin|FormID des ENCH
        public string EnchantmentKey { get; set; } = "";

        // Plugin|FormID des MGEF
        public string MagicEffectKey { get; set; } = "";

        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";

        public float Magnitude { get; set; }
        public int Duration { get; set; }
        public int Area { get; set; }

        public List<MagicEffectsRecords> AllMagicEffects { get; set; }
    }
}

