using System;
using System.Collections.Generic;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Resolves the WINNING override of a record straight out of the plugin files, so the ESP builder
    // can deep-copy a real record instead of assembling one from the handful of fields item.db
    // tracks.
    //
    // Why this exists: building an override from scratch silently defaults every field the tool
    // doesn't track. For COBJ that is CreatedObjectCount, the EditorID and - by far the worst - the
    // conditions. For ObjectEffect it would be far more destructive still: EnchantType, ChargeTime,
    // Flags, EnchantmentAmount, ObjectBounds, BaseEnchantment, CastType, TargetType, Effects and
    // Name are all untracked, and a from-scratch override would blank every one of them.
    //
    // Deep-copying the winner makes that a non-issue: untracked fields carry over untouched because
    // nothing ever writes to them.
    //
    // COBJ and ENCH are resolved in ONE pass. Both patch paths share the same generated ESP, and
    // opening a 120-plugin load order twice would double the most expensive part of the export.
    //
    // Overlays are lazy readers over memory-mapped files, so the returned getters stay valid only
    // while this object lives - deep-copy before disposing it.
    public sealed class WinningRecordResolver : IDisposable
    {
        private readonly List<IDisposable> _open = new();
        private readonly Dictionary<FormKey, IConstructibleObjectGetter> _cobj = new();
        private readonly Dictionary<FormKey, IObjectEffectGetter> _ench = new();

        private WinningRecordResolver() { }

        // pluginsInLoadOrder must be in real load order: later entries overwrite earlier ones, which
        // is exactly how the game resolves the winner.
        //
        // The wanted sets limit what is retained. Callers know every FormKey they are going to
        // override, and keeping only those avoids holding a dictionary of the whole load order.
        public static WinningRecordResolver Open(
            IEnumerable<(string FileName, string FullPath)> pluginsInLoadOrder,
            IReadOnlySet<FormKey> wantedCobj,
            IReadOnlySet<FormKey> wantedEnchantments,
            ICollection<string> warnings)
        {
            var resolver = new WinningRecordResolver();
            if (wantedCobj.Count == 0 && wantedEnchantments.Count == 0)
                return resolver;

            foreach (var (fileName, fullPath) in pluginsInLoadOrder)
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) continue;

                try
                {
                    var mod = OpenWithStrings(fullPath);
                    resolver._open.Add(mod);

                    if (wantedCobj.Count > 0)
                        foreach (var cobj in mod.ConstructibleObjects)
                            if (wantedCobj.Contains(cobj.FormKey))
                                resolver._cobj[cobj.FormKey] = cobj; // later plugin wins

                    if (wantedEnchantments.Count > 0)
                        foreach (var ench in mod.ObjectEffects)
                            if (wantedEnchantments.Contains(ench.FormKey))
                                resolver._ench[ench.FormKey] = ench;
                }
                catch (Exception ex)
                {
                    // A single unreadable plugin must not sink the whole export - the builder falls
                    // back to from-scratch (COBJ) or skips (ENCH) and warns about that too.
                    warnings.Add($"{fileName}: could not be read for override lookup — {ex.Message}");
                }
            }

            return resolver;
        }

        // Opens a plugin so its LOCALIZED text survives the deep copy.
        //
        // The problem, found against the real load order: a localized plugin keeps its display names
        // in .STRINGS files, not in the record - the record holds an index. Mutagen resolves that
        // index from the Strings folder or the BSAs sitting NEXT TO the plugin file. A cleaned
        // vanilla master (D:\...\mods\Cleaned Plugins\Skyrim.esm) has neither, because the BSAs
        // stayed behind in the game's Data folder - so every name reads back as the empty string,
        // and an override deep-copied from it would ship a record with no name at all. Measured on
        // an
        // : it resolved fine, kept its face, and lost "Whiterun Guard". That editor is gone
        // since, but the same path carries every ENCH/COBJ override this tool writes.
        //
        // Only plugins that CANNOT resolve their own strings get the fallback: a mod that brings its
        // own (loose in Strings\, or in a BSA in the same folder) keeps using them, because pointing
        // it at the game folder instead would break exactly those. When neither exists, the strings
        // are unreachable either way and the game folder is strictly better than nothing.
        private static ISkyrimModDisposableGetter OpenWithStrings(string fullPath)
        {
            var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

            bool localized = mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Localized);
            if (!NeedsStringsFallback(fullPath, localized, GlobalState.GameDataPath))
                return mod;

            // Reopened, not patched in place: the strings source is chosen when the file is read.
            mod.Dispose();

            return SkyrimMod.Create(SkyrimRelease.SkyrimSE)
                .FromPath(fullPath)
                .WithBsaFolder(GlobalState.GameDataPath)
                .Construct();
        }

        // Split out, and taking the game folder as an argument rather than reading GlobalState: the
        // decision is pure path logic, and a test for it should not have to set global state that
        // every other test in the process can see.
        internal static bool NeedsStringsFallback(string fullPath, bool localized, string gameDataPath)
        {
            if (!localized) return false;
            if (string.IsNullOrWhiteSpace(gameDataPath)) return false;
            if (!Directory.Exists(gameDataPath)) return false;

            var dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

            // Already in the game folder: the normal lookup is looking in the right place.
            if (string.Equals(
                    Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            if (Directory.Exists(Path.Combine(dir, "Strings"))) return false;

            // Any BSA in the folder counts: a mod that ships localized text ships the archive that
            // holds it, and guessing which archive name belongs to which plugin is a game of its own.
            foreach (var _ in Directory.EnumerateFiles(dir, "*.bsa")) return false;

            return true;
        }

        public bool TryGetCobj(FormKey key, out IConstructibleObjectGetter winner) =>
            _cobj.TryGetValue(key, out winner!);

        public bool TryGetEnchantment(FormKey key, out IObjectEffectGetter winner) =>
            _ench.TryGetValue(key, out winner!);

        public void Dispose()
        {
            foreach (var d in _open)
            {
                try { d.Dispose(); } catch { /* best effort */ }
            }
            _open.Clear();
            _cobj.Clear();
            _ench.Clear();
        }
    }
}
