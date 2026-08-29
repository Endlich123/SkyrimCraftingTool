using Mutagen.Bethesda.Skyrim;
using System;
using System.Collections.Generic;
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
                }
                    
            }
        }

        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        private bool _runOnPlayer = true; // Mostly Subject (Player) in the COBJ context
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

        // Shared comparison value of the condition, mapped to the concrete property depending on type
        public abstract float ComparisonValue { get; set; }

        // Helper to later convert the UI condition back into a real Mutagen condition
        public abstract ConditionFloat ToMutagenCondition();
    }
}
