using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.TemplateChildren;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // What a preset patch over a set of items touches, now that template children follow their
    // template (docs/Plan_Aktuell/KinderPatcher-Plan.md, K3):
    // - the standard system computes every item that is no child;
    // - the template of every chosen child is computed and patched with it (decision c, 2026-10-28) -
    //   otherwise a run over Thaumaturgy alone would change nothing: its children follow vanilla
    //   templates the run never touched;
    // - every child of a computed template gets its extras (ChildExtras) refreshed, chosen or not, so
    //   an enchanted master robe outside the run still carries its x512 when its plain robe changes.
    internal sealed class ChildScope
    {
        public List<ItemNodeVM> Computed { get; } = new();
        public HashSet<string> AddedTemplates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ItemNodeVM> Children { get; } = new();
        public HashSet<string> Requested { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SkippedNoName { get; private set; }

        public static ChildScope Plan(MainContentVM main, IReadOnlyList<ItemNodeVM> items, Func<ItemNodeVM, bool> ofKind,
            TemplateFamilies families)
        {
            var scope = new ChildScope();
            var nodes = main.ItemNodesByKey();
            var computed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requestedNodes = new List<ItemNodeVM>();

            foreach (var item in items)
            {
                if (!ofKind(item)) continue;
                if (ModularPresetApplier.IsSkipped(item)) { scope.SkippedNoName++; continue; }
                if (scope.Requested.Add(item.Key)) requestedNodes.Add(item);
            }

            // Templates of chosen children first: the children's extras compare against the
            // template's selection of this run.
            foreach (var item in requestedNodes)
            {
                var root = families.RootOf(item.Key);
                if (root == null) continue;
                children.Add(item.Key);
                if (scope.Requested.Contains(root) || computed.Contains(root)) continue;
                if (!nodes.TryGetValue(root, out var template) || !ofKind(template) || ModularPresetApplier.IsSkipped(template)) continue;
                computed.Add(root);
                scope.AddedTemplates.Add(root);
                scope.Computed.Add(template);
            }
            foreach (var item in requestedNodes)
            {
                if (children.Contains(item.Key) || !computed.Add(item.Key)) continue;
                scope.Computed.Add(item);
            }

            foreach (var item in scope.Computed)
                foreach (var child in families.ChildrenOf(item.Key))
                    children.Add(child);

            foreach (var key in children)
                if (nodes.TryGetValue(key, out var child) && ofKind(child) && !ModularPresetApplier.IsSkipped(child))
                    scope.Children.Add(child);
            return scope;
        }
    }

    // The extras of EVERY template child from its detected addons, without patching anything (K5).
    // Before the Kinder-Patcher a child's own addons lived in its own numbers (a master robe's x512
    // was in its value); the cleanup clears those, so the extras are computed first. Only the
    // detection - the dry run's plugin and item choices are not kept anywhere to replay.
    internal static class ChildExtrasRefresh
    {
        public static List<ChildExtras> All(MainContentVM main)
        {
            var result = new List<ChildExtras>();
            var nodes = main.ItemNodesByKey();

            if (ModularCatalogCache.Current is { } armor)
                Collect(main, nodes, main.ArmorFamilies, i => i.IsArmor, armor.Addons,
                    n => ModularPrefill.Prefill(armor, ModularPresetApplier.StateOf(main, n)).Selection, result);

            WeaponCatalog? weapons = null;
            try { weapons = new WeaponCatalogStore(ModularCatalogStore.WeaponsRoot).Read(); }
            catch (Exception ex) { AppLogger.LogError("ChildExtrasRefresh: weapon catalog unreadable - weapon children keep their extras", ex); }
            if (weapons != null)
                Collect(main, nodes, main.WeaponFamilies, i => i.IsWeapon, weapons.Addons,
                    n => WeaponPrefill.Prefill(weapons, WeaponPresetApplier.StateOf(main, n)).Selection, result);
            return result;
        }

        private static void Collect(MainContentVM main, Dictionary<string, ItemNodeVM> nodes, TemplateFamilies families,
            Func<ItemNodeVM, bool> ofKind, IReadOnlyList<Model.KeywordAddon> addons,
            Func<ItemNodeVM, ModularSelection> detect, List<ChildExtras> result)
        {
            var templates = new Dictionary<string, ModularSelection?>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.Values)
            {
                if (!ofKind(node) || ModularPresetApplier.IsSkipped(node)) continue;
                var root = families.RootOf(node.Key);
                if (root == null) continue;
                if (!templates.TryGetValue(root, out var templateSelection))
                {
                    templateSelection = nodes.TryGetValue(root, out var t) && ofKind(t) && !ModularPresetApplier.IsSkipped(t) ? detect(t) : null;
                    templates[root] = templateSelection;
                }
                result.Add(ChildExtrasCalculator.Compute(addons, node.Key, detect(node), templateSelection));
            }
        }
    }
}
