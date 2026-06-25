namespace SkyrimCraftingTool.Model
{
    public class WeaponRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public float Weight { get; set; }
        public int Value { get; set; }
        public int Damage { get; set; }

        // NEW
        public float Speed { get; set; }
        public float Reach { get; set; }
        public float Stagger { get; set; }

        // Liste von Plugin|FormID
        public List<string> Keywords { get; set; } = new();
    }
}
