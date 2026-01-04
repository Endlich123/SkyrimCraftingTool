using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool;

public class SharedInfoPanelVM : INotifyPropertyChanged
{
    public IGameRecord Record { get; }

    public SharedInfoPanelVM(IGameRecord record)
    {
        Record = record;
    }

    public string EditorID
    {
        get => Record.EditorID;
        set
        {
            if (Record.EditorID != value)
            {
                Record.EditorID = value;
                OnPropertyChanged();
            }
        }
    }

    public string FormKey
    {
        get => Record.FormKey.ToString();
        set
        {
            if (Record.FormKey.ToString() != value)
            {
                if (Mutagen.Bethesda.Plugins.FormKey.TryFactory(value, out var fk))
                {
                    Record.FormKey = fk;
                    OnPropertyChanged();
                }
                else
                {
                    // Optional: handle invalid input
                }
            }
        }
    }


    public int Value
    {
        get => (int)Record.Value;
        set
        {
            if (Record.Value != value)
            {
                Record.Value = (uint)value;
                OnPropertyChanged();
            }
        }
    }

    public float Weight
    {
        get => Record.Weight;
        set
        {
            if (Record.Weight != value)
            {
                Record.Weight = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
