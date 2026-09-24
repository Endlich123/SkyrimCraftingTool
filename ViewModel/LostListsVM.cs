using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // Every leveled list that lost entries to an override, in one place.
    //
    // WHY THIS EXISTS: the History block inside a list only helps someone who already opened that
    // list, and lists are reached through an item that happens to sit in one. On a real load order
    // that leaves 155 affected lists that nobody would ever open by chance - the information was
    // built and then unreachable.
    //
    // NOTHING HERE IS A DEFECT LIST. A plugin that drops an entry may be a patch removing it on
    // purpose; the record looks identical either way, and the biggest cases measured are exactly
    // that (one list pruned from 26 vanilla spell tomes to 5, another from 55 Daedric weapons to
    // 8 - both deliberate redesigns). So this reports and the user decides, and the wording has to
    // keep saying so.
    public sealed class LostListsVM : ViewModelBase
    {
        private readonly string? _dbPath;

        public ObservableCollection<LostListRowVM> Rows { get; } = new();

        public LostListsVM(string? dbPath = null)
        {
            _dbPath = dbPath;

            foreach (var summary in PlacementLookup.ReadListsWithLostEntries(dbPath))
                Rows.Add(new LostListRowVM(summary));
        }

        public bool HasRows => Rows.Count > 0;

        public int TotalLost => Rows.Sum(r => r.LostCount);

        public string Summary => Rows.Count == 0
            ? "No leveled list in your load order lost entries to an override. Nothing to review."
            : $"{TotalLost} entries are missing from {Rows.Count} leveled list(s): an earlier plugin " +
              "had them and the plugin that won does not. A mod may have removed them on purpose, or " +
              "may simply have overwritten the list - the record looks the same either way, so none " +
              "of this is patched unless you put it back yourself.";
    }

    public sealed class LostListRowVM
    {
        private readonly LostListSummary _summary;

        public LostListRowVM(LostListSummary summary) => _summary = summary;

        public string ListKey => _summary.ListKey;
        public string Display => _summary.EditorId;
        public int LostCount => _summary.LostCount;

        public string CountText => _summary.LostCount == 1
            ? "1 entry missing"
            : $"{_summary.LostCount} entries missing";

    }
}
