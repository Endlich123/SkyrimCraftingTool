using SkyrimCraftingTool.Model;
using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.ViewModel
{
    public class COBJNodeVM : ViewModelBase
    {
        public string Key { get; set; }
        public ObservableCollection<IngredientEntryVM> Ingredients { get; } = new();
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }


        public COBJNodeVM(COBJRecord rec, FormIDDBHandler formidHandler)
        {
            Key = rec.Key;
            foreach (var rawIng in rec.IngredientKeys)
            {
                string finalKey = rawIng;
                int finalCount = 1;

                if (rawIng.Contains("*"))
                {
                    var parts = rawIng.Split('*');
                    finalKey = parts[0];
                    int.TryParse(parts[1], out finalCount);
                }

                var masterRecord = formidHandler.GetByKey(finalKey);
                string displayName = masterRecord != null ? masterRecord.Name : finalKey;

                Ingredients.Add(new IngredientEntryVM
                {
                    Key = finalKey,
                    MaterialName = displayName,
                    Count = finalCount
                });
            }
        }
    }
}
