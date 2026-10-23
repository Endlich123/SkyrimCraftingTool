using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    public class ContainerRecord
    {
        public string ContainerKey { get; set; }
        public string Name { get; set; }

        public List<ContainerLVLIRecord> LVLIEntries { get; set; } = new();
    }
}
