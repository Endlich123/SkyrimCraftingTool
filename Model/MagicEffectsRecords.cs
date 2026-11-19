using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    public class MagicEffectsRecords : IPickerGrouped
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string EditorID { get; set; } = "";
        public bool HasMagnitude { get; set; }
        public bool HasDuration { get; set; }
        public bool HasArea { get; set; }

        //Type
        public string CastType { get; set; } = "";
        public string TargetType { get; set; } = "";

        // In-game description with the game's placeholders (<mag>, <dur>, <area>) unfilled. Empty
        // until a scan after 2026-11-08 has written it, and for effects that carry none.
        public string Description { get; set; } = "";

        public string Label
        {
            get
            {
                var editorId = (EditorID ?? "").Trim();
                var name = (Name ?? "").Trim();

                if (editorId.Length > 0 && name.Length > 0) return $"{editorId} | {name}";
                if (editorId.Length > 0) return editorId;
                if (name.Length > 0) return name;

                return Key;
            }
        }

        // --- Picker row + filter chips (Enchantment tab) ---
        //
        // THE CHIPS ARE A FILTER, NOT A RULE. Measured on a 1300-mod list (2026-11-08): 1,784 of the
        // 1,807 effects that sit in any enchantment share its cast + target type - but 25 enchantments
        // in 18 plugins (Skyrim.esm and Dawnguard among them) do not: armor enchantments with
        // FireAndForget/Self potion effects, weapon enchantments with Self effects, staffs on a
        // neighbouring delivery. So the chip name carries the technical criterion, nothing is
        // preselected, and adding outside the chip stays possible.

        public const string GroupArmor = "Armor · Constant/Self";
        public const string GroupWeapon = "Weapon · FF/Touch";
        public const string GroupStaff = "Staff · Aimed/Target";
        public const string GroupSelf = "Self · FF/Conc.";
        public const string GroupOther = "Other";

        // The chip order: the three enchantment kinds first, the leftovers last.
        public static readonly IReadOnlyList<string> EnchantGroupOrder =
            new[] { GroupArmor, GroupWeapon, GroupStaff, GroupSelf, GroupOther };

        // Staff takes every delivery a staff enchantment uses (Aimed, TargetLocation, TargetActor,
        // each as FireAndForget or Concentration) - one chip, because staffs mix them among themselves.
        public static string EnchantGroupOf(string castType, string targetType)
        {
            bool constant = string.Equals(castType, "ConstantEffect", StringComparison.OrdinalIgnoreCase);
            bool fireAndForget = string.Equals(castType, "FireAndForget", StringComparison.OrdinalIgnoreCase);
            bool concentration = string.Equals(castType, "Concentration", StringComparison.OrdinalIgnoreCase);

            switch ((targetType ?? "").ToLowerInvariant())
            {
                case "self":
                    if (constant) return GroupArmor;
                    if (fireAndForget || concentration) return GroupSelf;
                    break;
                case "touch":
                    if (fireAndForget) return GroupWeapon;
                    break;
                case "aimed":
                case "targetlocation":
                case "targetactor":
                    if (fireAndForget || concentration) return GroupStaff;
                    break;
            }

            return GroupOther;
        }

        public string EnchantGroup => EnchantGroupOf(CastType, TargetType);

        public IEnumerable<string> PickerGroups => new[] { EnchantGroup };

        // Grey after the label: the exact values, so an effect outside its chip's usual pair is
        // visible in the row itself.
        public string Suffix
        {
            get
            {
                var cast = (CastType ?? "").Trim();
                var target = (TargetType ?? "").Trim();
                if (cast.Length == 0 && target.Length == 0) return "";
                return $"  · {cast}/{target}";
            }
        }

        // Same layout as FormIDRecord.ToolTipText: game name and cast/target on ONE line, the key,
        // then the description after a blank line. No game name: the EditorID stands in.
        public string ToolTipText
        {
            get
            {
                var pair = Suffix.TrimStart(' ', '·');
                var title = string.IsNullOrWhiteSpace(Name) ? EditorID : Name;
                var head = string.Join("  ·  ", new[] { title, pair }
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
                var lines = new List<string>();
                if (head.Length > 0) lines.Add(head);
                lines.Add(Key);
                if (!string.IsNullOrWhiteSpace(Description)) lines.Add("\n" + Description.Trim());
                return string.Join("\n", lines);
            }
        }
    }
}
