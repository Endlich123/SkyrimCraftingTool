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

        // ConditionType: HasPerk, GetIsSex, GetActorValue, GetLevel, GetStageDone
        public string ConditionType { get; set; } = "";

        // Target:
        // - HasPerk: Plugin|FormID of the perk
        // - GetIsSex: Male / Female
        // - GetActorValue: ActorValue enum name
        // - GetLevel: ""
        // - GetStageDone: Plugin|FormID of the quest
        public string Target { get; set; } = "";

        // Value:
        // - HasPerk: 1 or 0
        // - GetIsSex: 1
        // - GetActorValue: comparison value
        // - GetLevel: comparison value
        // - GetStageDone: stage number
        public string Value { get; set; } = "";

        // Extra: currently unused, but prepared for future condition types
        public string Extra { get; set; } = "";

        // RunOn: Subject, Target, Reference
        public string RunOn { get; set; } = "";
    }
}
