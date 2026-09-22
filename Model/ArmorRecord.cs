namespace SkyrimCraftingTool.Model
{
    public class ArmorRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";
        public float Weight { get; set; }
        public int Value { get; set; }
        public float ArmorRating { get; set; }

        // NEW
        public uint BodySlotMask { get; set; }        // BipedObjectSlots Bitmask

        // BOD2 ArmorType: "LightArmor" / "HeavyArmor" / "Clothing". This - NOT the ArmorLight
        // keyword - is what makes the game treat a record as armor or clothing: it drives the
        // inventory category, whether an armor rating is shown at all, and whether the item can be
        // tempered. 27% of vanilla armor carries no class keyword, so it cannot be derived from one.
        public string ArmorType { get; set; } = "";

        // The ObjectEffect (ENCH) this item wears, as Plugin|FormID - empty when it has none.
        //
        // Not the same thing as the Enchantments tab: that one edits the effect itself, this says
        // WHICH effect an item carries. 2,809 of 3,674 vanilla armors and 3,010 of 3,267 weapons have
        // one, because the enchanted variants are separate records rather than a flag.
        public string ObjectEffectKey { get; set; } = "";

        // Liste von Plugin|FormID
        public List<string> Keywords { get; set; } = new();
        public string ContainerString { get; set; } = "";
    }
}

