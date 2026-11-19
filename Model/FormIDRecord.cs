namespace SkyrimCraftingTool.Model
{
    public class FormIDRecord : IPickerGrouped
    {
        public string FormID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Plugin { get; set; } = "";
        public string Type { get; set; } = ""; // Keyword, Material, COBJ, Enchantment
        public string Key { get; set; } = "";

        // Quests only: the stage indices defined by the QUST record, ascending. Null for every
        // other record type. Feeds the stage picker of GetStageDone and GetStage.
        public IReadOnlyList<int>? Stages { get; set; }

        // Quests only: the journal text (log entry) per stage index, for stages that have one. Null on
        // a formid.db scanned before 2026-11-08. See QuestStageOption.
        public IReadOnlyDictionary<int, string>? StageTexts { get; set; }

        // NAME IS THE EDITORID - it always was, and every picker shows and sorts by it. These three
        // came with the formid.db schema of 2026-11-07, all optional (empty when the record has none
        // or the formid.db predates them):
        //   FullName    - the in-game name ("Armorer"); perks, materials, quests
        //   Description - the in-game text; perks only
        //   Skill       - the skill tree(s) the perk sits in ("Smithing"); perks only
        public string FullName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Skill { get; set; } = "";

        // Materials only: the WorldItem kind - "Misc", "SoulGem", "Ammo", "Food", "Ingredient". The
        // material catalogue is built from item.db's WorldItem rows since 2026-11-07; before that the
        // kind was baked into Name as " (INGR)" and could not be shown, sorted or filtered apart.
        // Enchantments (item tab picker, 2026-11-08) use it for Armor / Weapon / Staff / Other, and
        // Description for their effects' text - see ViewModel.EnchantmentChoices.
        public string Kind { get; set; } = "";

        public bool HasSkill => !string.IsNullOrEmpty(Skill);

        public string KindLabel => WorldItemRecord.KindLabel(Kind);

        // What groups the record in a picker: the skill tree of a perk, the kind of a material. Empty
        // for everything else. Shown grey after the EditorID, and what the filter chips will offer.
        public string Group => HasSkill ? Skill : KindLabel;

        // For the pickers: grey after the EditorID, "· Smithing" / "· Misc".
        public string Suffix => Group.Length > 0 ? $"  · {Group}" : "";

        // The filter chips: each skill tree on its own ("One-handed, Two-handed" is in both chips),
        // otherwise the kind. A perk in no tree has no group and lands in the "(none)" chip.
        public IEnumerable<string> PickerGroups
            => HasSkill
                ? Skill.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : KindLabel.Length > 0 ? new[] { KindLabel } : Array.Empty<string>();

        // The tooltip of a picker row: game name and group, the key, then the description. Lines that
        // are empty are left out, so a keyword shows its key and nothing else - as before.
        public string ToolTipText
        {
            get
            {
                var head = string.Join("  ·  ", new[] { FullName, Group }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var lines = new List<string>();
                if (head.Length > 0) lines.Add(head);
                lines.Add(Key);
                if (!string.IsNullOrWhiteSpace(Description)) lines.Add("\n" + Description.Trim());
                return string.Join("\n", lines);
            }
        }

        // Search as you type: every word must turn up in the EditorID, the game name or the group
        // (skill / kind). Compared without spaces, hyphens and underscores, so "OneHanded" finds
        // "One-handed", "armorer" finds "MAG_Armorer" and "soulgem" finds the soul gems.
        public bool Matches(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            _searchHaystack ??= Squash($"{Name}|{FullName}|{Group}");

            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var needle = Squash(word);
                if (needle.Length > 0 && !_searchHaystack.Contains(needle, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private string? _searchHaystack;

        private static string Squash(string? s)
            => new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '|').Select(char.ToLowerInvariant).ToArray());

        // Fallback if GUI loads before the record is fully populated
        public override string ToString()
        {
            return Name ?? string.Empty;
        }
    }
}
