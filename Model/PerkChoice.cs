namespace SkyrimCraftingTool.Model
{
    // One entry of the half-cost perk picker on the Spells tab. Its own small type because the
    // picker needs the EditorID next to the key - the school-mismatch note reads it - and
    // FormIDRecord has no place for one.
    //
    // Shown as "Name (EditorID)": half-cost perks share names across schools ("Adept"), the
    // EditorID does not. Key "" is the "(none)" entry: a spell without a half-cost perk is valid.
    //
    // Skill and Description come from formid.db's perk columns (2026-11-07): the skill tree after the
    // name, the description in the tooltip. Both empty until a scan has filled them.
    public sealed record PerkChoice(string Key, string EditorId, string Name) : IPickerGrouped
    {
        // The skill trees as filter chips; "(none)" itself has no group and is never filtered away
        // (SpellPerkPickerVM keeps it).
        public System.Collections.Generic.IEnumerable<string> PickerGroups
            => Skill.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        public string Skill { get; init; } = "";
        public string Description { get; init; } = "";

        public string Display =>
            Key.Length == 0 ? "(none)"
            : Name.Length > 0 && EditorId.Length > 0 ? $"{Name} ({EditorId})"
            : Name.Length > 0 ? Name
            : EditorId.Length > 0 ? EditorId
            : Key;

        public string SkillSuffix => Skill.Length > 0 ? $"  · {Skill}" : "";

        public string ToolTipText =>
            Key.Length == 0 ? "No half-cost perk."
            : new FormIDRecord { Key = Key, Name = EditorId, FullName = Name, Skill = Skill, Description = Description }.ToolTipText;

        // The picker filters on what the box shows plus the skill tree, so "destruction" lists the
        // Destruction tree's perks.
        public bool Matches(string text)
            => Display.Contains(text, System.StringComparison.OrdinalIgnoreCase)
               || Skill.Contains(text, System.StringComparison.OrdinalIgnoreCase);

        public static PerkChoice From(FormIDRecord perk)
            => new(perk.Key, perk.Name ?? "", perk.FullName ?? "")
            {
                Skill = perk.Skill ?? "",
                Description = perk.Description ?? "",
            };

        public static readonly PerkChoice None = new("", "", "");
    }
}
