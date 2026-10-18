using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // Always scoped to a unit list - a tab passes its module's units (TabExportModule.Units), so no
    // caller can export or import "everything" by accident. The whole-database variants exist only
    // on ItemDBHandler itself (ExportUnits.All), for the round-trip suites.
    public interface IImportExportService
    {
        List<EditedItemDto> GetEditedItems(IReadOnlyList<ExportUnit> units, ExportScope scope, string scopeValue = null);
        ImportPlan PreviewImport(IReadOnlyList<ExportUnit> units, List<EditedItemDto> fileItems);
        ImportResult ApplyImport(IReadOnlyList<ExportUnit> units, ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion);
    }
}
