using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
namespace SkyrimCraftingTool;

public class WeaponRecord : IGameRecord
{
    public string EditorID { get; set; }
    public string SourceEspPath { get; set; }
    public uint Value { get; set; }
    public float Weight { get; set; }
    public string Workbench { get; set; }
    public List<string> Vendor { get; set; } = new();
    public string Name { get; set; }
    public float Damage { get; set; }
    public string WeaponType { get; set; }
    public WeaponTypes WeaponTypes { get; set; }

    public FormKey FormKey { get; set; }
    public Dictionary<string, int> Materials { get; set; } = new();
    public List<MaterialEntry> MaterialList { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Effects { get; set; } = new(); // z. B. entchanments not yet used

    public WeaponRecord() { MaterialList = Materials.Select(kvp => new MaterialEntry { Material = kvp.Key, Amount = kvp.Value }).ToList(); }

}
