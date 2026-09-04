using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool.Model
{
    public class EnchantmentRecord : INotifyPropertyChanged
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string EditorID { get; set; } = "";

        // Raises FieldChanged so EnchantmentMenuVM can autosave edits made directly against this
        // record (the EnchantmentView binds straight to it, with no wrapping ViewModel in between).
        public event Action<string> FieldChanged;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Set from Enchantments.LastChanged at load, flipped true on the first live edit. Drives the
        // tree's edited badge + the "only edited" filter, mirroring ItemNodeVM.IsEdited.
        private bool _isEdited;
        public bool IsEdited
        {
            get => _isEdited;
            set { if (_isEdited != value) { _isEdited = value; OnPropertyChanged(); } }
        }

        // Both notifications, and they do different jobs:
        //   OnPropertyChanged -> tells the bound TextBox to redisplay. Only matters when the value
        //     is written PROGRAMMATICALLY (Reset), because a user keystroke already updates the box
        //     itself. Without it, "Reset Changes" wrote the pristine value into the model but the
        //     detail view kept showing the edited text until the tab was switched.
        //   FieldChanged -> the autosave hook (EnchantmentMenuVM unsubscribes it around Reset so
        //     reverting doesn't immediately re-save as a fresh edit).
        private string _name = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(Name));
            }
        }

        // Not user-editable in the current UI (shown read-only). If it ever becomes editable, wire
        // FieldChanged here like Name/EnchantmentCost so EnchantmentSaveHandler picks it up.
        public string CastType { get; set; } = "";

        private float _enchantmentCost;
        public float EnchantmentCost
        {
            get => _enchantmentCost;
            set
            {
                if (_enchantmentCost == value) return;
                _enchantmentCost = value;
                OnPropertyChanged();   // see Name — required for Reset to be visible
                FieldChanged?.Invoke(nameof(EnchantmentCost));
            }
        }

        public string TargetType { get; set; } = "";

        // Plugin|FormID of the FLST. Set programmatically (keyword-list edits), not directly by the
        // user — keep it a plain setter so a selection switch can't trigger a phantom save.
        public string WornRestrictionListKey { get; set; } = "";

        // Plugin|FormID of the ENCH this ObjectEffect inherits from (magnitude/duration tier
        // variants have one). Read-only scan value — no shadow column, never user-edited. Drives the
        // "↳" derived-leaf tree tag + the "only base enchantments" filter.
        public string BaseEnchantmentKey { get; set; } = "";

        public bool IsDerived =>
            !string.IsNullOrWhiteSpace(BaseEnchantmentKey)
            && !BaseEnchantmentKey.StartsWith("Null|", StringComparison.OrdinalIgnoreCase);

        public ObservableCollection<EnchantmentEffectRecord> Effects { get; set; }
            = new ObservableCollection<EnchantmentEffectRecord>();

        public ObservableCollection<string> WornRestrictionKeywords { get; set; }
            = new ObservableCollection<string>();


        public string Plugin => Key.Split('|')[0];
        public string FormID => Key.Split('|')[1];
    }
}
