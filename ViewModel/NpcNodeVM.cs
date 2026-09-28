using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One NPC: tree leaf and detail view in the same object, the pattern ItemNodeVM established.
    //
    // Editable since N-P2: every setter writes the value, persists it through the callbacks below
    // and re-raises its Is<X>Changed marker.
    //
    // What is NOT editable is as deliberate as what is: a field SkyPatcher has no operation for is
    // shown through a *Display property and has no setter at all - see docs/NPC-Plan.md §3.2. The
    // three exceptions are combat style, crime faction and perk removal, which the generator writes
    // as an ESP override instead (N-P6, §3.2).
    public class NpcNodeVM : ViewModelBase
    {
        private readonly NpcRecord _record;
        private readonly INpcLabels _labels;

        public NpcNodeVM(NpcRecord record, INpcLabels? labels = null)
        {
            _record = record ?? new NpcRecord();
            _labels = labels ?? NpcLabels.Empty;

            Factions = new ObservableCollection<NpcFactionRowVM>(
                _record.Factions
                    .OrderBy(f => _labels.Faction(f.FactionKey), StringComparer.OrdinalIgnoreCase)
                    .Select(f =>
                    {
                        var row = new NpcFactionRowVM(f, _labels);
                        WireFaction(row);
                        return row;
                    }));

            // Fixed order, not the order the record happens to list them in: this is a table people
            // read down, and 18 rows that move between NPCs are unreadable.
            Skills = new ObservableCollection<NpcSkillRowVM>(
                NpcSkillNames.All
                    .Select(s => _record.Skills.FirstOrDefault(x =>
                                string.Equals(x.Skill, s, StringComparison.OrdinalIgnoreCase))
                            ?? new NpcSkillRecord { Skill = s })
                    .Select(s =>
                    {
                        var row = new NpcSkillRowVM(s);
                        row.ValueChanged = (r, v) =>
                        {
                            _record.IsEdited = true;
                            SkillChanged?.Invoke(this, r.Skill, v);
                            OnPropertyChanged(nameof(IsEdited));
                            OnPropertyChanged(nameof(HasAnyEdit));
                        };
                        return row;
                    }));

            BuildFlagRows();
            BuildListRows();
        }

        public string Key => _record.Key;
        public string EditorID => _record.EditorID;
        public string Name => _record.Name;
        public string ShortName => _record.ShortName;
        // WHAT THE SCREEN CALLS "edited": does this record actually differ from what the scan read.
        //
        // NOT the IsEdited flag on the row, which is a different thing: that flag is set the moment
        // anything calls a setter and is never taken back, so a value written and written again
        // unchanged leaves the record marked for good. Reported as a template going orange just from
        // being opened through the template link, and the flag is why: something in the editor wrote
        // a value back on the way past, the value was identical, and the badge stayed.
        //
        // The flag still has its job - it is what the database and the patch generator read to find
        // the rows worth looking at. It is simply not what the user is being shown.
        public bool IsEdited => HasAnyEdit;

        // The raw flag. Nothing on screen reads it - it is here so the tests can show that the two
        // really are different things, which is the whole point of the note above. The flag's own
        // job happens in the database, where the patch generator uses it to find the rows worth
        // reading; that path never goes through this view model.
        public bool IsEditedFlag => _record.IsEdited;

        // What the tree shows. An NPC without a name is not unusual (templates, test actors), and a
        // blank row in a tree of 6.580 is worse than a technical one.
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(EditorID) ? EditorID
            : Key;

        public string PluginName
        {
            get
            {
                int bar = Key.IndexOf('|');
                return bar > 0 ? Key.Substring(0, bar) : Key;
            }
        }

        public string ClassDisplay => _labels.Class(_record.ClassKey);
        public string RaceDisplay => _labels.Race(_record.RaceKey);

        // --------------------
        // Level
        // --------------------

        // One line instead of five fields, because only one of the two shapes is ever meaningful and
        // showing both leaves the reader to work out which. "x1.00 of player (16-50)" says it.
        public string LevelDisplay =>
            _record.UsesPcLevelMult
                ? $"x{_record.LevelMult:0.00} of player" +
                  (_record.CalcMinLevel > 0 || _record.CalcMaxLevel > 0
                      ? $"  ({_record.CalcMinLevel}-{_record.CalcMaxLevel})"
                      : "")
                : _record.Level.ToString();


        // --------------------
        // Stats
        // --------------------

        // --------------------
        // Editing (N-P2: stats and level)
        // --------------------
        //
        // Every setter follows the same three steps: write the value, tell whoever is listening to
        // persist it, and re-raise the matching Is<X>Changed so the field's border updates. The
        // change marker compares against what the SCAN read, not against the value the box held a
        // moment ago - typing 250 over a scanned 250 is not an edit, and typing the scanned value
        // back is an undo.
        //
        // The bindings commit on focus loss, so one committed edit is one write. That is why there
        // is no debouncer here, unlike the item pipeline - see the note on NpcEditStore.

        // Set by whoever owns this NPC (the tab). Not a service reference: this view model is built
        // by the hundred in tests, and a nullable callback keeps it constructible without a database.
        public Action<NpcNodeVM, string, object>? FieldChanged { get; set; }
        public Action<NpcNodeVM, string, int?>? SkillChanged { get; set; }

        private void Persist(string column, object value)
        {
            _record.IsEdited = true;
            FieldChanged?.Invoke(this, column, value);

            OnPropertyChanged(nameof(IsEdited));
            OnPropertyChanged(nameof(IsEditedFlag));
            OnPropertyChanged(nameof(HasAnyEdit));
        }

        // WHAT THE GAME WILL ACTUALLY USE, for the 67,5 % of NPCs that recompute their stats.
        //
        // Shown BESIDE the stored value, never instead of it, and never written anywhere: the
        // formula is a hypothesis that no reading has confirmed yet (see Services/AutoCalcStats.cs).
        // The stored number stays visible because it is what the record says, and the prediction
        // stands next to it because it is what the player will meet.
        public bool UsesAutoCalc => (_record.Flags & (uint)NpcFlag.AutoCalcStats) != 0;

        private AutoCalcResult? Predicted
        {
            get
            {
                if (!UsesAutoCalc) return null;

                var race = _labels.RaceStats(_record.RaceKey);
                var weights = _labels.ClassStatWeights(_record.ClassKey);
                if (race == null || weights == null) return null;

                return AutoCalcStats.Compute(new AutoCalcInputs(
                    race.Value.Health, race.Value.Magicka, race.Value.Stamina,
                    weights.Value.Health, weights.Value.Magicka, weights.Value.Stamina,
                    Level));
            }
        }

        public bool HasPrediction => Predicted != null;

        public string PredictedStatsText
        {
            get
            {
                var p = Predicted;
                return p == null
                    ? ""
                    : $"The game recalculates this NPC: health {p.Value.Health}, magicka {p.Value.Magicka}, "
                      + $"stamina {p.Value.Stamina} at level {Level}. The values below are stored but unused. "
                      + "Unverified - this is the published formula, not a reading from the game.";
            }
        }

        public int Health
        {
            get => _record.Health;
            set
            {
                if (_record.Health == value) return;
                _record.Health = value;
                Persist(nameof(Health), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHealthChanged));
            }
        }

        public int Magicka
        {
            get => _record.Magicka;
            set
            {
                if (_record.Magicka == value) return;
                _record.Magicka = value;
                Persist(nameof(Magicka), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMagickaChanged));
            }
        }

        public int Stamina
        {
            get => _record.Stamina;
            set
            {
                if (_record.Stamina == value) return;
                _record.Stamina = value;
                Persist(nameof(Stamina), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsStaminaChanged));
            }
        }

        public int Level
        {
            get => _record.Level;
            set
            {
                if (_record.Level == value) return;
                _record.Level = value;
                Persist(nameof(Level), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLevelChanged));
                OnPropertyChanged(nameof(LevelDisplay));
            }
        }

        public int CalcMinLevel
        {
            get => _record.CalcMinLevel;
            set
            {
                if (_record.CalcMinLevel == value) return;
                _record.CalcMinLevel = value;
                Persist(nameof(CalcMinLevel), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCalcMinLevelChanged));
                OnPropertyChanged(nameof(LevelDisplay));
            }
        }

        public int CalcMaxLevel
        {
            get => _record.CalcMaxLevel;
            set
            {
                if (_record.CalcMaxLevel == value) return;
                _record.CalcMaxLevel = value;
                Persist(nameof(CalcMaxLevel), value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCalcMaxLevelChanged));
                OnPropertyChanged(nameof(LevelDisplay));
            }
        }

        // Turning the multiplier off is what makes a fixed level mean anything, and SkyPatcher wants
        // a fallback level with it (setPcLevelMult=false=50). The whole level block therefore
        // redraws when this flips - the fields that matter are not the same before and after.
        public bool UsesPcLevelMult
        {
            get => _record.UsesPcLevelMult;
            set
            {
                if (_record.UsesPcLevelMult == value) return;
                _record.UsesPcLevelMult = value;
                Persist(nameof(UsesPcLevelMult), value ? 1 : 0);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUsesPcLevelMultChanged));
                OnPropertyChanged(nameof(LevelDisplay));
                OnPropertyChanged(nameof(ShowsFixedLevel));
            }
        }

        // Which half of the level block applies. The other one is not hidden - seeing what the
        // record would fall back to is the point of changing this at all.
        public bool ShowsFixedLevel => !_record.UsesPcLevelMult;

        // --------------------
        // Lists (N-P3)
        // --------------------
        //
        // Both lists are held whole and saved whole. Unlike the single fields there is no shadow
        // COLUMN to write - a list needs a snapshot of what the scan found, and the store takes that
        // on the first edit. What the view model owes it is simply "this is the list now".

        public Action<NpcNodeVM, IReadOnlyList<NpcFactionRecord>>? FactionsChanged { get; set; }

        private void PersistFactions()
        {
            _record.IsEdited = true;
            FactionsChanged?.Invoke(this, Factions.Select(f => f.ToRecord()).ToList());

            OnPropertyChanged(nameof(FactionCount));
            OnPropertyChanged(nameof(IsFactionsChanged));
            OnPropertyChanged(nameof(IsEdited));
            OnPropertyChanged(nameof(HasAnyEdit));
        }

        public void AddFaction(string factionKey, int rank = 0)
        {
            if (string.IsNullOrWhiteSpace(factionKey)) return;

            // A faction the NPC is already in is a rank change, not a second membership - the table
            // has (NpcKey, FactionKey) as its key and the record never lists one twice.
            var existing = Factions.FirstOrDefault(f =>
                string.Equals(f.FactionKey, factionKey, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.Rank == rank) return;
                existing.Rank = rank;
            }
            else
            {
                var row = new NpcFactionRowVM(new NpcFactionRecord { FactionKey = factionKey, Rank = rank }, _labels);
                WireFaction(row);

                // Inserted in name order, the order the list is read in - appending would put new
                // entries somewhere different every time.
                int at = 0;
                while (at < Factions.Count &&
                       string.Compare(Factions[at].DisplayName, row.DisplayName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    at++;
                }
                Factions.Insert(at, row);
            }

            PersistFactions();
        }

        public void RemoveFaction(NpcFactionRowVM row)
        {
            if (row == null || !Factions.Remove(row)) return;
            PersistFactions();
        }

        private void WireFaction(NpcFactionRowVM row) => row.RankChanged = _ => PersistFactions();

        public bool IsFactionsChanged
        {
            get
            {
                var scanned = (_record.ScannedFactions ?? new List<NpcFactionRecord>())
                    .ToDictionary(f => f.FactionKey, f => f.Rank, StringComparer.OrdinalIgnoreCase);

                if (scanned.Count != Factions.Count) return true;

                return Factions.Any(f =>
                    !scanned.TryGetValue(f.FactionKey, out int rank) || rank != f.Rank);
            }
        }

        // Keywords. The list is a set of keys; the view shows them through the same chip control the
        // item editor uses, so the view model hands out the keys and takes them back.
        public Action<NpcNodeVM, IReadOnlyList<string>>? KeywordsChanged { get; set; }

        public IReadOnlyList<string> Keywords => _record.Keywords;

        public bool HasKeywords => _record.Keywords.Count > 0;

        public void ToggleKeyword(string keywordKey)
        {
            if (string.IsNullOrWhiteSpace(keywordKey)) return;

            int at = _record.Keywords.FindIndex(k =>
                string.Equals(k, keywordKey, StringComparison.OrdinalIgnoreCase));

            if (at >= 0) _record.Keywords.RemoveAt(at);
            else _record.Keywords.Add(keywordKey);

            _record.IsEdited = true;
            KeywordsChanged?.Invoke(this, _record.Keywords.ToList());

            OnPropertyChanged(nameof(Keywords));
            OnPropertyChanged(nameof(HasKeywords));
            OnPropertyChanged(nameof(KeywordCount));
            OnPropertyChanged(nameof(IsKeywordsChanged));
            OnPropertyChanged(nameof(IsEdited));
            OnPropertyChanged(nameof(HasAnyEdit));
        }

        public int KeywordCount => _record.Keywords.Count;

        public bool IsKeywordsChanged
        {
            get
            {
                var scanned = new HashSet<string>(_record.ScannedKeywords ?? new List<string>(),
                                                  StringComparer.OrdinalIgnoreCase);
                return scanned.Count != _record.Keywords.Count
                    || _record.Keywords.Any(k => !scanned.Contains(k));
            }
        }

        // --------------------
        // Links, body, AI and flags (N-P4)
        // --------------------
        //
        // Same pattern as N-P2: write, persist, re-raise the marker. What is NOT here stays out for
        // the reason in NPC-Plan.md §3.2 - CombatStyleKey and CrimeFactionKey have no SkyPatcher
        // operation, so they keep their *Display property and have no setter at all.

        private bool SetLinkField(string current, string value, string column)
        {
            value ??= "";
            if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase)) return false;

            Persist(column, value);
            return true;
        }

        public string ClassKey
        {
            get => _record.ClassKey;
            set
            {
                if (!SetLinkField(_record.ClassKey, value, nameof(ClassKey))) return;
                _record.ClassKey = value ?? "";
                Raise(nameof(ClassKey), nameof(ClassDisplay), nameof(IsClassChanged));
            }
        }

        public string RaceKey
        {
            get => _record.RaceKey;
            set
            {
                if (!SetLinkField(_record.RaceKey, value, nameof(RaceKey))) return;
                _record.RaceKey = value ?? "";
                Raise(nameof(RaceKey), nameof(RaceDisplay), nameof(IsRaceChanged));
            }
        }

        public string VoiceKey
        {
            get => _record.VoiceKey;
            set
            {
                if (!SetLinkField(_record.VoiceKey, value, nameof(VoiceKey))) return;
                _record.VoiceKey = value ?? "";
                Raise(nameof(VoiceKey), nameof(VoiceDisplay), nameof(IsVoiceChanged));
            }
        }

        public string DefaultOutfitKey
        {
            get => _record.DefaultOutfitKey;
            set
            {
                if (!SetLinkField(_record.DefaultOutfitKey, value, nameof(DefaultOutfitKey))) return;
                _record.DefaultOutfitKey = value ?? "";
                Raise(nameof(DefaultOutfitKey), nameof(DefaultOutfitDisplay), nameof(IsDefaultOutfitChanged));
            }
        }

        public string SleepOutfitKey
        {
            get => _record.SleepOutfitKey;
            set
            {
                if (!SetLinkField(_record.SleepOutfitKey, value, nameof(SleepOutfitKey))) return;
                _record.SleepOutfitKey = value ?? "";
                Raise(nameof(SleepOutfitKey), nameof(SleepOutfitDisplay), nameof(IsSleepOutfitChanged));
            }
        }

        public string DeathItemKey
        {
            get => _record.DeathItemKey;
            set
            {
                if (!SetLinkField(_record.DeathItemKey, value, nameof(DeathItemKey))) return;
                _record.DeathItemKey = value ?? "";
                Raise(nameof(DeathItemKey), nameof(DeathItemDisplay), nameof(IsDeathItemChanged));
            }
        }

        public string SkinKey
        {
            get => _record.SkinKey;
            set
            {
                if (!SetLinkField(_record.SkinKey, value, nameof(SkinKey))) return;
                _record.SkinKey = value ?? "";
                Raise(nameof(SkinKey), nameof(SkinDisplay), nameof(IsSkinChanged));
            }
        }

        public float Weight
        {
            get => _record.Weight;
            set
            {
                if (Math.Abs(_record.Weight - value) < 0.0001f) return;
                _record.Weight = value;
                Persist(nameof(Weight), (double)value);
                Raise(nameof(Weight), nameof(IsWeightChanged));
            }
        }

        public float Height
        {
            get => _record.Height;
            set
            {
                if (Math.Abs(_record.Height - value) < 0.0001f) return;
                _record.Height = value;
                Persist(nameof(Height), (double)value);
                Raise(nameof(Height), nameof(IsHeightChanged));
            }
        }

        // The five AI values, each one token from its own list. Stored as the token, so nothing has
        // to translate on the way into the patch string.
        public string Aggression
        {
            get => _record.Aggression;
            set
            {
                if (!SetLinkField(_record.Aggression, value, nameof(Aggression))) return;
                _record.Aggression = value ?? "";
                Raise(nameof(Aggression), nameof(IsAggressionChanged));
            }
        }

        public string Confidence
        {
            get => _record.Confidence;
            set
            {
                if (!SetLinkField(_record.Confidence, value, nameof(Confidence))) return;
                _record.Confidence = value ?? "";
                Raise(nameof(Confidence), nameof(IsConfidenceChanged));
            }
        }

        public string Assistance
        {
            get => _record.Assistance;
            set
            {
                if (!SetLinkField(_record.Assistance, value, nameof(Assistance))) return;
                _record.Assistance = value ?? "";
                Raise(nameof(Assistance), nameof(IsAssistanceChanged));
            }
        }

        public string Morality
        {
            get => _record.Morality;
            set
            {
                if (!SetLinkField(_record.Morality, value, nameof(Morality))) return;
                _record.Morality = value ?? "";
                Raise(nameof(Morality), nameof(IsMoralityChanged));
            }
        }

        public string Mood
        {
            get => _record.Mood;
            set
            {
                if (!SetLinkField(_record.Mood, value, nameof(Mood))) return;
                _record.Mood = value ?? "";
                Raise(nameof(Mood), nameof(IsMoodChanged));
            }
        }

        // --------------------
        // Flags
        // --------------------
        //
        // Shown as one checkbox per flag rather than a number, because that is how they are read and
        // how setFlags/removeFlags address them. The rows are built once and know their bit.
        public ObservableCollection<NpcFlagRowVM> FlagRows { get; } = new();
        public ObservableCollection<NpcFlagRowVM> TemplateFlagRows { get; } = new();

        private void BuildFlagRows()
        {
            // pclevelmult is deliberately not offered here: it is the Level block's checkbox, and
            // having the same switch in two places lets the user set it twice with different
            // answers - the record would then get both setPcLevelMult and setFlags for one thing.
            foreach (var name in NpcFlagNames.All.Where(n => n != "pclevelmult"))
            {
                var row = new NpcFlagRowVM(name, NpcFlagNames.Parse(new[] { name }));
                row.Toggled = OnFlagToggled;
                FlagRows.Add(row);
            }

            foreach (var name in NpcTemplateFlagNames.All)
            {
                var row = new NpcFlagRowVM(name, NpcTemplateFlagNames.Parse(new[] { name }));
                row.Toggled = OnTemplateFlagToggled;
                TemplateFlagRows.Add(row);
            }

            SyncFlagRows();
        }

        private void SyncFlagRows()
        {
            foreach (var row in FlagRows) row.SetWithoutNotifying((_record.Flags & row.Bit) != 0);
            foreach (var row in TemplateFlagRows) row.SetWithoutNotifying((_record.TemplateFlags & row.Bit) != 0);
        }

        private void OnFlagToggled(NpcFlagRowVM row)
        {
            _record.Flags = row.IsSet ? _record.Flags | row.Bit : _record.Flags & ~row.Bit;
            Persist(nameof(Flags), (long)_record.Flags);
            Raise(nameof(FlagsDisplay), nameof(IsFlagsChanged));
        }

        private void OnTemplateFlagToggled(NpcFlagRowVM row)
        {
            _record.TemplateFlags = row.IsSet
                ? _record.TemplateFlags | row.Bit
                : _record.TemplateFlags & ~row.Bit;

            Persist(nameof(TemplateFlags), (long)_record.TemplateFlags);

            // Clearing the Stats flag is what makes a stat edit actually take effect, so the whole
            // inheritance block redraws - including the warning above the numbers.
            Raise(nameof(TemplateFlagsDisplay), nameof(IsTemplateFlagsChanged),
                  nameof(InheritsStats), nameof(InheritsTraits), nameof(InheritsFactions),
                  nameof(InheritsInventory), nameof(InheritsSpells), nameof(InheritsKeywords),
                  nameof(ShowsStatInheritanceWarning));
        }

        public uint Flags => _record.Flags;
        public uint TemplateFlags => _record.TemplateFlags;

        private void Raise(params string[] names)
        {
            foreach (var name in names) OnPropertyChanged(name);
        }

        // --------------------
        // Spells, perks and inventory (N-P3, second half)
        // --------------------
        //
        // Three lists, one shape: hold them whole, save them whole, mark them by comparing with the
        // snapshot. The rows are read-only records except for an inventory count, which is the one
        // thing on any of these three rows that can be edited in place.

        public Action<NpcNodeVM, IReadOnlyList<NpcSpellRecord>>? SpellsChanged { get; set; }
        public Action<NpcNodeVM, IReadOnlyList<NpcPerkRecord>>? PerksChanged { get; set; }
        public Action<NpcNodeVM, IReadOnlyList<NpcItemRecord>>? ItemsChanged { get; set; }

        public ObservableCollection<NpcSpellRowVM> SpellRows { get; } = new();
        public ObservableCollection<NpcPerkRowVM> PerkRows { get; } = new();
        public ObservableCollection<NpcItemRowVM> ItemRows { get; } = new();

        private void BuildListRows()
        {
            SpellRows.Clear();
            foreach (var spell in _record.Spells.OrderBy(SpellName, StringComparer.OrdinalIgnoreCase))
                SpellRows.Add(new NpcSpellRowVM(spell, SpellName(spell)));

            PerkRows.Clear();
            foreach (var perk in _record.Perks.OrderBy(p => _labels.Perk(p.PerkKey), StringComparer.OrdinalIgnoreCase))
                PerkRows.Add(new NpcPerkRowVM(perk, _labels.Perk(perk.PerkKey), IsScannedPerk(perk.PerkKey)));

            ItemRows.Clear();
            foreach (var item in _record.Items.OrderBy(i => _labels.Item(i.ItemKey), StringComparer.OrdinalIgnoreCase))
            {
                var row = new NpcItemRowVM(item, _labels.Item(item.ItemKey));
                row.CountChanged = _ => PersistItems();
                ItemRows.Add(row);
            }
        }

        // Which catalogue holds the name depends on what kind of entry it is - that is the whole
        // reason the scan records a kind at all.
        private string SpellName(NpcSpellRecord spell) => spell.Kind switch
        {
            "shout" => _labels.Shout(spell.SpellKey),
            "levspell" => _labels.LeveledSpell(spell.SpellKey),
            _ => _labels.Spell(spell.SpellKey),
        };

        // A perk that came from the plugins cannot be taken away: there is no perksToRemove. The row
        // knows, so the screen can leave the remove button off rather than offering an edit no patch
        // could carry (NPC-Plan.md section 3.2).
        private bool IsScannedPerk(string perkKey) =>
            (_record.ScannedPerks ?? new List<NpcPerkRecord>())
                .Any(p => string.Equals(p.PerkKey, perkKey, StringComparison.OrdinalIgnoreCase));

        public int SpellCount => SpellRows.Count;
        public int PerkCount => PerkRows.Count;
        public int ItemCount => ItemRows.Count;

        public void AddSpell(string key, string kind)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (_record.Spells.Any(s => string.Equals(s.SpellKey, key, StringComparison.OrdinalIgnoreCase))) return;

            _record.Spells.Add(new NpcSpellRecord { SpellKey = key, Kind = kind ?? "" });
            PersistSpells();
        }

        public void RemoveSpell(NpcSpellRowVM row)
        {
            if (row == null) return;
            _record.Spells.RemoveAll(s => string.Equals(s.SpellKey, row.Key, StringComparison.OrdinalIgnoreCase));
            PersistSpells();
        }

        public void AddPerk(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (_record.Perks.Any(p => string.Equals(p.PerkKey, key, StringComparison.OrdinalIgnoreCase))) return;

            _record.Perks.Add(new NpcPerkRecord { PerkKey = key });
            PersistPerks();
        }

        // Since N-P6 a perk from the plugins can go too - not by rule, there is no perksToRemove, but
        // through an ESP override of the record. The row still knows which kind it is, because the
        // two cost very different things.
        public void RemovePerk(NpcPerkRowVM row)
        {
            if (row == null) return;
            _record.Perks.RemoveAll(p => string.Equals(p.PerkKey, row.Key, StringComparison.OrdinalIgnoreCase));
            PersistPerks();
            Raise(nameof(NeedsEspOverride));
        }

        public void AddItem(string key, int count = 1)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var existing = _record.Items.FirstOrDefault(i =>
                string.Equals(i.ItemKey, key, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.Count == count) return;
                existing.Count = count;
            }
            else
            {
                _record.Items.Add(new NpcItemRecord { ItemKey = key, Count = count });
            }

            PersistItems();
        }

        public void RemoveItem(NpcItemRowVM row)
        {
            if (row == null) return;
            _record.Items.RemoveAll(i => string.Equals(i.ItemKey, row.Key, StringComparison.OrdinalIgnoreCase));
            PersistItems();
        }

        private void PersistSpells()
        {
            _record.IsEdited = true;
            SpellsChanged?.Invoke(this, _record.Spells.ToList());
            AfterListChange(nameof(SpellCount), nameof(IsSpellsChanged));
        }

        private void PersistPerks()
        {
            _record.IsEdited = true;
            PerksChanged?.Invoke(this, _record.Perks.ToList());
            AfterListChange(nameof(PerkCount), nameof(IsPerksChanged));
        }

        private void PersistItems()
        {
            _record.IsEdited = true;
            ItemsChanged?.Invoke(this, _record.Items.ToList());
            AfterListChange(nameof(ItemCount), nameof(IsItemsChanged));
        }

        private void AfterListChange(params string[] names)
        {
            BuildListRows();
            Raise(names);
            Raise(nameof(IsEdited), nameof(HasAnyEdit));
        }

        public bool IsSpellsChanged => !SameSet(
            _record.Spells.Select(s => s.SpellKey),
            (_record.ScannedSpells ?? new List<NpcSpellRecord>()).Select(s => s.SpellKey));

        public bool IsPerksChanged => !SameSet(
            _record.Perks.Select(p => p.PerkKey),
            (_record.ScannedPerks ?? new List<NpcPerkRecord>()).Select(p => p.PerkKey));

        // Counts matter here, unlike the other two: the same item at a different amount is an edit.
        public bool IsItemsChanged
        {
            get
            {
                var scanned = (_record.ScannedItems ?? new List<NpcItemRecord>())
                    .ToDictionary(i => i.ItemKey, i => i.Count, StringComparer.OrdinalIgnoreCase);

                if (scanned.Count != _record.Items.Count) return true;

                return _record.Items.Any(i =>
                    !scanned.TryGetValue(i.ItemKey, out int count) || count != i.Count);
            }
        }

        private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b)
        {
            var left = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            var right = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
            return left.SetEquals(right);
        }

        // --------------------
        // The two an ESP has to carry (N-P6)
        // --------------------
        //
        // No SkyPatcher operation reaches either, so changing one makes the generator write an
        // override of this NPC's record. That is a heavier thing than a rule - our plugin then wins
        // over anything loaded before it for this NPC - so the screen says so and the report names
        // every one.
        public string CombatStyleKey
        {
            get => _record.CombatStyleKey;
            set
            {
                if (!SetLinkField(_record.CombatStyleKey, value, nameof(CombatStyleKey))) return;
                _record.CombatStyleKey = value ?? "";
                Raise(nameof(CombatStyleKey), nameof(CombatStyleDisplay), nameof(IsCombatStyleChanged),
                      nameof(NeedsEspOverride));
            }
        }

        public string CrimeFactionKey
        {
            get => _record.CrimeFactionKey;
            set
            {
                if (!SetLinkField(_record.CrimeFactionKey, value, nameof(CrimeFactionKey))) return;
                _record.CrimeFactionKey = value ?? "";
                Raise(nameof(CrimeFactionKey), nameof(CrimeFactionDisplay), nameof(IsCrimeFactionChanged),
                      nameof(NeedsEspOverride));
            }
        }

        public bool IsCombatStyleChanged => !Same(_record.CombatStyleKey, _record.Scanned.CombatStyleKey);
        public bool IsCrimeFactionChanged => !Same(_record.CrimeFactionKey, _record.Scanned.CrimeFactionKey);

        // Whether this NPC's record will be overridden in the ESP, and why. Shown next to the fields
        // that cause it rather than only in the report: by the time the report exists the patch is
        // already written.
        public bool NeedsEspOverride =>
            IsCombatStyleChanged || IsCrimeFactionChanged || HasRemovedPerks;

        private bool HasRemovedPerks
        {
            get
            {
                var current = new HashSet<string>(
                    _record.Perks.Select(p => p.PerkKey), StringComparer.OrdinalIgnoreCase);

                return (_record.ScannedPerks ?? new List<NpcPerkRecord>())
                    .Any(p => !current.Contains(p.PerkKey));
            }
        }

        public string EspOverrideNote =>
            "These changes cannot travel as a SkyPatcher rule, so this NPC's record is copied into " +
            "the generated ESP. The copy keeps its face, inventory and everything else untouched - " +
            "but our plugin then wins over any mod loaded before it for this NPC.";

        public bool IsClassChanged => !Same(_record.ClassKey, _record.Scanned.ClassKey);
        public bool IsRaceChanged => !Same(_record.RaceKey, _record.Scanned.RaceKey);
        public bool IsVoiceChanged => !Same(_record.VoiceKey, _record.Scanned.VoiceKey);
        public bool IsDefaultOutfitChanged => !Same(_record.DefaultOutfitKey, _record.Scanned.DefaultOutfitKey);
        public bool IsSleepOutfitChanged => !Same(_record.SleepOutfitKey, _record.Scanned.SleepOutfitKey);
        public bool IsDeathItemChanged => !Same(_record.DeathItemKey, _record.Scanned.DeathItemKey);
        public bool IsSkinChanged => !Same(_record.SkinKey, _record.Scanned.SkinKey);
        public bool IsWeightChanged => Math.Abs(_record.Weight - _record.Scanned.Weight) > 0.0001f;
        public bool IsHeightChanged => Math.Abs(_record.Height - _record.Scanned.Height) > 0.0001f;
        public bool IsAggressionChanged => !Same(_record.Aggression, _record.Scanned.Aggression);
        public bool IsConfidenceChanged => !Same(_record.Confidence, _record.Scanned.Confidence);
        public bool IsAssistanceChanged => !Same(_record.Assistance, _record.Scanned.Assistance);
        public bool IsMoralityChanged => !Same(_record.Morality, _record.Scanned.Morality);
        public bool IsMoodChanged => !Same(_record.Mood, _record.Scanned.Mood);
        public bool IsFlagsChanged => _record.Flags != _record.Scanned.Flags;
        public bool IsTemplateFlagsChanged => _record.TemplateFlags != _record.Scanned.TemplateFlags;

        private static bool Same(string a, string b) =>
            string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private bool HasLinkOrFlagEdit =>
            IsClassChanged || IsRaceChanged || IsVoiceChanged
            || IsDefaultOutfitChanged || IsSleepOutfitChanged || IsDeathItemChanged || IsSkinChanged
            || IsWeightChanged || IsHeightChanged
            || IsAggressionChanged || IsConfidenceChanged || IsAssistanceChanged
            || IsMoralityChanged || IsMoodChanged
            || IsFlagsChanged || IsTemplateFlagsChanged
            || IsCombatStyleChanged || IsCrimeFactionChanged;

        public bool IsHealthChanged => _record.Health != _record.Scanned.Health;
        public bool IsMagickaChanged => _record.Magicka != _record.Scanned.Magicka;
        public bool IsStaminaChanged => _record.Stamina != _record.Scanned.Stamina;
        public bool IsLevelChanged => _record.Level != _record.Scanned.Level;
        public bool IsCalcMinLevelChanged => _record.CalcMinLevel != _record.Scanned.CalcMinLevel;
        public bool IsCalcMaxLevelChanged => _record.CalcMaxLevel != _record.Scanned.CalcMaxLevel;
        public bool IsUsesPcLevelMultChanged => _record.UsesPcLevelMult != _record.Scanned.UsesPcLevelMult;

        // "Does this record differ from its plugin at all" - computed from the values rather than
        // read from the IsEdited flag, so undoing every edit by hand clears the badge without a
        // reload. The flag in the database is kept in step by NpcEditStore.
        public bool HasAnyEdit =>
            IsHealthChanged || IsMagickaChanged || IsStaminaChanged
            || IsLevelChanged || IsCalcMinLevelChanged || IsCalcMaxLevelChanged
            || IsUsesPcLevelMultChanged
            || IsKeywordsChanged || IsFactionsChanged
            || IsSpellsChanged || IsPerksChanged || IsItemsChanged
            || HasLinkOrFlagEdit
            || Skills.Any(s => s.IsChanged);

        // Back to what the plugins say. Clears the values in place rather than reloading, so the
        // open detail pane updates - and raises everything, because a reset can touch any field.
        public void ResetToScanned()
        {
            _record.Health = _record.Scanned.Health;
            _record.Magicka = _record.Scanned.Magicka;
            _record.Stamina = _record.Scanned.Stamina;
            _record.Level = _record.Scanned.Level;
            _record.CalcMinLevel = _record.Scanned.CalcMinLevel;
            _record.CalcMaxLevel = _record.Scanned.CalcMaxLevel;
            _record.UsesPcLevelMult = _record.Scanned.UsesPcLevelMult;

            _record.ClassKey = _record.Scanned.ClassKey;
            _record.RaceKey = _record.Scanned.RaceKey;
            _record.VoiceKey = _record.Scanned.VoiceKey;
            _record.DefaultOutfitKey = _record.Scanned.DefaultOutfitKey;
            _record.SleepOutfitKey = _record.Scanned.SleepOutfitKey;
            _record.DeathItemKey = _record.Scanned.DeathItemKey;
            _record.SkinKey = _record.Scanned.SkinKey;
            _record.Weight = _record.Scanned.Weight;
            _record.Height = _record.Scanned.Height;
            _record.Aggression = _record.Scanned.Aggression;
            _record.Confidence = _record.Scanned.Confidence;
            _record.Assistance = _record.Scanned.Assistance;
            _record.Morality = _record.Scanned.Morality;
            _record.Mood = _record.Scanned.Mood;
            _record.Flags = _record.Scanned.Flags;
            _record.TemplateFlags = _record.Scanned.TemplateFlags;

            // The checkboxes are set from the record without notifying, or every reset would save
            // once per flag it put back.
            SyncFlagRows();

            // The three N-P3 lists come back from their snapshot the same way the factions do.
            _record.Spells = (_record.ScannedSpells ?? new List<NpcSpellRecord>())
                .Select(s => new NpcSpellRecord { SpellKey = s.SpellKey, Kind = s.Kind }).ToList();
            _record.Perks = (_record.ScannedPerks ?? new List<NpcPerkRecord>())
                .Select(p => new NpcPerkRecord { PerkKey = p.PerkKey, Rank = p.Rank }).ToList();
            _record.Items = (_record.ScannedItems ?? new List<NpcItemRecord>())
                .Select(i => new NpcItemRecord { ItemKey = i.ItemKey, Count = i.Count }).ToList();

            BuildListRows();

            _record.IsEdited = false;

            foreach (var skill in Skills) skill.ResetToScanned();

            // The lists are rebuilt from the snapshot rather than unflagged: unlike a shadow column,
            // the live entries were replaced, so the snapshot is the only copy of what the plugins
            // said.
            _record.Keywords = new List<string>(_record.ScannedKeywords ?? new List<string>());

            Factions.Clear();
            foreach (var scanned in (_record.ScannedFactions ?? new List<NpcFactionRecord>())
                        .OrderBy(f => _labels.Faction(f.FactionKey), StringComparer.OrdinalIgnoreCase))
            {
                var row = new NpcFactionRowVM(
                    new NpcFactionRecord { FactionKey = scanned.FactionKey, Rank = scanned.Rank }, _labels);
                WireFaction(row);
                Factions.Add(row);
            }

            foreach (var name in new[]
            {
                nameof(Health), nameof(Magicka), nameof(Stamina),
                nameof(Level), nameof(CalcMinLevel), nameof(CalcMaxLevel), nameof(UsesPcLevelMult),
                nameof(IsHealthChanged), nameof(IsMagickaChanged), nameof(IsStaminaChanged),
                nameof(IsLevelChanged), nameof(IsCalcMinLevelChanged), nameof(IsCalcMaxLevelChanged),
                nameof(IsUsesPcLevelMultChanged),
                nameof(LevelDisplay), nameof(ShowsFixedLevel), nameof(IsEdited), nameof(HasAnyEdit),
                nameof(Keywords), nameof(HasKeywords), nameof(KeywordCount), nameof(IsKeywordsChanged),
                nameof(FactionCount), nameof(IsFactionsChanged),
                nameof(ClassKey), nameof(ClassDisplay), nameof(IsClassChanged),
                nameof(RaceKey), nameof(RaceDisplay), nameof(IsRaceChanged),
                nameof(VoiceKey), nameof(VoiceDisplay), nameof(IsVoiceChanged),
                nameof(DefaultOutfitKey), nameof(DefaultOutfitDisplay), nameof(IsDefaultOutfitChanged),
                nameof(SleepOutfitKey), nameof(SleepOutfitDisplay), nameof(IsSleepOutfitChanged),
                nameof(DeathItemKey), nameof(DeathItemDisplay), nameof(IsDeathItemChanged),
                nameof(SkinKey), nameof(SkinDisplay), nameof(IsSkinChanged),
                nameof(Weight), nameof(IsWeightChanged), nameof(Height), nameof(IsHeightChanged),
                nameof(Aggression), nameof(IsAggressionChanged),
                nameof(Confidence), nameof(IsConfidenceChanged),
                nameof(Assistance), nameof(IsAssistanceChanged),
                nameof(Morality), nameof(IsMoralityChanged),
                nameof(Mood), nameof(IsMoodChanged),
                nameof(Flags), nameof(FlagsDisplay), nameof(IsFlagsChanged),
                nameof(TemplateFlags), nameof(TemplateFlagsDisplay), nameof(IsTemplateFlagsChanged),
                nameof(InheritsStats), nameof(ShowsStatInheritanceWarning),
                nameof(SpellCount), nameof(IsSpellsChanged),
                nameof(PerkCount), nameof(IsPerksChanged),
                nameof(ItemCount), nameof(IsItemsChanged),
            })
            {
                OnPropertyChanged(name);
            }
        }


        public int HealthOffset => _record.HealthOffset;
        public int MagickaOffset => _record.MagickaOffset;
        public int StaminaOffset => _record.StaminaOffset;

        // The offsets are a second, separate set of numbers on the same record, and it is not yet
        // established which of the two changeStats writes. Shown only when one is non-zero, so the
        // open question is visible exactly where it matters and nowhere else.
        public bool HasOffsets =>
            _record.HealthOffset != 0 || _record.MagickaOffset != 0 || _record.StaminaOffset != 0;

        public string OffsetsDisplay =>
            $"Health {_record.HealthOffset:+#;-#;0}, Magicka {_record.MagickaOffset:+#;-#;0}, Stamina {_record.StaminaOffset:+#;-#;0}";

        // --------------------
        // The template state - the single most important thing on this screen
        // --------------------

        public bool HasTemplate => !string.IsNullOrWhiteSpace(_record.TemplateKey);

        public string TemplateKey => _record.TemplateKey;

        public string TemplateDisplay => _labels.Npc(_record.TemplateKey);

        // How many NPCs take their values from THIS one. Filled by the menu VM after loading,
        // because a record does not know who points at it - and it is the number that decides
        // whether editing this record is worth doing: EncBandit00Template feeds 502 NPCs.
        private int _heirCount;
        public int HeirCount
        {
            get => _heirCount;
            set
            {
                if (SetProperty(ref _heirCount, value))
                {
                    OnPropertyChanged(nameof(IsTemplate));
                    OnPropertyChanged(nameof(HeirDisplay));
                }
            }
        }

        public bool IsTemplate => HeirCount > 0;

        public string HeirDisplay =>
            HeirCount == 0 ? "" : $"{HeirCount} NPC(s) inherit from this one";

        public bool InheritsStats => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.Stats) != 0;
        public bool InheritsTraits => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.Traits) != 0;
        public bool InheritsFactions => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.Factions) != 0;
        public bool InheritsInventory => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.Inventory) != 0;
        public bool InheritsSpells => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.SpellList) != 0;
        public bool InheritsKeywords => HasTemplate && (_record.TemplateFlags & (uint)NpcTemplateFlag.Keywords) != 0;

        // THE warning (NPC-Plan.md §4). 61,4 % of all NPCs are in this state, and for them the stat
        // fields on this screen describe something the game never reads. Saying so next to the
        // numbers is the whole point - finding out in the dry run is too late.
        public bool ShowsStatInheritanceWarning => InheritsStats;

        public string StatInheritanceWarning =>
            "These values come from the template, not from this record - the game never reads the " +
            "ones below. A patch has to clear the Stats template flag first.";

        public string TemplateFlagsDisplay
        {
            get
            {
                var set = NpcTemplateFlagNames.Describe(_record.TemplateFlags);
                return set.Count == 0 ? "-" : string.Join(", ", set);
            }
        }

        // --------------------
        // Links
        // --------------------

        public string VoiceDisplay => _labels.VoiceType(_record.VoiceKey);
        public string DefaultOutfitDisplay => _labels.Outfit(_record.DefaultOutfitKey);
        public string SleepOutfitDisplay => _labels.Outfit(_record.SleepOutfitKey);
        public string DeathItemDisplay => _labels.LeveledItem(_record.DeathItemKey);
        public string SkinDisplay => _labels.Armor(_record.SkinKey);

        // No SkyPatcher operation for these two: shown, never editable.
        public string CombatStyleDisplay => _labels.CombatStyle(_record.CombatStyleKey);
        public string CrimeFactionDisplay => _labels.Faction(_record.CrimeFactionKey);


        public string FlagsDisplay
        {
            get
            {
                var set = NpcFlagNames.Describe(_record.Flags);
                return set.Count == 0 ? "-" : string.Join(", ", set);
            }
        }


        // No operation for this one.
        public int EnergyLevel => _record.EnergyLevel;

        private static string Token(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

        public ObservableCollection<NpcFactionRowVM> Factions { get; }
        public ObservableCollection<NpcSkillRowVM> Skills { get; }

        public int FactionCount => Factions.Count;

    }

    // A faction membership row: which faction, at which rank.
    public class NpcFactionRowVM : ViewModelBase
    {
        private readonly NpcFactionRecord _record;

        public NpcFactionRowVM(NpcFactionRecord record, INpcLabels labels)
        {
            _record = record;
            DisplayName = labels.Faction(record.FactionKey);
        }

        // Raised so the NPC can persist the whole list again - a row does not know its NPC.
        public Action<NpcFactionRowVM>? RankChanged { get; set; }

        public string FactionKey => _record.FactionKey;
        public string DisplayName { get; }

        public int Rank
        {
            get => _record.Rank;
            set
            {
                if (_record.Rank == value) return;
                _record.Rank = value;

                RankChanged?.Invoke(this);
                OnPropertyChanged();
                OnPropertyChanged(nameof(RankDisplay));
            }
        }

        public NpcFactionRecord ToRecord() =>
            new() { FactionKey = _record.FactionKey, Rank = _record.Rank };

        // Rank 0 is the default and true of 94 % of memberships; printing "0" on every row would be
        // noise that hides the few that carry a real one. -1 is shown because it is not a default -
        // it is the marker for a membership that exists without the actor really belonging.
        public string RankDisplay => _record.Rank == 0 ? "" : _record.Rank.ToString();
    }

    // A spell, shout or leveled spell on an NPC. Kind decides which of the three SkyPatcher
    // operations carries it, and is shown so the difference is visible rather than implied.
    public class NpcSpellRowVM : ViewModelBase
    {
        public NpcSpellRowVM(NpcSpellRecord record, string displayName)
        {
            Key = record.SpellKey;
            Kind = record.Kind;
            DisplayName = displayName;
        }

        public string Key { get; }
        public string Kind { get; }
        public string DisplayName { get; }

        // A blank kind means the key matched no spell, shout or leveled-spell catalogue - a dead
        // reference. Said out loud, because no operation can carry it.
        public bool IsUnresolved => string.IsNullOrWhiteSpace(Kind);

        public string KindDisplay => IsUnresolved ? "unknown" : Kind;
    }

    // A perk. IsFromPlugin is the important one: there is no perksToRemove, so a perk the plugins
    // gave this NPC can be shown but never taken away.
    public class NpcPerkRowVM : ViewModelBase
    {
        public NpcPerkRowVM(NpcPerkRecord record, string displayName, bool fromPlugin)
        {
            Key = record.PerkKey;
            Rank = record.Rank;
            DisplayName = displayName;
            IsFromPlugin = fromPlugin;
        }

        public string Key { get; }
        public int Rank { get; }
        public string DisplayName { get; }
        public bool IsFromPlugin { get; }

        // Every perk can be removed since N-P6 - but one from the plugins costs an ESP override of
        // the whole record, because there is no perksToRemove operation. The row says which it is so
        // the screen can too.
        public bool CanRemove => true;
        public bool RemovalNeedsEsp => IsFromPlugin;

        public string RankDisplay => Rank == 0 ? "" : Rank.ToString();
    }

    // One inventory line. The count is editable; everything else is not.
    public class NpcItemRowVM : ViewModelBase
    {
        private readonly NpcItemRecord _record;

        public NpcItemRowVM(NpcItemRecord record, string displayName)
        {
            _record = record;
            DisplayName = displayName;
        }

        public Action<NpcItemRowVM>? CountChanged { get; set; }

        public string Key => _record.ItemKey;
        public string DisplayName { get; }

        public int Count
        {
            get => _record.Count;
            set
            {
                if (_record.Count == value) return;
                _record.Count = value;

                CountChanged?.Invoke(this);
                OnPropertyChanged();
            }
        }
    }

    // One flag checkbox. Carries its own bit so the NPC never has to look a name up again, and
    // SetWithoutNotifying exists because building the rows and resetting both set the state from the
    // record - and a checkbox that saved while being populated would write on every NPC opened.
    public class NpcFlagRowVM : ViewModelBase
    {
        public NpcFlagRowVM(string name, uint bit)
        {
            Name = name;
            Bit = bit;
        }

        public string Name { get; }
        public uint Bit { get; }

        public Action<NpcFlagRowVM>? Toggled { get; set; }

        private bool _isSet;
        public bool IsSet
        {
            get => _isSet;
            set
            {
                if (_isSet == value) return;
                _isSet = value;

                Toggled?.Invoke(this);
                OnPropertyChanged();
            }
        }

        public void SetWithoutNotifying(bool value)
        {
            if (_isSet == value) return;
            _isSet = value;
            OnPropertyChanged(nameof(IsSet));
        }
    }

    public class NpcSkillRowVM : ViewModelBase
    {
        private readonly NpcSkillRecord _record;

        public NpcSkillRowVM(NpcSkillRecord record) => _record = record;

        // Raised so the NPC can persist the edit and refresh its own badge. One row does not know
        // its NPC; the NPC wires this up when it builds the rows.
        public Action<NpcSkillRowVM, int>? ValueChanged { get; set; }

        public string Skill => _record.Skill;
        public string DisplayName => NpcSkillNames.Label(_record.Skill);

        public int Value
        {
            get => _record.Value;
            set
            {
                if (_record.Value == value) return;
                _record.Value = value;

                ValueChanged?.Invoke(this, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChanged));
            }
        }

        // Against the scanned value, not the previous one: typing the original back is an undo.
        public bool IsChanged => _record.Value != _record.ScannedValue;

        public void ResetToScanned()
        {
            _record.Value = _record.ScannedValue;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(IsChanged));
        }

        public int Offset => _record.Offset;

        public bool HasOffset => _record.Offset != 0;
        public string OffsetDisplay => _record.Offset == 0 ? "" : _record.Offset.ToString("+#;-#;0");
    }
}
