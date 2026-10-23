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

        // Editable only for records the user created (see EnchantmentMenuVM.CanEditEditorId): a
        // scanned record's EditorID belongs to its plugin, and there is no shadow column to hold an
        // override anyway. Notifies like Name does, and for the same two reasons - the tree row
        // binds straight to this, so a rename shows up there without rebuilding the tree.
        private string _editorId = "";
        public string EditorID
        {
            get => _editorId;
            set
            {
                if (_editorId == value) return;
                _editorId = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(EditorID));
            }
        }

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

        // Editable on user-created records only. SkyPatcher does have castType=, so a scanned record
        // COULD be patched - the restriction is a product decision, not a technical one: cast type
        // is what decides whether an enchantment is an armor or a weapon one, and the tree is built
        // from it.
        private string _castType = "";
        public string CastType
        {
            get => _castType;
            set
            {
                if (_castType == value) return;
                _castType = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(CastType));
            }
        }

        // --- The rest of ENIT ---

        // "Enchantment" or "StaffEnchantment". User-created records only: SkyPatcher has no
        // operation for it at all, so on a scanned record the edit could never reach the game
        // without a full ESP override.
        private string _enchantType = "";
        public string EnchantType
        {
            get => _enchantType;
            set
            {
                if (_enchantType == value) return;
                _enchantType = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(EnchantType));
            }
        }

        // The raw ENIT flag dword. Kept as a number rather than named flags because SkyPatcher knows
        // a third one ("fooditem") that Mutagen's ObjectEffect.Flag does not name - see the
        // Enchantments schema comment. The individual bits are exposed below for binding.
        private int _flags;
        public int Flags
        {
            get => _flags;
            set
            {
                if (_flags == value) return;
                _flags = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoAutoCalc));
                OnPropertyChanged(nameof(ExtendDurationOnRecast));
                FieldChanged?.Invoke(nameof(Flags));
            }
        }

        // SkyPatcher calls this one "costoverride". Without it the game recalculates the cost from
        // the effects and ignores the stored number - measured across the load order: of 1943 ENCH
        // records, the 81 whose amount differs from their cost ALL carry this flag, and none without
        // it does.
        public const int FlagNoAutoCalc = 0x01;
        public const int FlagExtendDurationOnRecast = 0x04;

        public bool NoAutoCalc
        {
            get => (Flags & FlagNoAutoCalc) != 0;
            set => Flags = value ? Flags | FlagNoAutoCalc : Flags & ~FlagNoAutoCalc;
        }

        public bool ExtendDurationOnRecast
        {
            get => (Flags & FlagExtendDurationOnRecast) != 0;
            set => Flags = value ? Flags | FlagExtendDurationOnRecast : Flags & ~FlagExtendDurationOnRecast;
        }

        // Staff enchantments only - measured: all 364 records with a non-zero charge time across the
        // load order are staffs, and no non-staff uses it.
        private float _chargeTime;
        public float ChargeTime
        {
            get => _chargeTime;
            set
            {
                if (_chargeTime == value) return;
                _chargeTime = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(ChargeTime));
            }
        }

        private int _enchantmentAmount;
        public int EnchantmentAmount
        {
            get => _enchantmentAmount;
            set
            {
                if (_enchantmentAmount == value) return;
                _enchantmentAmount = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(EnchantmentAmount));
            }
        }

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

        // Created in this tool rather than scanned from a plugin (Enchantments.Original = 0). It
        // exists nowhere until the generated ESP is written, which is also why the scan must leave
        // its row alone - nothing would ever produce it again.
        public bool IsUserCreated { get; set; }

        public ObservableCollection<EnchantmentEffectRecord> Effects { get; set; }
            = new ObservableCollection<EnchantmentEffectRecord>();

        public ObservableCollection<string> WornRestrictionKeywords { get; set; }
            = new ObservableCollection<string>();


        public string Plugin => Key.Split('|')[0];
        public string FormID => Key.Split('|')[1];
    }
}
