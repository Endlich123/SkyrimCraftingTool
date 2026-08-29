using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.Adapters
{
    public class FormIdServiceAdapter : SkyrimCraftingTool.Services.IFormIdService
    {
        private readonly FormIDDBHandler _handler;

        public FormIdServiceAdapter(FormIDDBHandler handler)
        {
            _handler = handler;
        }

        public void PutIntoDataBank(List<PluginInfo> plugins) => _handler.PutIntoDataBank(plugins);

        public FormIDRecord? GetByKey(string key) => _handler.GetByKey(key);

        public List<FormIDRecord> SearchByType(string type) => _handler.SearchByType(type);

        public List<FormIDRecord> Search(string? name = null, string? prefix = null, string? plugin = null, string? type = null, string? key = null)
            => _handler.Search(name, prefix, plugin, type, key);
    }
}
