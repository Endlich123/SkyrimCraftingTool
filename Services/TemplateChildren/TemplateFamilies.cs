using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.ModularPresets;

namespace SkyrimCraftingTool.Services.TemplateChildren
{
    // Who is a template child, and which children hang below a template - over one record type
    // (armor or weapons; a template is always of its own type). Built once per cache load.
    public sealed class TemplateFamilies
    {
        private readonly Dictionary<string, string> _templateOf = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _rootOf = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILookup<string, string> _childrenByRoot;

        public TemplateFamilies(IEnumerable<(string Key, string? TemplateKey)> records)
        {
            foreach (var (key, template) in records)
                if (!string.IsNullOrWhiteSpace(key)) _templateOf.TryAdd(key, template ?? "");

            foreach (var key in _templateOf.Keys)
                _rootOf[key] = TemplateInheritance.RootOf(key, TemplateOf);
            _childrenByRoot = _rootOf.Where(kv => kv.Value != null)
                .ToLookup(kv => kv.Value!, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        }

        private string? TemplateOf(string key) => _templateOf.TryGetValue(key, out var t) ? t : null;

        // The template the item follows (the top of its chain), or null when it is no child.
        public string? RootOf(string key) => _rootOf.TryGetValue(key, out var root) ? root : null;

        public bool IsChild(string key) => RootOf(key) != null;

        // Every child that follows this template, chains included.
        public IReadOnlyList<string> ChildrenOf(string root) => _childrenByRoot[root].ToList();
    }

    // The extras of one child from the addons the preset engine detected on it (ChildExtras): what
    // its own addons do on top of its template. Moved here from TakeTemplateValues, which applied the
    // same arithmetic directly to the child's numbers.
    public static class ChildExtrasCalculator
    {
        // templateSelection: the template's selection in this run, null when the template is not
        // computed (not scanned, no name). A template HELD by an addon (KeepValues / LeaveUntouched -
        // the vanilla uniques: "TemplateClothesRobesMageMaster" is named "Master Robes" and keeps its
        // 5 gold) took none of its addons, so all of the child's count. A template that merely has no
        // material shares them all the same: a plugin tier on both a Glenmoril pistol and its "+1"
        // must not count for the child alone (2026-10-26).
        public static ChildExtras Compute(IReadOnlyList<KeywordAddon> catalogAddons, string childKey,
            ModularSelection childSelection, ModularSelection? templateSelection)
        {
            KeywordAddon? Find(string n) => catalogAddons.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase));

            var own = ModularPresetEngine.WithAlsoAddons(catalogAddons, childSelection.AddonNames)
                .Select(Find).Where(a => a != null).Select(a => a!).ToList();

            var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (templateSelection != null)
            {
                var templateAddons = ModularPresetEngine.WithAlsoAddons(catalogAddons, templateSelection.AddonNames);
                bool held = templateAddons.Select(Find).Any(a => a != null && (a.KeepValues || a.LeaveUntouched));
                if (!held) shared.UnionWith(templateAddons);
            }

            var x = new ChildExtras
            {
                Key = childKey,
                KeepValues = own.Any(a => a.KeepValues),
                KeepKeywords = own.Any(a => a.KeepKeywords),
                LeaveUntouched = own.Any(a => a.LeaveUntouched),
            };
            var named = new List<string>();
            foreach (var a in own)
            {
                bool counts = !shared.Contains(a.Name);
                if (counts)
                {
                    x.ArmorRatingFactor *= a.ArmorRatingFactor;
                    x.ValueFactor *= a.ValueFactor;
                    x.WeightFactor *= a.WeightFactor;
                    x.DamageFactor *= a.DamageFactor;
                }
                bool factor = counts && (a.ArmorRatingFactor != 1 || a.ValueFactor != 1 || a.WeightFactor != 1 || a.DamageFactor != 1);
                if (factor || a.KeepValues || a.KeepKeywords || a.LeaveUntouched)
                    named.Add(a.Name);
            }
            x.Addons = string.Join(", ", named);
            return x;
        }
    }
}
