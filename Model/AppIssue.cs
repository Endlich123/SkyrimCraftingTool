namespace SkyrimCraftingTool.Model
{
    public enum AppIssueSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <param name="Severity">How loud this should be.</param>
    /// <param name="Message">One-line summary shown in the issue list.</param>
    /// <param name="Context">Optional second line with detail / what to do about it.</param>
    /// <param name="Category">Optional tag so a producer can replace its own batch via IssueService.Clear(category).</param>
    public sealed record AppIssue(
        AppIssueSeverity Severity,
        string Message,
        string? Context = null,
        string? Category = null);
}
