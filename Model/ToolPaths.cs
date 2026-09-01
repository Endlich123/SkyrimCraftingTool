using System.IO;

namespace SkyrimCraftingTool.Model;

public class ToolPaths
{
    public string ModFolder { get; }
    public string InputFolder { get; }
    public string OutputFolder { get; }

    // Root of the generated SkyPatcher tree. The per-category subfolders (armor/, weapon/, ...) and
    // the zzz_SkyrimCraftingTool priority folder inside them are created on demand by
    // Services.PatchGen.PatchGeneratorService — not here.
    public string SkyPatcherFolder { get; }

    public ToolPaths()
    {
        ModFolder = AppContext.BaseDirectory;

        InputFolder = Path.Combine(ModFolder, "Input");
        OutputFolder = Path.Combine(ModFolder, "Output");
        SkyPatcherFolder = Path.Combine(OutputFolder, "SKSE", "Plugins", "SkyPatcher");

        Directory.CreateDirectory(InputFolder);
        Directory.CreateDirectory(OutputFolder);
    }
}
