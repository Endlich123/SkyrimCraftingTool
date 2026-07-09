using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface ICacheManager
    {
        CacheSnapshot BuildCachesFromDB(List<PluginInfo> activePlugins);
    }
}
