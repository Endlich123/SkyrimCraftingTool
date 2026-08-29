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

        public List<EditedItemDto> GetEditedItems(ExportScope scope, string scopeValue = null)
            => _handler.GetEditedItems(scope, scopeValue);

        public ImportPlan PreviewImport(List<EditedItemDto> fileItems)
            => _handler.PreviewImport(fileItems);

        public ImportResult ApplyImport(ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion)
            => _handler.ApplyImport(plan, conflictKeysToUseFileVersion);
    }
}
