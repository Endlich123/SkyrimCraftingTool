using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IFormIdService
    {
        void PutIntoDataBank(List<PluginInfo> plugins);

        FormIDRecord? GetByKey(string key);
        List<FormIDRecord> SearchByType(string type);
        List<FormIDRecord> Search(string? name = null, string? prefix = null, string? plugin = null, string? type = null, string? key = null);
    }
}
