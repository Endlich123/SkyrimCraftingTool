namespace SkyrimCraftingTool.ViewModel
{
    // A tab's view model, asked to set the stage before a tour step: select an example item, open a
    // sub-tab. The action names come from the tour's JSON ("Prepare"). False means "cannot" - nothing
    // scanned yet, typically - and the tour stops there with a word on what is missing.
    public interface ITutorialHost
    {
        bool PrepareTutorialStep(string action);
    }
}
