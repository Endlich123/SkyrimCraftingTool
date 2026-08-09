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
        public string COBJKey { get; set; } = "";  // Plugin|FormID des COBJ

        // ConditionType: HasPerk, GetIsSex, GetActorValue, GetLevel, GetStageDone
        public string ConditionType { get; set; } = "";

        // Target:
        // - HasPerk: Plugin|FormID des Perks
        // - GetIsSex: Male / Female
        // - GetActorValue: ActorValue enum name
        // - GetLevel: ""
        // - GetStageDone: Plugin|FormID der Quest
        public string Target { get; set; } = "";

        // Value:
        // - HasPerk: 1 oder 0
        // - GetIsSex: 1
        // - GetActorValue: Vergleichswert
        // - GetLevel: Vergleichswert
        // - GetStageDone: Stage-Nummer
        public string Value { get; set; } = "";

        // Extra: aktuell ungenutzt, aber vorbereitet für zukünftige Condition‑Typen
        public string Extra { get; set; } = "";

        // RunOn: Subject, Target, Reference
        public string RunOn { get; set; } = "";
    }
}
