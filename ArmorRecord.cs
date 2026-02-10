
using Mutagen.Bethesda.Plugins;
using System.Collections.Generic;
using System.ComponentModel;


namespace SkyrimCraftingTool;
public class ArmorRecord : IGameRecord, INotifyPropertyChanged
{
    public string EditorID { get; set; }
    public string SourceEspPath { get; set; }
    public uint Value { get; set; }
    public float Weight { get; set; }
    public string Workbench { get; set; }
    public List<string> Vendor { get; set; } = new();
    public float ArmorRating { get; set; }
    public FormKey FormKey { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Slots { get; set; } = new();
    private ArmorSlot? _selectedSlot; 
    public ArmorSlot SelectedSlot { get; set; }


    public Dictionary<string, int> Materials { get; set; } = new();
    public List<MaterialEntry> MaterialList { get; set; }

    public override string ToString()
    {
        return $"{EditorID} | Gewicht: {Weight} | Wert: {Value} | ArmorRating: {ArmorRating}";
    }
    public event PropertyChangedEventHandler? PropertyChanged; protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ArmorRecord() { MaterialList = Materials.Select(kvp => new MaterialEntry { Material = kvp.Key, Amount = kvp.Value }).ToList(); }
}
