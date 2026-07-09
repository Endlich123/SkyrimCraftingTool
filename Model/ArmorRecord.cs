namespace SkyrimCraftingTool.Model
{
    public class ArmorRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public float Weight { get; set; }
        public int Value { get; set; }
        public float ArmorRating { get; set; }

        // NEW
        public uint BodySlotMask { get; set; }        // BipedObjectSlots Bitmask

        // Liste von Plugin|FormID
        public List<string> Keywords { get; set; } = new();
    }
}

