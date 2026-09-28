using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // A branch in the NPC tree: a plugin at the top, a class below it. Both levels behave the same,
    // so they are the same type - unlike the item tree, where plugin and category nodes each carry
    // their own commands.
    //
    // The tree is Plugin -> Class -> NPC and deliberately has no faction level: a faction spans
    // plugins (BanditFaction is defined in Skyrim.esm and reaches 613 NPCs across several), so
    // nesting it under one made a node's count differ from what a rule on it would patch. Factions
    // get their own view instead, where the number shown is the number patched. See
    // docs/NPC-Plan.md §8.
    public class NpcTreeNodeVM : ViewModelBase
    {
        public NpcTreeNodeVM(string displayName, bool isPlugin)
        {
            DisplayName = displayName;
            IsPlugin = isPlugin;
        }

        public string DisplayName { get; }

        // Drives the tree's three-level colouring, the same treatment the item and preset trees use.
        public bool IsPlugin { get; }
        public bool IsBranch => true;

        public ObservableCollection<NpcTreeNodeVM> Children { get; } = new();
        public ObservableCollection<NpcNodeVM> Npcs { get; } = new();

        // One list for the TreeView to bind to, because a node has either children or leaves and
        // WPF wants a single ItemsSource.
        public IEnumerable<object> Items => Children.Cast<object>().Concat(Npcs);

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        // Leaves below this node, at any depth. Shown on the branch because the counts are the whole
        // point of this tree shape: 905 under Skyrim.esm > CombatWarrior1H, 3 under most others.
        public int NpcCount => Npcs.Count + Children.Sum(c => c.NpcCount);

        public string CountDisplay => $"({NpcCount})";

        public bool HasEditedNpcs => Npcs.Any(n => n.IsEdited) || Children.Any(c => c.HasEditedNpcs);

        // Filtered COPY that shares the leaf view models - never clones them, the way
        // CategoryNodeVM.FilterReference does it. Two copies of an NPC would drift apart the moment
        // one is edited.
        // factionKey narrows the tree to the members of one faction - what the separate faction view
        // used to be for (docs/NPC-Plan.md §12). It belongs here rather than in a view of its own:
        // the thing anyone wanted from it is "show me who hangs together", and the answer is a list
        // of NPCs. A faction rule does NOT need this - filterByFactions patches by membership at
        // runtime, which is the whole point of it - so this is a lens, not a selection.
        public NpcTreeNodeVM? Filter(string text, bool parentMatches = false, bool onlyEdited = false,
                                     string factionKey = "")
        {
            bool selfMatches = parentMatches
                || string.IsNullOrWhiteSpace(text)
                || DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase);

            var copy = new NpcTreeNodeVM(DisplayName, IsPlugin) { IsExpanded = IsExpanded };

            foreach (var child in Children)
            {
                var filtered = child.Filter(text, selfMatches, onlyEdited, factionKey);
                if (filtered != null) copy.Children.Add(filtered);
            }

            foreach (var npc in Npcs)
            {
                if (onlyEdited && !npc.IsEdited) continue;

                if (!string.IsNullOrEmpty(factionKey)
                    && !npc.Factions.Any(f => string.Equals(f.FactionKey, factionKey,
                                                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                bool hit = selfMatches
                    || npc.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || npc.EditorID.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || npc.Key.Contains(text, StringComparison.OrdinalIgnoreCase);

                if (hit) copy.Npcs.Add(npc);
            }

            return copy.Children.Count > 0 || copy.Npcs.Count > 0 ? copy : null;
        }

        public void CollapseAll()
        {
            IsExpanded = false;
            foreach (var child in Children) child.CollapseAll();
        }

        // Builds the whole tree from a flat NPC list. Grouping by the plugin the FormID names, not by
        // the plugin that last overrode the record: the key is what every rule and every other table
        // in this tool is addressed by, so the tree has to agree with it.
        public static List<NpcTreeNodeVM> Build(IEnumerable<NpcNodeVM> npcs)
        {
            var roots = new List<NpcTreeNodeVM>();

            foreach (var byPlugin in (npcs ?? Enumerable.Empty<NpcNodeVM>())
                        .GroupBy(n => n.PluginName, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var pluginNode = new NpcTreeNodeVM(byPlugin.Key, isPlugin: true);

                foreach (var byClass in byPlugin
                            .GroupBy(n => n.ClassDisplay, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var classNode = new NpcTreeNodeVM(byClass.Key, isPlugin: false);

                    foreach (var npc in byClass.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
                        classNode.Npcs.Add(npc);

                    pluginNode.Children.Add(classNode);
                }

                roots.Add(pluginNode);
            }

            return roots;
        }
    }
}
