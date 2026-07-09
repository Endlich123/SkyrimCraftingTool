using System.IO;

namespace SkyrimCraftingTool.Model;

public class ToolPaths
{
    public string ModFolder { get; }
    public string InputFolder { get; }
    public string OutputFolder { get; }
    public string SkyPatcherFolder { get; }
    public string SkyPatcherWeaponsFolder { get; }
    public string SkyPatcherConstructibleObjectFolder { get; }
    public string SkyPatcherContainerFolder { get; }
    public string SkyPatcherEnchantmentFolder { get; }

    public ToolPaths()
    {
        ModFolder = AppContext.BaseDirectory;

        InputFolder = Path.Combine(ModFolder, "Input");
        OutputFolder = Path.Combine(ModFolder, "Output");

        SkyPatcherFolder = Path.Combine(
            OutputFolder,
            "SKSE",
            "Plugins",
            "SkyPatcher"
        );

        SkyPatcherWeaponsFolder = Path.Combine(
            SkyPatcherFolder,
            "weapons"
        );

        SkyPatcherConstructibleObjectFolder = Path.Combine(
            SkyPatcherFolder,
            "constructibleObject"
        );

        SkyPatcherContainerFolder = Path.Combine(
            SkyPatcherFolder,
            "container"
        );

        SkyPatcherEnchantmentFolder = Path.Combine(
            SkyPatcherFolder,
            "enchantment"
        );

        // Ordner direkt anlegen
        Directory.CreateDirectory(InputFolder);
        Directory.CreateDirectory(SkyPatcherFolder);
        Directory.CreateDirectory(SkyPatcherWeaponsFolder);
        Directory.CreateDirectory(SkyPatcherConstructibleObjectFolder);
        Directory.CreateDirectory(SkyPatcherContainerFolder);
        Directory.CreateDirectory(SkyPatcherEnchantmentFolder);
    }
}
