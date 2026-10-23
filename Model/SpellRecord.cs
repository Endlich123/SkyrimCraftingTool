using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool.Model
{
    // One SPEL, as the Spells tab works with it.
    //
    // Deliberately shaped like EnchantmentRecord rather than invented fresh: Mutagen hands SPEL and
    // ENCH the SAME effect type, SkyPatcher gives them near-identical operation sets, and the two
    // tabs answer the same question about different records. Where they differ, they differ for a
    // reason that is written down at the field.
    //
    // WHAT IS NOT HERE, and will not be: there is no BaseSpell. The whole family/tier machinery on
    // the enchantment side (EnchantmentFamily) hangs off BaseEnchantment, and spells have no
    // equivalent - they are not a ladder over a shared base.
    public class SpellRecord : INotifyPropertyChanged
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Raised so a view model can autosave an edit made straight against this record. Wired in
        // S-P3; the display-only tab leaves it unsubscribed.
        public event Action<string> FieldChanged;

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

        // Spell / Disease / Power / LesserPower / Ability / Poison / Addiction / Voice.
        //
        // READ-ONLY on a scanned record, and not for want of trying: SkyPatcher has no operation for
        // it. It is also the tree's grouping level, so changing it would move the record under the
        // user mid-edit - the same pair of reasons that makes CastType read-only on an enchantment.
        public string SpellType { get; set; } = "";

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

        // No SkyPatcher operation either, so no shadow column and no editing on a scanned record.
        public string TargetType { get; set; } = "";

        private int _baseCost;
        public int BaseCost
        {
            get => _baseCost;
            set
            {
                if (_baseCost == value) return;
                _baseCost = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(BaseCost));
            }
        }

        private float _chargeTime;
        public float ChargeTime
        {
            get => _chargeTime;
            set
            {
                if (Math.Abs(_chargeTime - value) < 0.0001f) return;
                _chargeTime = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(ChargeTime));
            }
        }

        // The perk that halves this spell's cost. Patchable (halfCostPerk=), which is the one
        // operation the spell patcher has and the enchantment one does not.
        private string _halfCostPerkKey = "";
        public string HalfCostPerkKey
        {
            get => _halfCostPerkKey;
            set
            {
                if (_halfCostPerkKey == value) return;
                _halfCostPerkKey = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(HalfCostPerkKey));
            }
        }

        // The raw dword. NOT a name list: SkyPatcher's flag names do not match Mutagen's, and three
        // bits that 415 spells in a real load order actually set have no name in either. Reading
        // through an enum would drop them silently.
        private int _flags;
        public int Flags
        {
            get => _flags;
            set
            {
                if (_flags == value) return;
                _flags = value;
                OnPropertyChanged();
                FieldChanged?.Invoke(nameof(Flags));
            }
        }

        // Scan-only, no shadow column. Measured over 127 plugins: CastDuration is set on 32 spells,
        // SpellRange on 322, Description on 124 - and Keywords on none at all.
        public float CastDuration { get; set; }
        public float SpellRange { get; set; }
        public string Description { get; set; } = "";
        public string Keywords { get; set; } = "";

        // Set from Spells.LastChanged at load, flipped on the first live edit. Drives the tree badge
        // and the "only edited" filter, as EnchantmentRecord.IsEdited does.
        private bool _isEdited;
        public bool IsEdited
        {
            get => _isEdited;
            set { if (_isEdited != value) { _isEdited = value; OnPropertyChanged(); } }
        }

        // False for a spell the user created in the tool: it exists in no plugin and leaves through
        // the generated ESP rather than as a SkyPatcher rule.
        public bool IsOriginal { get; set; } = true;

        public bool IsUserCreated => !IsOriginal;

        // How many of this spell's effect entries the database could not keep.
        //
        // PRIMARY KEY(SpellKey, MagicEffectKey) holds one row per pair, and a SPEL may legitimately
        // name the same magic effect twice with different numbers. Counted at scan time because the
        // duplicates are gone afterwards - and SHOWN, because a record displayed as complete when it
        // is not is worse than one that admits the gap. SkyPatcher cannot address the second
        // instance either, so this is the honest shape of what is patchable.
        public int DroppedEffectRows { get; set; }

        public bool HasDroppedEffectRows => DroppedEffectRows > 0;

        public ObservableCollection<SpellEffectRecord> Effects { get; set; } = new();

        // The magic school, DERIVED from the effects rather than stored.
        //
        // A SPEL HAS NO SCHOOL - checked against Mutagen, the property does not exist. The school
        // belongs to each magic effect (MGEF.MagicSkill), and the game files a spell under the school
        // of its COSTLIEST effect (Services/SpellCategory). One school, as the game shows one - the
        // first Spells tab listed every school the effects touched, which is not what the menu does.
        //
        // Only a plain Spell has one. A power, a shout word or an ability is filed by its type, so
        // its effects' schools say nothing about where the game shows it.
        public string School =>
            SpellType == "Spell" ? Services.SpellCategory.SchoolOf(Effects) : "";

        // The tier of that same costliest effect - Novice .. Master, what the menu shows next to it.
        public string SkillTier
        {
            get
            {
                if (School.Length == 0) return "";
                return Services.SpellCategory.Costliest(Effects)?.SkillTier ?? "";
            }
        }

        // Where the tree files the spell: a school, or Powers / Shouts / Abilities / Other.
        public string Category => Services.SpellCategory.Of(this);

        // The panel's "Filed under" line: the school with its tier, the shout and word, or the
        // category on its own.
        public string CategoryLabel =>
            School.Length > 0 ? SchoolLabel :
            IsShoutWord ? $"{Category} - {ShoutLabel}" :
            Category;

        // The half-cost perk's EditorID, read alongside the key - only for the mismatch note below.
        // Notifies, because the perk picker changes it while the record is on screen.
        private string _halfCostPerkEditorId = "";
        public string HalfCostPerkEditorId
        {
            get => _halfCostPerkEditorId;
            set
            {
                if (_halfCostPerkEditorId == (value ?? "")) return;
                _halfCostPerkEditorId = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HalfCostPerkMismatch));
                OnPropertyChanged(nameof(HasHalfCostPerkMismatch));
            }
        }

        // A half-cost perk from another school than the one the spell is filed under. The game does
        // not mind - the perk halves whatever spell names it - but it is almost always a copy-paste
        // slip in a mod: a Destruction spell made cheaper by "AlterationAdept50". Judged by the
        // vanilla naming (School + Tier), so a perk named otherwise is never flagged.
        public string HalfCostPerkMismatch
        {
            get
            {
                string school = School;
                string perk = HalfCostPerkEditorId ?? "";
                if (school.Length == 0 || perk.Length == 0) return "";

                foreach (var other in new[] { "Alteration", "Conjuration", "Destruction", "Illusion", "Restoration" })
                {
                    if (perk.StartsWith(other, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(other, school, StringComparison.OrdinalIgnoreCase))
                        return $"This perk belongs to {other}, but the spell is filed under {school}. " +
                               $"The {other} perk makes it cheaper; {school} perks do not.";
                }

                return "";
            }
        }

        public bool HasHalfCostPerkMismatch => HalfCostPerkMismatch.Length > 0;

        // The equip slot the spell takes (EQUP key). Shown, never patched - SkyPatcher has no
        // operation for it.
        public string EquipType { get; set; } = "";

        // For a Voice spell: the shout whose word casts it, and which word. The game lists the
        // shout ("Unrelenting Force"), not the three SPELs behind its words.
        public string ShoutName { get; set; } = "";
        public int ShoutWord { get; set; }

        public bool IsShoutWord => ShoutWord > 0;

        public string ShoutLabel => IsShoutWord ? $"{ShoutName} - word {ShoutWord}" : "";

        // "Destruction (Adept)" for the panel, empty when the spell belongs to no school.
        public string SchoolLabel
        {
            get
            {
                string school = School;
                return school.Length == 0 ? "" : $"{school} ({SkillTier})";
            }
        }

        public string Plugin => Key.Contains('|') ? Key.Split('|')[0] : Key;
        public string FormID => Key.Contains('|') ? Key.Split('|')[1] : "";

        // What the tree row shows. EditorID is what a mod author and xEdit both go by; the display
        // name is the fallback for the records that have none.
        // A shout word leads with its shout, because that is the only name anyone knows it by.
        public string Label =>
            IsShoutWord ? $"{ShoutLabel}  ({(string.IsNullOrWhiteSpace(EditorID) ? Key : EditorID)})" :
            !string.IsNullOrWhiteSpace(EditorID) ? EditorID :
            !string.IsNullOrWhiteSpace(Name) ? Name : Key;
    }
}
