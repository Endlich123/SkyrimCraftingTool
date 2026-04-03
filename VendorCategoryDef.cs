namespace SkyrimCraftingTool
{
    public class VendorCategoryDef
    {
        public string CategoryKey { get; set; }      // "blacksmith"
        public string IniFileName { get; set; }      // "blacksmith.ini"
        public List<string> Vendors { get; set; }    // Vendor-Namen
    }

}
