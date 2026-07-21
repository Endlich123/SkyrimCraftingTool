using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using SkyrimCraftingTool.Services.EnchantmentFilter;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentEffectViewModel : INotifyPropertyChanged
    {
        public EnchantmentEffectRecord Model { get; }

        public MagicEffectsRecords SelectedMagicEffect
        {
            get => _selectedMagicEffect;
            set
            {
                if (_selectedMagicEffect != value)
                {
                    _selectedMagicEffect = value;
                    ApplyMagicEffectRules();
                    OnPropertyChanged();
                }
            }
        }

        public bool AllowsMagnitude => SelectedMagicEffect?.HasMagnitude ?? true;
        public bool AllowsDuration => SelectedMagicEffect?.HasDuration ?? true;
        public bool AllowsArea => SelectedMagicEffect?.HasArea ?? true;

        public ObservableCollection<MagicEffectsRecords> AllMagicEffects { get; }

        public EnchantmentEffectViewModel(
            EnchantmentEffectRecord model,
            IEnumerable<MagicEffectsRecords> allEffects,
            EnchantmentCategory category)
        {
            Model = model;

            // Filter anwenden
            //AllMagicEffects = new ObservableCollection<MagicEffectsRecords>(
            //    allEffects.Where(m => MagicEffectFilter.IsValidEffectForEnchantment(m, category))
            //);

            AllMagicEffects = new ObservableCollection<MagicEffectsRecords>(allEffects);

            SelectedMagicEffect = AllMagicEffects
                .FirstOrDefault(x => x.Key == model.MagicEffectKey);
        }


        private void ApplyMagicEffectRules()
        {
            // Update model key
            Model.MagicEffectKey = SelectedMagicEffect?.Key;
            Model.Name = SelectedMagicEffect.Name;
            Model.EditorID = SelectedMagicEffect.EditorID;

            // Magnitude
            if (!AllowsMagnitude)
                Model.Magnitude = 0;

            // Duration
            if (!AllowsDuration)
                Model.Duration = 0;

            // Area
            if (!AllowsArea)
                Model.Area = 0;

            // Notify UI
            OnPropertyChanged(nameof(AllowsMagnitude));
            OnPropertyChanged(nameof(AllowsDuration));
            OnPropertyChanged(nameof(AllowsArea));
            OnPropertyChanged(nameof(Model));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private MagicEffectsRecords _selectedMagicEffect;
    }

}
