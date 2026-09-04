using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    public class COBJConditionRecord
    {
        public int Id { get; set; }                // PK
        public string COBJKey { get; set; } = "";  // Plugin|FormID of the COBJ

        // ConditionType: the Mutagen condition function name.
        //
        // Five of them are editable in the UI (HasPerk, GetIsSex, GetActorValue, GetLevel,
        // GetStageDone). The rest are scanned and round-tripped, but shown read-only - they exist so
        // an override doesn't silently drop them. See ConditionCatalog for the split; anything not
        // listed there is stored under UnsupportedPrefix + the function name and can only be
        // counted, not rebuilt.
        public string ConditionType { get; set; } = "";

        // Target:
        // - HasPerk: Plugin|FormID of the perk
        // - GetIsSex: Male / Female
        // - GetActorValue: ActorValue enum name
        // - GetLevel: ""
        // - GetStageDone: Plugin|FormID of the quest
        // - GetItemCount / GetGlobalValue / HasSpell / HasKeyword / GetQuestCompleted /
        //   GetInCurrentLoc / GetVMQuestVariable: Plugin|FormID of the single referenced record
        // - EPTemperingItemIsEnchanted: "" (the function takes no parameter)
        public string Target { get; set; } = "";

        // Value: the comparison value (or the stage number for GetStageDone).
        public string Value { get; set; } = "";

        // Extra: GetVMQuestVariable stores its script variable name here. Unused by every other type.
        public string Extra { get; set; } = "";

        // RunOn: Subject, Target, Reference, CombatTarget, LinkedReference, QuestAlias,
        // PackageData, EventData. The editable types only ever produce Subject/Target.
        public string RunOn { get; set; } = "";

        // CompareOperator: EqualTo, NotEqualTo, GreaterThan, GreaterThanOrEqualTo, LessThan,
        // LessThanOrEqualTo. Empty on rows written before this column existed - the ESP builder then
        // falls back to the old per-type guess, which measured exact against the vanilla masters.
        public string CompareOperator { get; set; } = "";

        // Flags: comma-separated Condition.Flag names, i.e. "OR" and/or "SwapSubjectAndTarget".
        // OR matters a great deal: rebuilding an OR-chained pair as AND turns "either perk" into
        // "both perks", which removes the recipe from the crafting menu.
        public string Flags { get; set; } = "";
    }
}
