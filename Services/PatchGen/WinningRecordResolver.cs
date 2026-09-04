using System;
using System.Collections.Generic;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

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
            if (wantedCobj.Count == 0 && wantedEnchantments.Count == 0) return resolver;

            foreach (var (fileName, fullPath) in pluginsInLoadOrder)
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) continue;

                try
                {
                    var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);
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
