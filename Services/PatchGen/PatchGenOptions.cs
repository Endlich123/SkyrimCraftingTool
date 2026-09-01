using System.IO;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public enum PatchPriorityMode
    {
        // zzz_ prefix -> loads after other SkyPatcher configs, so our field writes win.
        High,
        // Plain folder name -> third-party compat patches can override us.
        Normal,
    }

    public enum PatchCobjSplitMode
    {
        // One SkyrimCraftingTool.esp for every generated recipe.
        Global,
        // One ESP per source plugin: "SkyrimCraftingTool - <Plugin>.esp".
        PerSourcePlugin,
    }

    public sealed class PatchGenOptions
    {
        // Root that the SKSE\Plugins\SkyPatcher tree is written under. Defaults to the tool's
        // Output folder; overridable for tests. (Tool is null until GlobalState.Initialize runs.)
        public string OutputRoot { get; init; } = GlobalState.Tool?.OutputFolder ?? "";

        public PatchPriorityMode PriorityMode { get; init; } = PatchPriorityMode.High;

        // COBJ recipes -> generated ESP (Phase B). Name matches KeyFactory.UserPluginName so
        // tool-internal recipe keys already point at the right master.
        public bool GenerateCobj { get; init; } = true;
        public string EspFileName { get; init; } = "SkyrimCraftingTool.esp";
        public bool EslWhenPossible { get; init; } = true;

        // Global   -> one EspFileName for all recipes.
        // PerSourcePlugin -> "SkyrimCraftingTool - <SourcePlugin>.esp" per plugin (grouped by
        //                    CobjPatchEntry.SourcePlugin).
        public PatchCobjSplitMode CobjSplitMode { get; init; } = PatchCobjSplitMode.Global;

        public string EspNameFor(string sourcePlugin) =>
            CobjSplitMode == PatchCobjSplitMode.Global || string.IsNullOrEmpty(sourcePlugin)
                ? EspFileName
                : $"SkyrimCraftingTool - {Path.GetFileNameWithoutExtension(sourcePlugin)}.esp";

        // When true, build + validate the rules and fill the report, but write nothing.
        public bool DryRun { get; init; }

        public string PriorityFolderName =>
            PriorityMode == PatchPriorityMode.High ? "zzz_SkyrimCraftingTool" : "SkyrimCraftingTool";

        public string CategoryDir(string category) =>
            Path.Combine(OutputRoot, "SKSE", "Plugins", "SkyPatcher", category, PriorityFolderName);
    }
}
