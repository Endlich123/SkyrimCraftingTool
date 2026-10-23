namespace SkyrimCraftingTool.ViewModel
{
    // A marker so the shell can tell the two tabs apart (docs/NPC-Plan.md §12).
    //
    // The Templates tab and the NPC tab show the SAME NpcMenuVM - a template is an NPC record, and
    // 1.073 of the 1.372 templates in the load order are records the NPC tab already holds. One view
    // model means one set of records, one selection and one editor: an edit made on either tab is
    // the edit the other one shows, with no second copy to fall out of step.
    //
    // CurrentView is resolved by type through a DataTemplate, though, so two tabs backed by the same
    // instance would resolve to the same view. This wrapper exists only to carry a second type; the
    // template hands its Npc straight on as the view's DataContext.
    public class TemplateTabVM : ViewModelBase
    {
        public TemplateTabVM(NpcMenuVM npc) => Npc = npc;

        public NpcMenuVM Npc { get; }
    }
}
