using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IImportExportService
    {
        List<EditedItemDto> GetEditedItems(ExportScope scope, string scopeValue = null);
        ImportPlan PreviewImport(List<EditedItemDto> fileItems);
        ImportResult ApplyImport(ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion);
    }
}
