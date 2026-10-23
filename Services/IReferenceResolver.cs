namespace SkyrimCraftingTool.Services
{
    public enum ReferenceKind
    {
        Unknown,
        Keyword,
        Material,
        Workbench,
        Perk,
        Quest,
        Container,
    }

    /// <param name="Found">True if the key resolves to something in the currently scanned load order.</param>
    /// <param name="Name">Display name of the resolved record, if any.</param>
    /// <param name="Kind">What kind of record the key resolves to.</param>
    public readonly record struct ReferenceLookup(bool Found, string? Name, ReferenceKind Kind)
    {
        public static readonly ReferenceLookup Miss = new(false, null, ReferenceKind.Unknown);
    }

    // One place to ask, for any "Plugin|FormID" key: does it still resolve against the current
    // (scanned) load order, and what is it? Backed by the same catalogs the UI already holds
    // (MainContentVM.AllAvailable*). Rebuilt after every scan. Consumed by dead-reference marking,
    // GetStageDone quest validation, orphaned-edit detection and the rescan report.
    public interface IReferenceResolver
    {
        ReferenceLookup Resolve(string? key);

        // Same as Resolve, but Found is only true if the resolved Kind also matches 'expected'.
        // Name/Kind are still filled in on a kind mismatch, for diagnostics.
        ReferenceLookup Resolve(string? key, ReferenceKind expected);

        bool IsActive(string? key);

        // Resolved name if known; otherwise 'fallback' if given; otherwise the key itself.
        string DisplayName(string? key, string fallback = "");
    }
}
