using System;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.ViewModel
{
    // One editable effect row of a spell.
    //
    // Thin on purpose: it wraps the record, notifies, and tells its owner that something changed.
    // It does NOT save - the view model owns the write, because a save is a statement about the
    // whole spell's effect list and a row cannot make it alone.
    public sealed class SpellEffectVM : ViewModelBase
    {
        public SpellEffectRecord Model { get; }

        // Raised on every committed change. The view model debounces and writes.
        public event Action Changed;

        public SpellEffectVM(SpellEffectRecord model, SpellEffectRecord original)
        {
            Model = model;
            _original = original;
        }

        // What the scan had. Kept beside the live value so a row can say it has been changed
        // without asking the database on every keystroke.
        private readonly SpellEffectRecord _original;

        public string Label => Model.Label;
        public string MagicEffectKey => Model.MagicEffectKey;
        public string SchoolLabel => Model.SchoolLabel;

        // Which of the three numbers this magic effect even has.
        //
        // READ OFF THE MGEF, not guessed: a Soul Trap effect has no magnitude and a Fortify one no
        // area, and a box that takes a number the record cannot hold writes a value the game
        // ignores. The catalogue knows (MagicEffects.HasMagnitude/HasDuration/HasArea), so the box
        // is disabled rather than silently useless.
        //
        // True when the effect is not in the catalogue at all - 110 effect rows of 8.302 measured
        // point at an MGEF outside the scan. Disabling everything there would make the row look
        // broken when it is merely unknown.
        public bool AllowsMagnitude => _catalogue?.HasMagnitude ?? true;
        public bool AllowsDuration => _catalogue?.HasDuration ?? true;
        public bool AllowsArea => _catalogue?.HasArea ?? true;

        private MagicEffectsRecords _catalogue;

        internal void AttachCatalogue(MagicEffectsRecords entry)
        {
            _catalogue = entry;
            OnPropertyChanged(nameof(AllowsMagnitude));
            OnPropertyChanged(nameof(AllowsDuration));
            OnPropertyChanged(nameof(AllowsArea));
        }

        public float Magnitude
        {
            get => Model.Magnitude;
            set
            {
                if (Math.Abs(Model.Magnitude - value) < 0.0001f) return;
                Model.Magnitude = value;
                OnPropertyChanged();
                RaiseChanged();
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
                RaiseChanged();
            }
        }

        // In game units, not feet. SkyPatcher's own documentation shows a 15-foot fireball patched
        // to 5 with an area of 7, so the two are not the same number and the field is left as the
        // record states it.
        public int Area
        {
            get => Model.Area;
            set
            {
                if (Model.Area == value) return;
                Model.Area = value;
                OnPropertyChanged();
                RaiseChanged();
            }
        }

        // Differs from what the scan found. Per FIELD rather than per row, so the window can mark
        // the one number the user actually moved instead of lighting up the whole line.
        public bool IsMagnitudeEdited => _original != null && Math.Abs(_original.Magnitude - Model.Magnitude) > 0.0001f;
        public bool IsDurationEdited => _original != null && _original.Duration != Model.Duration;
        public bool IsAreaEdited => _original != null && _original.Area != Model.Area;

        public bool IsEdited => IsMagnitudeEdited || IsDurationEdited || IsAreaEdited;

        private void RaiseChanged()
        {
            OnPropertyChanged(nameof(IsMagnitudeEdited));
            OnPropertyChanged(nameof(IsDurationEdited));
            OnPropertyChanged(nameof(IsAreaEdited));
            OnPropertyChanged(nameof(IsEdited));
            Changed?.Invoke();
        }

        // Puts this row back to the scanned numbers WITHOUT announcing a change - the caller is
        // resetting, and a notification here would have the view model save the revert as a fresh
        // edit. The enchantment path unsubscribes its autosave around Reset for the same reason;
        // doing it in one place is less to remember.
        internal void RevertSilently()
        {
            if (_original == null) return;

            Model.Magnitude = _original.Magnitude;
            Model.Duration = _original.Duration;
            Model.Area = _original.Area;

            OnPropertyChanged(nameof(Magnitude));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(Area));
            OnPropertyChanged(nameof(IsMagnitudeEdited));
            OnPropertyChanged(nameof(IsDurationEdited));
            OnPropertyChanged(nameof(IsAreaEdited));
            OnPropertyChanged(nameof(IsEdited));
        }
    }
}
