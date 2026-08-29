using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // One Preset, persisted as a single JSON file (Output/Presets/<SanitizedPresetName>.json).
    // Holds every Armor-Slot- and Weapon-Type-specific config that makes up the preset.
    public class PresetFile
    {
        // Bumped whenever the on-disk shape changes in a way a different build must notice.
        // A file written before versioning has no field -> deserializes to 0 -> PresetFileStore
        // normalizes that to 1. No migration logic exists yet; this is groundwork.
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string PresetName { get; set; } = "";

        // NodeKey = ArmorSlotMask bit index as string ("0".."31"), matches SlotVM.Bit.
        public List<PresetSlotConfig> ArmorSlots { get; set; } = new();

        // NodeKey = the WeapType keyword's own key ("Plugin|FormID").
        public List<PresetSlotConfig> WeaponTypes { get; set; } = new();

        // WPF's ComboBoxItem (like TreeViewItem) falls back to ToString() for its UI-Automation Name
        // when no other name source is available (e.g. the ConfigPresets picker in the item detail
        // view uses DisplayMemberPath for the visual text, which doesn't feed the automation name).
        public override string ToString() => PresetName;
    }

    // One Armor-Slot- or Weapon-Type-node's configuration within a Preset. ArmorRating is only
    // meaningful for ArmorSlots entries; Damage/Speed/Reach/Stagger only for WeaponTypes entries.
    public class PresetSlotConfig
    {
        public string NodeKey { get; set; } = "";

        public FieldValue<double> Weight { get; set; } = new();
        public FieldValue<int> Value { get; set; } = new();
        public FieldValue<double> ArmorRating { get; set; } = new();
        public FieldValue<int> Damage { get; set; } = new();
        public FieldValue<double> Speed { get; set; } = new();
        public FieldValue<double> Reach { get; set; } = new();
        public FieldValue<double> Stagger { get; set; } = new();

        // Plugin|FormID list.
        public FieldValue<List<string>> Keywords { get; set; } = new() { Value = new() };

        public RecipeConfig CraftRecipe { get; set; } = new();
        public RecipeConfig TemperRecipe { get; set; } = new();

        // Same serialized format as Armor/Weapons.ContainerString (built/parsed via
        // ContainerSelectionVM.BuildString/LoadFromString, see ContainerStringBuilder/Parser).
        public FieldValue<string> Container { get; set; } = new() { Value = "{}" };
    }

    // Wraps a preset field together with its "Include"-checkbox state. Apply only touches
    // fields where Enabled == true; everything else on the target item is left untouched.
    public class FieldValue<T>
    {
        public bool Enabled { get; set; }
        public T Value { get; set; }
    }

    public class RecipeConfig
    {
        public FieldValue<string> WorkbenchKey { get; set; } = new() { Value = "" };
        public FieldValue<List<IngredientEntry>> Ingredients { get; set; } = new() { Value = new() };
        public FieldValue<List<ConditionEntry>> Conditions { get; set; } = new() { Value = new() };
    }

    public class IngredientEntry
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
    }

    public class ConditionEntry
    {
        public string ConditionType { get; set; } = "";
        public string Target { get; set; } = "";
        public string Value { get; set; } = "";
        public string RunOn { get; set; } = "";
    }
}
