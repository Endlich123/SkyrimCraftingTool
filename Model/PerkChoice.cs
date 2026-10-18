namespace SkyrimCraftingTool.Model
{
    // One entry of the half-cost perk picker on the Spells tab. Its own small type because the
    // picker needs the EditorID next to the key - the school-mismatch note reads it - and
    // FormIDRecord has no place for one.
    //
    // Shown as "Name (EditorID)": half-cost perks share names across schools ("Adept"), the
    // EditorID does not. Key "" is the "(none)" entry: a spell without a half-cost perk is valid.
    public sealed record PerkChoice(string Key, string EditorId, string Name)
    {
        public string Display =>
            Key.Length == 0 ? "(none)"
            : Name.Length > 0 && EditorId.Length > 0 ? $"{Name} ({EditorId})"
            : Name.Length > 0 ? Name
            : EditorId.Length > 0 ? EditorId
            : Key;

        public static readonly PerkChoice None = new("", "", "");
    }
}
