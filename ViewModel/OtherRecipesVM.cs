using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One row in the "other recipes" window: a recipe of this item that the editor is NOT showing.
    public sealed class OtherRecipeVM : ViewModelBase
    {
        public string Key { get; }
        public string Name { get; }
        public string WorkbenchName { get; }
        public string Summary { get; }
        public bool IsMain { get; }
        public bool IsUserCreated { get; }
        public bool IsRemoved { get; }

        // Plain flags rather than multi-binding converters: this project has exactly one of those
        // (BooleanToVisibility) and no inverting one, so the view model answers the question.
        public bool CanMakeMain => !IsMain && !IsRemoved;
        public bool CanRemove => !IsRemoved;

        // What the remove button will actually do, spelled out - the two cases behave very
        // differently and the user should not have to know which one they are in.
        public string RemoveHint => IsUserCreated
            ? "Deletes this recipe. It was created here, so nothing in the game brings it back."
            : "Takes this recipe out of the game. The patch writes a deleted-record override for it.";

        public OtherRecipeVM(string key, string name, string workbenchName, string summary,
            bool isMain, bool isUserCreated, bool isRemoved)
        {
            Key = key;
            Name = name;
            WorkbenchName = workbenchName;
            Summary = summary;
            IsMain = isMain;
            IsUserCreated = isUserCreated;
            IsRemoved = isRemoved;
        }
    }

    // The small window behind "N more" in a recipe section header.
    //
    // ONE window for all three recipe kinds. The sections differ in what they edit, but "which of
    // these is the one I see, and can I take the others out" is the same question for all of them -
    // a second shape per section is exactly what this feature exists to avoid.
    //
    // Deliberately not an editor: rows show what the recipe is and offer two decisions. Editing
    // still happens in the normal section, on whichever recipe is the main one.
    public sealed class OtherRecipesVM : ViewModelBase
    {
        private readonly ItemNodeVM _item;
        private readonly RecipeKind _kind;
        private readonly MainContentVM _main;

        public string Title { get; }
        public ObservableCollection<OtherRecipeVM> Rows { get; } = new();

        public bool HasRows => Rows.Count > 0;
        public bool IsEmpty => Rows.Count == 0;

        public string EmptyText => $"{_item.EditorID} has only one {KindLabel(_kind).ToLowerInvariant()} recipe.";

        public ICommand MakeMainCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand RestoreCommand { get; }

        public OtherRecipesVM(ItemNodeVM item, RecipeKind kind)
        {
            _item = item;
            _kind = kind;
            _main = item?.Main;

            Title = $"{KindLabel(kind)} recipes — {item?.EditorID}";

            MakeMainCommand = new RelayCommand<OtherRecipeVM>(row =>
            {
                if (row == null || row.IsMain || row.IsRemoved) return;
                _main?.MakeRecipeMain(_item, _kind, row.Key);
                Reload();
            });

            RemoveCommand = new RelayCommand<OtherRecipeVM>(row =>
            {
                if (row == null || row.IsRemoved) return;

                var answer = System.Windows.MessageBox.Show(
                    row.RemoveHint + "\n\nContinue?",
                    "Remove recipe",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (answer != System.Windows.MessageBoxResult.Yes) return;

                _main?.RemoveRecipe(_item, _kind, row.Key, row.IsUserCreated);
                Reload();
            });

            RestoreCommand = new RelayCommand<OtherRecipeVM>(row =>
            {
                if (row == null || !row.IsRemoved) return;
                _main?.RestoreRecipe(_item, row.Key);
                Reload();
            });

            Reload();
        }

        private static string KindLabel(RecipeKind kind) => kind switch
        {
            RecipeKind.Crafting => "Crafting",
            RecipeKind.Temper => "Temper",
            _ => "Breakdown",
        };

        public void Reload()
        {
            Rows.Clear();

            var mainKey = MainRecipeStore.MainFor(_item.Key, _kind);
            var removed = RemovalStore.RemovedKeys(RemovalScope.Recipe, _item.Key);

            foreach (var row in BuildRows(mainKey, removed))
                Rows.Add(row);

            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsEmpty));
        }

        // Removed recipes stay in the list, marked - otherwise taking one out would make it vanish
        // with no way back, and the decision IS undoable (the row in RemovalStore is the whole of
        // it).
        // One shape for all three kinds, over the item's All* record lists. Those keep the REMOVED
        // recipes as well, which is the point: a removal is a decision, so it has to stay visible
        // and reversible rather than making the row disappear.
        private IEnumerable<OtherRecipeVM> BuildRows(string mainKey, IReadOnlyCollection<string> removed)
        {
            var (all, shownKey) = _kind switch
            {
                RecipeKind.Crafting => (_item.AllCraftingRecipes, _item.CraftingRecipe?.Key),
                RecipeKind.Temper => (_item.AllTemperRecipes, _item.TemperRecipe?.Key),
                _ => (_item.AllBreakdownRecipes, _item.BreakdownRecipe?.Key),
            };

            foreach (var record in all ?? new List<COBJRecord>())
            {
                bool isRemoved = removed.Contains(record.Key);

                yield return new OtherRecipeVM(
                    record.Key,
                    record.Name,
                    WorkbenchNameOf(record.WorkbenchKeywordKey),
                    DescribeRecipe(record),
                    // A removed recipe is never the main one, whatever an old decision still says.
                    isMain: !isRemoved
                            && (string.Equals(record.Key, shownKey, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(record.Key, mainKey, StringComparison.OrdinalIgnoreCase)),
                    isUserCreated: record.Original == 0,
                    isRemoved: isRemoved);
            }
        }

        // A breakdown recipe is read the other way round: what matters is what it PRODUCES, not
        // what it costs.
        private string DescribeRecipe(COBJRecord record) =>
            _kind == RecipeKind.Breakdown
                ? $"{record.CreatedObjectCount}x {NameOf(record.CreatedItemKey)}"
                : DescribeIngredients(record);

        // ReferenceLookup is a value type, so it cannot be null-conditionalled; an unresolved one
        // comes back with Found = false and a null Name. The raw key beats a blank cell - it is
        // what the user would search for.
        private string WorkbenchNameOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "(none)";

            var lookup = _main?.References?.Resolve(key);
            return string.IsNullOrWhiteSpace(lookup?.Name) ? key : lookup.Value.Name;
        }

        private string DescribeIngredients(COBJRecord record)
        {
            if (record.IngredientKeys == null || record.IngredientKeys.Count == 0) return "(no materials)";

            return string.Join(", ", record.IngredientKeys.Select(raw =>
            {
                var (key, count) = BreakdownRecipeVM.ParseIngredient(raw);
                return $"{NameOf(key)} x{count}";
            }));
        }

        private string NameOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "(none)";

            var name = _main?.FormIdService?.GetByKey(key)?.Name;
            return string.IsNullOrWhiteSpace(name) ? key : name;
        }
    }
}
