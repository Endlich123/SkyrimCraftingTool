using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IFileService
    {
        void RefreshPluginDatabase();
        List<PluginInfo> GetActivePlugins();

        // Plugin file names the last RefreshPluginDatabase found more than once, and which copy it took.
        IReadOnlyList<PluginPathConflict> LastPathConflicts { get; }
    }
}
