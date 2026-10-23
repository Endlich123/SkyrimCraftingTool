using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.Adapters
{
    public class ImportExportServiceAdapter : IImportExportService
    {
        private readonly ItemDBHandler _handler;

        public ImportExportServiceAdapter(ItemDBHandler handler)
        {
            _handler = handler;
        }

        public List<EditedItemDto> GetEditedItems(IReadOnlyList<ExportUnit> units, ExportScope scope, string scopeValue = null)
            => _handler.GetEditedItems(units, scope, scopeValue);

        public ImportPlan PreviewImport(IReadOnlyList<ExportUnit> units, List<EditedItemDto> fileItems)
            => _handler.PreviewImport(units, fileItems);

        public ImportResult ApplyImport(IReadOnlyList<ExportUnit> units, ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion)
            => _handler.ApplyImport(units, plan, conflictKeysToUseFileVersion);
    }
}
