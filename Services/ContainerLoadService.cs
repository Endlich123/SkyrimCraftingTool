using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public class ContainerLoadService
    {
        public void LoadInto(ItemNodeVM node)
        {
            node.ContainerSelection.LoadFromString(node.ContainerString);
        }
    }
}
