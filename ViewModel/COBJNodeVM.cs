using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.ObjectModel;

using SkyrimCraftingTool.Services;

public class COBJNodeVM : ViewModelBase
{
    public COBJRecord Record { get; }

    public string Key
    {
        get => Record.Key;
        set => Record.Key = value;
    }

    public string Name
    {
        get => Record.Name;
        set => Record.Name = value;
    }

    public string CreatedItemKey
    {
        get => Record.CreatedItemKey;
        set => Record.CreatedItemKey = value;
    }

    public string WorkbenchKeywordKey
    {
        get => Record.WorkbenchKeywordKey;
        set => Record.WorkbenchKeywordKey = value;
    }

    public ObservableCollection<IngredientEntryVM> Ingredients { get; } = new();

    public COBJNodeVM(ItemNodeVM parentItem, COBJRecord rec, IFormIdService formidHandler, bool isTemper)
    {
        Record = rec;

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

            Ingredients.Add(new IngredientEntryVM(parentItem, isTemper)
            {
                Key = finalKey,
                MaterialName = displayName,
                Count = finalCount
            });
        }
    }
}
