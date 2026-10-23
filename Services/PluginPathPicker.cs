using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One discarded duplicate, kept so the scan can say what it ignored instead of silently
    // dropping half the candidates.
    public sealed record PluginPathConflict(string FileName, string Chosen, IReadOnlyList<string> Discarded, string Reason);

    public sealed class PluginPathSelection
    {
        public IReadOnlyList<string> Chosen { get; init; } = Array.Empty<string>();
        public IReadOnlyList<PluginPathConflict> Conflicts { get; init; } = Array.Empty<PluginPathConflict>();
    }

    // Picks ONE path per plugin file name.
    //
    // THE BUG THIS FIXES. FileDBHandler.ScanFileSystemForPlugins searches the game's Data folder AND
    // the mod folder recursively, and every path whose file name appeared in plugins.txt was written
    // to the Plugins table. GetActivePlugins then collected ALL of them into PluginInfo.FullPaths,
    // and both scans do SelectMany over that - so a plugin that exists twice was PARSED TWICE and
    // WRITTEN TWICE. Which copy ended up in the database was decided by the row order of
    // "SELECT FileName, FullPath FROM Plugins WHERE Active = 1", a query with no ORDER BY, i.e. by
    // the order a recursive Directory.GetFiles happened to insert them in.
    //
    // That is not a corner case. Measured on the author's own setup (2026-10-05): 96 plugin files
    // existed in two copies - every vanilla master, every Creation Club file, and a dozen mods.
    // Among them SkyrimCraftingTool.esp in a mod folder that modlist.txt marks DISABLED and in
    // another that is enabled: the copy the game never loads could win.
    //
    // THE RULES, in order. They differ by manager because the managers differ:
    //
    //   MO2   - the VFS makes a mod folder override the game folder, and mod-vs-mod is decided by
    //           modlist.txt priority. A plugin is only loadable from a mod's ROOT, so a copy buried
    //           deeper inside a mod (FaceGen or voice folders are named after plugins) is not a
    //           candidate at all. A disabled mod is not loaded, so its copy loses outright.
    //   Vortex,
    //   Other,
    //   Unknown - deployment puts the real file in the game's Data folder, so the Data copy wins.
    //
    // Whatever happens, a file name that had candidates always gets exactly one back. Returning
    // nothing would silently shrink the scan, which is a worse failure than picking the second-best
    // copy - so the last rule is always a deterministic fallback, and it is reported as a conflict.
    public static class PluginPathPicker
    {
        public static PluginPathSelection PickOnePerFileName(
            IEnumerable<string> candidatePaths,
            string gameDataPath,
            string modDirectory,
            ModManagerKind kind,
            ModListOrder modList = null)
        {
            var chosen = new List<string>();
            var conflicts = new List<PluginPathConflict>();

            if (candidatePaths == null)
                return new PluginPathSelection();

            modList ??= ModListOrder.Empty;

            var groups = candidatePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                // Stable, manager-independent output order so two scans of the same disk agree.
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                // Deterministic starting order: whatever the file system handed us is not stable
                // across machines, and the tie-breaks below must be reproducible.
                var candidates = group
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidates.Count == 1)
                {
                    chosen.Add(candidates[0]);
                    continue;
                }

                // Loadability FIRST, and only then "is this a conflict?".
                //
                // A plugin is only ever loaded from the game folder's root or from a mod's root.
                // FaceGen and voice directories are named after the plugin they belong to, so a
                // modlist routinely holds copies like "<mod>\Sound\Voice\Thing.esp". Those are not
                // an alternative to anything - counting them as a second candidate would report a
                // conflict for every one of them and bury the real ones in the log.
                var loadable = candidates
                    .Where(p => IsLoadable(p, gameDataPath, modDirectory))
                    .ToList();

                if (loadable.Count == 1)
                {
                    chosen.Add(loadable[0]);
                    continue;
                }

                // None of the copies sits where a plugin can be loaded from. Odd enough to report,
                // and still resolved rather than dropped - losing the plugin would shrink the scan
                // silently, which is worse than reading an unusual copy of it.
                var pool = loadable.Count > 0 ? loadable : candidates;

                var (winner, reason) = loadable.Count == 0
                    ? (pool[0], "no copy sits where a plugin can be loaded from - path order used")
                    : kind == ModManagerKind.ModOrganizer2
                        ? PickForMo2(pool, gameDataPath, modDirectory, modList)
                        : PickForDeployedLayout(pool, gameDataPath);

                chosen.Add(winner);
                conflicts.Add(new PluginPathConflict(
                    group.Key,
                    winner,
                    pool.Where(p => !string.Equals(p, winner, StringComparison.OrdinalIgnoreCase)).ToList(),
                    reason));
            }

            return new PluginPathSelection { Chosen = chosen, Conflicts = conflicts };
        }

        // Every path handed in here is already known to be loadable - see PickOnePerFileName.
        private static (string Winner, string Reason) PickForMo2(
            List<string> candidates, string gameDataPath, string modDirectory, ModListOrder modList)
        {
            var modRootCopies = candidates
                .Select(p => (Path: p, Mod: ModFolderOf(p, modDirectory)))
                .Where(x => x.Mod != null)
                .ToList();

            var enabled = modRootCopies.Where(x => !modList.IsDisabled(x.Mod)).ToList();

            if (enabled.Count > 0)
            {
                // Lowest rank wins; ties (both unknown to modlist.txt) fall back to the path, which
                // is already in a deterministic order.
                var best = enabled
                    .OrderBy(x => modList.RankOf(x.Mod))
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .First();

                var reason = modList.IsEmpty
                    ? "MO2: mod folder overrides the game folder (no modlist.txt available, path order used)"
                    : modList.RankOf(best.Mod) == int.MaxValue
                        ? "MO2: mod folder overrides the game folder (not listed in modlist.txt, path order used)"
                        : $"MO2: highest modlist.txt priority ({best.Mod})";

                return (best.Path, reason);
            }

            if (modRootCopies.Count > 0)
            {
                // Every mod copy is in a disabled folder. The game folder is then the only thing MO2
                // would actually load, if it has a copy at all.
                var dataCopy = FindUnder(candidates, gameDataPath);
                if (dataCopy != null)
                    return (dataCopy, "MO2: every mod copy is in a disabled mod, using the game folder");

                // plugins.txt says this plugin is active while every folder holding it is switched
                // off - a contradictory state. Take the best disabled copy rather than drop the
                // plugin from the scan entirely.
                var fallback = modRootCopies
                    .OrderBy(x => modList.RankOf(x.Mod))
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .First();

                return (fallback.Path,
                    "MO2: active in plugins.txt but every copy sits in a disabled mod - check the setup");
            }

            // No mod-root copy at all: either it lives in the game folder, or every copy is buried
            // inside a mod's asset folders (a FaceGen or voice directory named after a plugin).
            var data = FindUnder(candidates, gameDataPath);
            if (data != null)
                return (data, "MO2: only the game folder holds a loadable copy");

            return (candidates[0], "MO2: no copy sits where a plugin can be loaded from - path order used");
        }

        private static (string Winner, string Reason) PickForDeployedLayout(List<string> candidates, string gameDataPath)
        {
            var data = FindUnder(candidates, gameDataPath);
            if (data != null)
                return (data, "deployed copy in the game's Data folder");

            return (candidates[0], "no copy in the game's Data folder - path order used");
        }

        // Where the game can actually load a plugin from: the root of the game's Data folder, or
        // the root of a mod folder (which MO2's VFS maps onto that same place). Anything deeper is
        // an asset directory that merely shares the plugin's name.
        internal static bool IsLoadable(string path, string gameDataPath, string modDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (!string.IsNullOrWhiteSpace(gameDataPath)
                && IsUnder(path, gameDataPath)
                && string.Equals(Path.GetDirectoryName(Normalize(path)), Normalize(gameDataPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ModFolderOf(path, modDirectory) != null;
        }

        private static string FindUnder(IEnumerable<string> candidates, string root) =>
            string.IsNullOrWhiteSpace(root)
                ? null
                : candidates.FirstOrDefault(p => IsUnder(p, root));

        // The mod folder name when the path is exactly <modDirectory>\<ModName>\<file>, else null.
        // Null is also the answer for a copy nested deeper, which is the point: MO2 does not load
        // those.
        internal static string ModFolderOf(string path, string modDirectory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(modDirectory)) return null;
            if (!IsUnder(path, modDirectory)) return null;

            var relative = Path.GetRelativePath(Normalize(modDirectory), Normalize(path));
            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            return segments.Length == 2 ? segments[0] : null;
        }

        internal static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

            var normalizedRoot = Normalize(root);
            var normalizedPath = Normalize(path);

            if (normalizedPath.Length <= normalizedRoot.Length) return false;
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;

            // Guard against "C:\Mods" matching "C:\ModsOld\x.esp".
            var next = normalizedPath[normalizedRoot.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        private static string Normalize(string path) =>
            path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
    }
}
