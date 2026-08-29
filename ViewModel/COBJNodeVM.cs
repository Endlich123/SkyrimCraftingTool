using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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

    public string PerkKey
    {
        get => Record.PerkKey;
        set => Record.PerkKey = value;
    }

    public ObservableCollection<IngredientEntryVM> Ingredients { get; } = new();

    public ObservableCollection<BaseConditionViewModel> Conditions { get; set; } //new

    public COBJNodeVM(ItemNodeVM parentItem, COBJRecord rec, IFormIdService formidHandler, bool isTemper)
    {
        Record = rec;

        // Merge duplicate ingredient keys (sum counts) so a recipe stored as e.g. "Iron*3" + "Iron*5"
        // loads as one "Iron*8" row instead of two.
        var mergedCounts = new Dictionary<string, int>();
        var keyOrder = new List<string>();
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
            if (finalCount < 1) finalCount = 1;

            if (mergedCounts.TryGetValue(finalKey, out var existing))
                mergedCounts[finalKey] = existing + finalCount;
            else
            {
                mergedCounts[finalKey] = finalCount;
                keyOrder.Add(finalKey);
            }
        }

        foreach (var finalKey in keyOrder)
        {
            var masterRecord = formidHandler.GetByKey(finalKey);
            string displayName = masterRecord != null ? masterRecord.Name : finalKey;

            Ingredients.Add(new IngredientEntryVM(parentItem, isTemper)
            {
                Key = finalKey,
                MaterialName = displayName,
                Count = mergedCounts[finalKey]
            });
        }

        Conditions = new ObservableCollection<BaseConditionViewModel>(
            (rec.Conditions ?? new List<COBJConditionRecord>())
                .Select(c => ConditionMapper.ToViewModel(c, parentItem?.AllAvailablePerks, parentItem?.AllAvailableQuests)));
    }
}
