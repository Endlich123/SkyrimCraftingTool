using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class SaveRequestService : ISaveRequestService
    {
        private readonly IEnumerable<ISaveHandler> _handlers;

        public SaveRequestService(IEnumerable<ISaveHandler> handlers)
        {
            _handlers = handlers;
        }

        public async Task SaveAsync(SaveRequest request)
        {
            foreach (var handler in _handlers)
            {
                if (handler.CanHandle(request))
                {
                    await handler.HandleAsync(request);
                    return;
                }
            }

            Debug.WriteLine($"[SaveRequestService] No handler for field {request.FieldName}");
        }
    }
}
