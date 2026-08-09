using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public interface ISaveHandler
    {
        bool CanHandle(SaveRequest request);
        Task HandleAsync(SaveRequest request);
    }
}
