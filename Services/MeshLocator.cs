using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyrimCraftingTool.Model;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyrimCraftingTool.Services
{
    // Where a record's model path actually lives.
    //
    // Unresolved is not an error state so much as a fact worth reporting: measured against a real
    // 208-mod setup, 32 of 4.460 paths pointed at a mesh no archive and no mod folder carries. Some
    // of those are plainly broken records (one ended in "_1 - .nif"), which is exactly the kind of
    // thing a scan should be able to name rather than discover later as an empty viewport.
    public enum MeshSourceKind
    {
        Unresolved = 0,
        Loose = 1,
        Archive = 2,
    }

    public sealed record MeshSource(MeshSourceKind Kind, string Reference)
    {
        public static readonly MeshSource None = new(MeshSourceKind.Unresolved, "");
    }

    // One spelling for a model path, so the same mesh is one row rather than four.
    //
    // THE RECORD DOES NOT STORE A LOCATION. ARMA/WEAP hold a logical name relative to meshes\
    // ("Armor\Daedric\Cuirass_1.nif"); BSA entries are keyed by the full data-relative path,
    // lowercased, with backslashes ("meshes\armor\daedric\cuirass_1.nif"). Those two have to be made
    // into one string before anything can be compared, or every lookup misses.
    //
    // Mutagen's AssetLink.DataRelativePath would prepend "Meshes\" on its own, but it is not used
    // here: the prefix also has to be added to paths coming from a BSA listing and from a loose-file
    // walk, and one function that all three go through is the only way those three can agree.
    //
    // Casing is NOT a detail. Vanilla writes "Armor\BoneCrown\...", BSAs store lowercase, and mods
    // use whatever the author typed. This is the same class of bug the Plugin|FormID keys already
    // paid for once - hence lowercase here AND COLLATE NOCASE on the column.
    public static class MeshPath
    {
        public const string MeshesPrefix = @"meshes\";
        public const string TexturesPrefix = @"textures\";

        public static string? Normalize(string? givenPath) => Normalize(givenPath, MeshesPrefix);

        // The same rule for the texture paths a NIF's shader blocks carry. They behave exactly like
        // model paths - relative to textures\ in the record, already prefixed in an archive listing, and
        // written in whatever casing the author used - so they go through the same function rather than
        // a near-copy that could drift from it.
        public static string? NormalizeTexture(string? givenPath) => Normalize(givenPath, TexturesPrefix);

        private static string? Normalize(string? givenPath, string prefix)
        {
            if (string.IsNullOrWhiteSpace(givenPath)) return null;

            var path = givenPath.Trim()
                                .Replace('/', '\\')
                                .TrimStart('\\')
                                .ToLowerInvariant()
                                .TrimEnd('\0');

            if (path.Length == 0) return null;

            // A record path is relative to the folder, an archive path already contains it. Both end up
            // with exactly one.
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                path = prefix + path;

            return path;
        }
    }

    // The name -> bytes half of the mesh index: which archive or loose file a normalised path
    // resolves to, resolved the way the game resolves it.
    //
    // PRIORITY, highest first:
    //   1. a loose file, from the highest-priority mod that ships one
    //   2. a BSA entry, from the highest-priority mod that ships one
    // Loose beats archived outright - that is the engine's own rule, and it is why a replacer mod
    // that ships no BSA works at all.
    //
    // Under MO2 the Data folder is virtual, so "is there a loose file" cannot be answered by looking
    // at Data alone: the answer is spread over mods\<mod>\meshes\ and decided by modlist.txt
    // (ModListOrder, top wins). Without a mod list - Vortex, or a hand-built setup - deployment has
    // already flattened everything into Data and walking Data alone is then the correct answer, not
    // a fallback.
    //
    // Cost, measured on 208 mods / 139 BSAs: 640 ms for 10.645 loose meshes, 289 ms for 28.938 BSA
    // entries. That is cheap enough to rebuild on every scan, which is why nothing here is cached
    // between runs - a stale index would answer for a mod the user has since disabled.
    public sealed class MeshLocator
    {
        // Value is the label that goes into the database AND what is needed to open the file.
        //
        // The archive half keeps the IArchiveFile rather than the archive's name alone: that entry
        // already knows its offset and length, so reading it later costs no second listing pass. It
        // also keeps the archive reader alive, which is the point - 139 archives were opened once to
        // build this, and reopening one per mesh would undo that.
        private readonly Dictionary<string, (string Label, string FullPath)> _loose;
        private readonly Dictionary<string, (string Label, IArchiveFile Entry)> _archive;

        public int LooseCount => _loose.Count;
        public int ArchiveCount => _archive.Count;

        // Archives that could not be opened. Reported rather than thrown: one corrupt BSA in a
        // 139-archive setup must not cost the whole index.
        public IReadOnlyList<string> UnreadableArchives { get; }

        private MeshLocator(
            Dictionary<string, (string, string)> loose,
            Dictionary<string, (string, IArchiveFile)> archive,
            IReadOnlyList<string> unreadable)
        {
            _loose = loose;
            _archive = archive;
            UnreadableArchives = unreadable;
        }

        public static MeshLocator Empty { get; } = new(
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, (string, IArchiveFile)>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>());

        public MeshSource Resolve(string? pathNorm)
        {
            if (string.IsNullOrEmpty(pathNorm)) return MeshSource.None;

            if (_loose.TryGetValue(pathNorm, out var loose))
                return new MeshSource(MeshSourceKind.Loose, loose.Label);

            if (_archive.TryGetValue(pathNorm, out var archive))
                return new MeshSource(MeshSourceKind.Archive, archive.Label);

            return MeshSource.None;
        }

        // The bytes of a mesh, from wherever it actually lives.
        //
        // Nothing is written to disk. A BSA entry goes straight from the archive into memory, which is
        // what lets stage 2 parse 4.335 meshes without unpacking the ~2,5 GB the vanilla archives hold.
        // Resolution order is Resolve's, so the bytes come from the file the game would load.
        public bool TryReadBytes(string? pathNorm, out byte[] bytes, out string error)
        {
            bytes = Array.Empty<byte>();
            error = "";

            if (string.IsNullOrEmpty(pathNorm)) { error = "no path"; return false; }

            if (_loose.TryGetValue(pathNorm, out var loose))
            {
                try
                {
                    bytes = File.ReadAllBytes(loose.FullPath);
                    return true;
                }
                catch (Exception ex)
                {
                    // The file was there when the index was built and is not now, or is locked.
                    error = $"loose file could not be read: {ex.Message}";
                    return false;
                }
            }

            if (_archive.TryGetValue(pathNorm, out var archive))
            {
                try
                {
                    bytes = archive.Entry.GetBytes();
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"archive entry could not be read: {ex.Message}";
                    return false;
                }
            }

            error = "no mod and no archive carries this path";
            return false;
        }

        // includeTextures is off for the scan and on for the preview. The archive pass gets textures for
        // free - it already walks every entry of every BSA - but the loose pass has to walk a second
        // directory tree, and the scan has no use for it. Measured: 14.271 loose textures against 10.645
        // loose meshes, so it is roughly a doubling of that part.
        public static MeshLocator Build(bool includeTextures = false) =>
            Build(GlobalState.GameDataPath, GlobalState.ModDirectoryPath,
                  ModListOrder.Read(GlobalState.ModListFilePath), includeTextures);

        public static MeshLocator Build(string? dataFolder, string? modsFolder, ModListOrder? modOrder,
                                        bool includeTextures = false)
        {
            var loose = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            var archive = new Dictionary<string, (string, IArchiveFile)>(StringComparer.OrdinalIgnoreCase);
            var unreadable = new List<string>();

            // Lowest priority first, so a higher-priority mod simply overwrites the entry. The two
            // passes are separate rather than interleaved because loose beats archived ACROSS mods:
            // a loose file in the lowest-priority mod still wins over a BSA in the highest.
            var roots = new List<(string Folder, string Label)>();

            if (!string.IsNullOrWhiteSpace(dataFolder) && Directory.Exists(dataFolder))
                roots.Add((dataFolder, "<Data>"));

            if (modOrder is { IsEmpty: false } && !string.IsNullOrWhiteSpace(modsFolder) && Directory.Exists(modsFolder))
            {
                for (int i = modOrder.EnabledHighestFirst.Count - 1; i >= 0; i--)
                {
                    var name = modOrder.EnabledHighestFirst[i];
                    var folder = Path.Combine(modsFolder, name);
                    if (Directory.Exists(folder)) roots.Add((folder, name));
                }
            }

            foreach (var (folder, label) in roots)
            {
                IndexLoose(folder, label, loose, "meshes", "*.nif");
                if (includeTextures) IndexLoose(folder, label, loose, "textures", "*.dds");
            }
            foreach (var (folder, label) in roots) IndexArchives(folder, archive, unreadable, includeTextures);

            return new MeshLocator(loose, archive, unreadable);
        }

        // Meshes and textures share one dictionary. They cannot collide: a normalised path always starts
        // with its own folder, and that prefix is part of the key.
        private static void IndexLoose(string root, string label, Dictionary<string, (string, string)> into,
                                       string folderName, string pattern)
        {
            var folder = Path.Combine(root, folderName);
            if (!Directory.Exists(folder)) return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                {
                    var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                    var norm = folderName == "meshes"
                        ? MeshPath.Normalize(relative)
                        : MeshPath.NormalizeTexture(relative);

                    if (norm != null) into[norm] = (label, file);
                }
            }
            catch (Exception ex)
            {
                // A mod folder that vanished mid-walk, or one the user has no rights to. The index is
                // allowed to be incomplete; it is not allowed to stop a scan.
                AppLogger.LogWarning($"Mesh index: loose walk of {label}\\{folderName} failed ({ex.Message})");
            }
        }

        private static void IndexArchives(string folder, Dictionary<string, (string, IArchiveFile)> into,
                                          List<string> unreadable, bool includeTextures)
        {
            string[] archives;
            try
            {
                archives = Directory.GetFiles(folder, "*.bsa");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Mesh index: listing archives in {folder} failed ({ex.Message})");
                return;
            }

            foreach (var path in archives)
            {
                try
                {
                    var reader = Archive.CreateReader(GameRelease.SkyrimSE, path);
                    foreach (var file in reader.Files)
                    {
                        string? norm = null;

                        if (file.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                            norm = MeshPath.Normalize(file.Path);
                        else if (includeTextures && file.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                            norm = MeshPath.NormalizeTexture(file.Path);

                        if (norm != null) into[norm] = (Path.GetFileName(path), file);
                    }
                }
                catch (Exception ex)
                {
                    unreadable.Add(Path.GetFileName(path));
                    AppLogger.LogWarning($"Mesh index: {Path.GetFileName(path)} could not be read ({ex.Message})");
                }
            }
        }
    }
}
