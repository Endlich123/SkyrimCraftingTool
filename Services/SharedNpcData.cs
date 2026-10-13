using System.Collections.Generic;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // The NPC records and name catalogues, read ONCE for the NPC tab and the NPC groups tab
    // (docs/Performance-Plan.md, P-P2a).
    //
    // Both tabs read the same ~26.000 records on their first visit: 1.3 s for the NPC tab, then
    // another 1.4 s for the groups tab - the same rows twice. Whichever opens first reads them, the
    // other gets them from here.
    //
    // SHARED RECORDS, NOT COPIES - and that is what keeps the groups right: the NPC tab writes its
    // edits into these very records (NpcNodeVM sets the field on the record, and Reset puts the
    // scanned values back into the same record), so the groups see an edit exactly as a fresh read of
    // the database would have shown it. The groups tab only reads them. Each tab still gets its own
    // List; only the records inside are shared.
    //
    // Cleared after every scan or reload (MainWindowVM, on DataLoaded), before the tabs drop their own
    // data - the scan rewrites the NPC tables, and the next visit has to read the new ones. Nothing
    // else writes NPC rows: all edits go through NpcEditStore from the NPC tab.
    public sealed class SharedNpcData
    {
        private readonly IItemService _items;
        private List<NpcRecord>? _npcs;
        private NpcLabels? _labels;

        public SharedNpcData(IItemService items) => _items = items;

        // Whether the records are already here - for the perf.log detail of the second tab.
        public bool HasNpcs => _npcs != null;

        public List<NpcRecord> Npcs() => new(_npcs ??= _items.GetAllNpcs());

        public NpcLabels Labels() => _labels ??= _items.GetNpcLabels();

        public void Clear()
        {
            _npcs = null;
            _labels = null;
        }
    }
}
