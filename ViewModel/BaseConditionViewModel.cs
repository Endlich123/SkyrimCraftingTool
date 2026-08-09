using Mutagen.Bethesda.Skyrim;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    public enum CustomConditionType
    {
        HasPerk,
        GetIsSex,
        GetActorValue,
        GetLevel,
        GetStageDone
    }

    public abstract class BaseConditionViewModel : ViewModelBase
    {
        // NOTE: These must be INSTANCE properties (not static). WPF's plain
        // {Binding PropertyName} resolves CLR properties via TypeDescriptor,
        // which only looks at instance members - static properties are
        // silently invisible to it, leaving the bound ComboBox empty.
        public IEnumerable<CustomConditionType> ConditionTypes =>
            Enum.GetValues(typeof(CustomConditionType)).Cast<CustomConditionType>();

        public IEnumerable<string> RunOnOptions { get; } = new[] { "Subject", "Target" };

        private CustomConditionType _type;
        public virtual CustomConditionType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    OnPropertyChanged(nameof(Type));
                    Debug.WriteLine($"Condition type changed to {value}");
                }
                    
            }
        }

        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        private bool _runOnPlayer = true; // Meistens Subject (Player) im Cobj-Kontext
        public bool RunOnPlayer
        {
            get => _runOnPlayer;
            set
            {
                if (SetProperty(ref _runOnPlayer, value))
                    OnPropertyChanged(nameof(RunOn));
            }
        }

        public string RunOn
        {
            get => RunOnPlayer ? "Subject" : "Target";
            set
            {
                bool newVal = value == "Subject";
                RunOnPlayer = newVal; // triggert SetProperty + RunOn-Notify
            }
        }

        // Gemeinsamer Vergleichswert der Condition, je nach Typ auf die konkrete Eigenschaft gemappt
        public abstract float ComparisonValue { get; set; }

        // Helfer, um die UI-Bedingung später wieder in eine echte Mutagen-Condition umzuwandeln
        public abstract ConditionFloat ToMutagenCondition();
    }
}
