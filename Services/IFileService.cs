using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IFileService
    {
        void RefreshPluginDatabase();
        List<PluginInfo> GetActivePlugins();
    }
}
