using SkyrimCraftingTool.Model;
using System;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // The building blocks as the Items tab sees them: read once, dropped when the Presets tab saves
    // (ModularCatalogStore.Saved). Every build box asks here instead of reading five files on each
    // click in the tree.
    public static class ModularCatalogCache
    {
        private static ModularCatalog? _catalog;
        private static string _problem = "";

        static ModularCatalogCache()
        {
            ModularCatalogStore.Saved += Invalidate;
        }

        // Bumped on every invalidation, so a holder can tell its copy is old.
        public static int Version { get; private set; }

        public static void Invalidate()
        {
            _catalog = null;
            _problem = "";
            Version++;
        }

        // Null when there is nothing usable; Problem says why.
        public static ModularCatalog? Current
        {
            get
            {
                if (_catalog != null || _problem.Length > 0) return _catalog;
                try
                {
                    var c = new ModularCatalogStore(ModularCatalogStore.ArmorRoot).Read();
                    if (c.Types.Count == 0 || c.Slots.Count == 0)
                        _problem = "No building blocks yet - set up types and slot definitions on the Presets tab.";
                    else
                        _catalog = c;
                }
                catch (Exception ex)
                {
                    _problem = $"The building blocks could not be read: {ex.Message}";
                }
                return _catalog;
            }
        }

        public static string Problem
        {
            get { _ = Current; return _problem; }
        }
    }
}
