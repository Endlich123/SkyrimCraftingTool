using System;
using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.Model
{
    public class EnchantmentRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string EditorID { get; set; } = "";

        // Raises FieldChanged so EnchantmentMenuVM can autosave edits made directly against this
        // record (the EnchantmentView binds straight to it, with no wrapping ViewModel in between).
        public event Action<string> FieldChanged;

        private string _name = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                FieldChanged?.Invoke(nameof(Name));
            }
        }

        public string CastType { get; set; } = "";

        private float _enchantmentCost;
        public float EnchantmentCost
        {
            get => _enchantmentCost;
            set
            {
                if (_enchantmentCost == value) return;
                _enchantmentCost = value;
                FieldChanged?.Invoke(nameof(EnchantmentCost));
            }
        }

        public string TargetType { get; set; } = "";

        // Plugin|FormID of the FLST
        public string WornRestrictionListKey { get; set; } = "";

        public ObservableCollection<EnchantmentEffectRecord> Effects { get; set; }
            = new ObservableCollection<EnchantmentEffectRecord>();

        public ObservableCollection<string> WornRestrictionKeywords { get; set; }
            = new ObservableCollection<string>();


        public string Plugin => Key.Split('|')[0];
        public string FormID => Key.Split('|')[1];
    }
}
