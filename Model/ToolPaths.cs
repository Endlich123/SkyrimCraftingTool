using System.IO;

namespace SkyrimCraftingTool.Model;

public class ToolPaths
{
    public string ModFolder { get; }
    public string InputFolder { get; }
    public string OutputFolder { get; }

    // The SkyPatcher tree is not here: PatchGenOptions builds it under the output folder chosen in
    // Settings, which need not be OutputFolder.

    public ToolPaths()
    {
        ModFolder = AppContext.BaseDirectory;

        InputFolder = Path.Combine(ModFolder, "Input");
        OutputFolder = Path.Combine(ModFolder, "Output");

        Directory.CreateDirectory(InputFolder);
        Directory.CreateDirectory(OutputFolder);
    }
}
