using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    public class LVLiEntryVM : ViewModelBase
    {
        public string Key => Definition.LVLiKey;
        public string LVLiName => Definition.LVLiName;

        public ContainerLVLIRecord Definition { get; }

        private int _level;
        public int Level
        {
            get => _level;
            set
            {
                if (_level != value)
                {
                    _level = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LevelDouble));
                }
            }
        }

        public double LevelDouble
        {
            get => Level;
            set => Level = (int)value;
        }

        public LVLiEntryVM(ContainerLVLIRecord def)
        {
            Definition = def;
        }
    }

}
