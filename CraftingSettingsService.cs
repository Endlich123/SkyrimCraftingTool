namespace SkyrimCraftingTool
{
    public class CraftingSettingsService
    {
        public Dictionary<string, SlotSettingsViewModel> SlotSettings { get; } = new();

        public event Action<string>? SettingsChanged;

        public SlotSettingsViewModel GetSlot(string category, string slot)
        {
            string key = $"{category}:{slot}";

            if (!SlotSettings.TryGetValue(key, out var vm))
            {
                vm = new SlotSettingsViewModel(category, slot);
                vm.PropertyChanged += (_, __) => SettingsChanged?.Invoke(key);
                SlotSettings[key] = vm;
            }

            return vm;
        }
    }
}
