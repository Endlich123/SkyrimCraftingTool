using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

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

        // Wrapper properties over Model.* so edits raise PropertyChanged — EnchantmentEffectRecord
        // is a plain POCO with no notification of its own, which autosave depends on.
        public float Magnitude
        {
            get => Model.Magnitude;
            set
            {
                if (Model.Magnitude == value) return;
                Model.Magnitude = value;
                OnPropertyChanged();
            }
        }

        public int Duration
        {
            get => Model.Duration;
            set
            {
                if (Model.Duration == value) return;
                Model.Duration = value;
                OnPropertyChanged();
            }
        }

        public int Area
        {
            get => Model.Area;
            set
            {
                if (Model.Area == value) return;
                Model.Area = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<MagicEffectsRecords> AllMagicEffects { get; }

        public EnchantmentEffectViewModel(
            EnchantmentEffectRecord model,
            IEnumerable<MagicEffectsRecords> allEffects)
        {
            Model = model;

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
                Magnitude = 0;

            // Duration
            if (!AllowsDuration)
                Duration = 0;

            // Area
            if (!AllowsArea)
                Area = 0;

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
