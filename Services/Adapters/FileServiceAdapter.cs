using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.Adapters
{
    public class FileServiceAdapter : SkyrimCraftingTool.Services.IFileService
    {
        private readonly FileDBHandler _handler;

        public FileServiceAdapter(FileDBHandler handler)
        {
            _handler = handler;
        }

        public void RefreshPluginDatabase() => _handler.RefreshPluginDatabase();

        public List<PluginInfo> GetActivePlugins() => _handler.GetActivePlugins();
    }
}
