namespace SkyrimCraftingTool.Model
{
    // An Armor/Weapons row whose edits are still in the DB but whose owning plugin is no longer in
    // the scanned load order (Active = 0). Surfaced by the rescan report and the cleanup window.
    public sealed record OrphanedEdit(string Table, string Key, string DisplayName, string LastChanged);
}
