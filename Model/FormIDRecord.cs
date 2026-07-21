namespace SkyrimCraftingTool.Model
{
    public class FormIDRecord
    {
        public string FormID { get; set; }
        public string Name { get; set; }
        public string Plugin { get; set; }
        public string Type { get; set; } // Keyword, Material, COBJ, Enchantment
        public string Key { get; set; } = "";

        // Fallback if GUI loads before the record is fully populated
        public override string ToString()
        {
            return Name ?? string.Empty;
        }
    }
}
