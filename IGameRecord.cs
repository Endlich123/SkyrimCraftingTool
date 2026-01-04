using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace SkyrimCraftingTool;

public interface IGameRecord
{
    public string EditorID { get; set; }
    public string SourceEspPath { get; set; }
    public uint Value { get; set; }
    public float Weight { get; set; }
    public string Workbench { get; set; }
    public List<string> Vendor { get; set; }
    public FormKey FormKey { get; set; }
    public List<string> Keywords { get; set; }
    public Dictionary<string, int> Materials { get; set; }
    public List<MaterialEntry> MaterialList { get; set; }
}