using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Non-blocking collector for issues the user should know about but that shouldn't interrupt them
    // (dead references, "preset from a newer build", rescan summary, ...). Surfaced by the status
    // strip at the bottom of MainWindow. Access it through IssueHub.Current.
    public sealed class IssueService : INotifyPropertyChanged
    {
        public ObservableCollection<AppIssue> Issues { get; } = new();

        public IssueService()
        {
            Issues.CollectionChanged += (_, _) => RaiseDerived();
        }

        public void Report(AppIssue? issue)
        {
            if (issue is null) return;
            Marshal(() =>
            {
                // Producers re-run constantly (switching to the Items tab re-reads every preset,
                // rescans, ...). An identical issue conveys nothing new - don't stack duplicates.
                // AppIssue is a record, so this is value equality across all four fields.
                if (Issues.Any(existing => existing == issue))
                    return;
                Issues.Add(issue);
            });
        }

        // Clears everything, or only the entries tagged with the given category.
        public void Clear(string? category = null)
        {
            Marshal(() =>
            {
                if (category == null)
                {
                    Issues.Clear();
                    return;
                }

                for (int i = Issues.Count - 1; i >= 0; i--)
                    if (string.Equals(Issues[i].Category, category, StringComparison.Ordinal))
                        Issues.RemoveAt(i);
            });
        }

        public int WarningCount => Issues.Count(i => i.Severity == AppIssueSeverity.Warning);
        public int ErrorCount => Issues.Count(i => i.Severity == AppIssueSeverity.Error);
        public bool HasIssues => Issues.Count > 0;

        public string Summary
        {
            get
            {
                int e = ErrorCount, w = WarningCount, info = Issues.Count - e - w;
                var parts = new List<string>(3);
                if (e > 0) parts.Add($"{e} error{(e == 1 ? "" : "s")}");
                if (w > 0) parts.Add($"{w} warning{(w == 1 ? "" : "s")}");
                if (info > 0) parts.Add($"{info} note{(info == 1 ? "" : "s")}");
                return parts.Count == 0 ? "No issues" : string.Join(", ", parts);
            }
        }

        // Producers may run on a background thread (scan). ObservableCollection is not thread-safe,
        // so hop to the UI thread when there is one. In tests (no Application) run inline.
        private static void Marshal(Action action)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d != null && !d.CheckAccess())
                d.Invoke(action);
            else
                action();
        }

        private void RaiseDerived()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WarningCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIssues)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // Ambient access point, so static stores (PresetFileStore) can report without plumbing the
    // service through. Mirrors GlobalState / AppLogger.
    public static class IssueHub
    {
        public static IssueService Current { get; } = new();
    }
}
