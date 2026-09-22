using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // One planned change, as the preview window shows it. The numbers are editable: the factor is a
    // proposal read out of the child's own values, not a law - see EnchantmentFamily for what the
    // load order actually says about tier ladders.
    public sealed class FamilyChangeRowVM : ViewModelBase
    {
        public FamilyEffectChange Change { get; }

        public FamilyChangeRowVM(FamilyEffectChange change)
        {
            Change = change;
            _magnitude = change.Magnitude;
            _duration = change.Duration;
            _area = change.Area;
        }

        public string ChildName => string.IsNullOrWhiteSpace(Change.Child.EditorID)
            ? Change.Child.Key
            : Change.Child.EditorID;

        public string EffectLabel => Change.Label;
        public bool IsRemoval => Change.IsRemoval;
        public string Action => Change.IsRemoval ? "remove" : "add";

        public string FactorText => Change.Factor == null ? "—" : $"×{Change.Factor.Value:0.00}";
        public string Note => Change.Note;
        public bool NeedsAttention => Change.NeedsAttention;

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private float _magnitude;
        public float Magnitude
        {
            get => _magnitude;
            set => SetProperty(ref _magnitude, value);
        }

        private int _duration;
        public int Duration
        {
            get => _duration;
            set => SetProperty(ref _duration, value);
        }

        private int _area;
        public int Area
        {
            get => _area;
            set => SetProperty(ref _area, value);
        }
    }

    // The review step for "apply this enchantment's effect list to its tier variants".
    //
    // A preview rather than a silent run, because the proposal can be wrong in ways only the person
    // can judge: roughly one child in ten has nothing to derive a factor from, and where a child
    // shares several effects with its base they frequently disagree about it.
    public sealed class EnchantmentFamilyVM : ViewModelBase
    {
        public ObservableCollection<FamilyChangeRowVM> Rows { get; } = new();

        public string Summary { get; }

        public bool Accepted { get; private set; }

        public ICommand ApplyCommand { get; }
        public ICommand CancelCommand { get; }

        public EnchantmentFamilyVM(EnchantmentRecord parent, IReadOnlyList<FamilyEffectChange> plan,
                                   int childCount, System.Action<bool> close)
        {
            foreach (var change in plan)
                Rows.Add(new FamilyChangeRowVM(change));

            int adds = plan.Count(p => !p.IsRemoval);
            int removals = plan.Count(p => p.IsRemoval);
            int attention = plan.Count(p => p.NeedsAttention);

            var name = string.IsNullOrWhiteSpace(parent.EditorID) ? parent.Key : parent.EditorID;
            Summary =
                $"{name} has {childCount} variant(s) pointing at it. " +
                $"{adds} effect(s) would be added, {removals} removed." +
                (attention > 0
                    ? $"  {attention} row(s) need a look — see the note on each."
                    : "");

            ApplyCommand = new RelayCommand(() => { Accepted = true; close(true); });
            CancelCommand = new RelayCommand(() => { Accepted = false; close(false); });
        }

        public IReadOnlyList<FamilyChangeRowVM> Selected => Rows.Where(r => r.IsSelected).ToList();
    }
}
