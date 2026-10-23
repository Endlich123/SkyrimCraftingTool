using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // An NPC group: a NAMED FILTER SET, which is the work unit of the NPC patcher
    // (G2, docs/NPC-Gruppen-Plan.md section 4). A single NPC is a group with no predicate and one
    // add-override, so there is one editor and one rule builder rather than two.
    //
    // THE ONE RULE BEHIND THIS SHAPE: membership is COMPUTED, never stored. A stored member list
    // goes stale on the next rescan - a new mod adds 17 bandits, and a saved change reaches NPCs
    // nobody ever looked at (section 8). What is stored is the predicate, the hand corrections, and
    // - separately, and only for the diff - a snapshot of what the user last confirmed.
    public sealed class NpcGroup
    {
        // 0 until the store has written the row: NpcGroup.Id is an INTEGER PRIMARY KEY, so SQLite
        // hands out the number, not this class.
        public long Id { get; set; }

        public string Name { get; set; } = "";

        // "auto" for a group the seeder made, "user" for one the user made. The seeder may re-seed
        // its own groups after a scan; it must never touch a user's.
        public string Origin { get; set; } = NpcGroupOrigin.User;

        // Which auto rule produced this group, kept so a re-seed can recognise its own work
        // (section 7). Empty for a user group.
        public string Seed { get; set; } = "";

        public int SortOrder { get; set; }
        public string Notes { get; set; } = "";
        public bool Active { get; set; } = true;

        public List<NpcGroupPredicate> Predicates { get; set; } = new();
        public List<NpcGroupMemberOverride> Overrides { get; set; } = new();
        public List<NpcGroupValue> Values { get; set; } = new();
        public List<NpcGroupList> Lists { get; set; } = new();
    }

    // One clause of the definition. Axis says what is being looked at, Mode how it combines - see
    // NpcGroupResolver for what "include" means per axis, which is NOT the same for all of them.
    public sealed class NpcGroupPredicate
    {
        public string Axis { get; set; } = "";
        public string Mode { get; set; } = NpcGroupMode.Include;
        public string Value { get; set; } = "";

        // Who wrote this line. Empty means a person did, and that is the default for everything the
        // filter editor and the seeder produce.
        //
        // WHY A CLAUSE NEEDS THIS AND THE SEEDER'S GROUPS ALREADY HAD IT (NpcGroup.Seed, section 7):
        // the auto-check writes a dozen exclusions in one press, and it has to be able to recognise
        // them again - to take them all back, and to replace its own previous answer instead of piling
        // a second one on top of it. Without the mark they are indistinguishable from hand-written
        // lines the moment the screen is left.
        //
        // NOT part of the primary key: a clause is identified by its terms, and who wrote it is an
        // attribute of the same line rather than a second line.
        public string Origin { get; set; } = "";
    }

    public static class NpcGroupPredicateOrigin
    {
        // Written by "exclude what other groups already cover" (NpcGroupDeduplicator).
        //
        // Deliberately NOT NpcGroupOrigin.Auto: a seeded group's own clauses are machine-written too,
        // and a button offering to strip the predicate off every pre-filled group would be a very
        // different thing from taking back one press.
        public const string AutoExclude = "autoexclude";
    }

    // A hand correction, held apart from the predicate so a re-seed cannot throw it away.
    //
    // The primary key is (GroupId, NpcKey) WITHOUT Mode, so one NPC cannot be both added and
    // removed in the same group - the contradiction is impossible rather than resolved later.
    public sealed class NpcGroupMemberOverride
    {
        public string NpcKey { get; set; } = "";
        public string Mode { get; set; } = NpcGroupOverrideMode.Add;
    }

    // What the group changes about a value. Low and High are strings because the four kinds carry
    // four different things: a number, the two ends of a span, a multiplier like 1.5, or the N of
    // calcHealth=N (section 5.2).
    public sealed class NpcGroupValue
    {
        public string Field { get; set; } = "";
        public string Kind { get; set; } = NpcGroupValueKind.Direct;
        public string Low { get; set; } = "";
        public string High { get; set; } = "";
    }

    // Perks, spells, items, factions, keywords the group adds or removes.
    public sealed class NpcGroupList
    {
        public string Kind { get; set; } = "";
        public string TargetKey { get; set; } = "";
        public string Mode { get; set; } = NpcGroupListMode.Add;

        // Rank for a faction, count for an item. Null where the operation takes none - perksToAdd
        // does not carry a rank, objectsToRemove does not carry a count.
        public string? Extra { get; set; }
    }

    public static class NpcGroupOrigin
    {
        public const string Auto = "auto";
        public const string User = "user";
    }

    public static class NpcGroupOverrideMode
    {
        public const string Add = "add";
        public const string Remove = "remove";
    }

    public static class NpcGroupValueKind
    {
        public const string Direct = "direct";
        public const string Span = "span";
        public const string Mult = "mult";
        public const string Calc = "calc";

        // healthBonus= / magickaBonus= / staminaBonus=, a flat amount added on top. Its own operation,
        // not part of changeStats - SkyValor writes "staminaBonus=100:changeStats=health=250~650,
        // calcStamina=10", and the plan names the pairing in section 5.2 ("calc -> calcHealth=10 +
        // healthBonus="). Measured in the reference mod: 64 of its 101 NPC rules carry a bonus.
        public const string Bonus = "bonus";
    }

    public static class NpcGroupListMode
    {
        public const string Add = "add";
        public const string Remove = "remove";
    }

    // The seven axes a predicate can look at. Exactly the seven SkyPatcher has a filter for
    // (section 5) - no more, because an axis the rule builder cannot express would resolve to a
    // member count in the interface that the patch then fails to reach.
    //
    // Keyword is deliberately NOT here. A keyword is something a group WRITES (NpcGroupList), and
    // the one place a keyword is read back is the tagger pattern in section 5.3, which the generator
    // builds for itself.
    public static class NpcGroupAxis
    {
        public const string Class = "class";
        public const string Race = "race";
        public const string Faction = "faction";
        public const string EditorId = "editorid";
        public const string Mod = "mod";
        public const string Flag = "flag";
        public const string Gender = "gender";

        public static readonly System.Collections.Generic.IReadOnlyList<string> All = new[]
        {
            Class, Race, Faction, EditorId, Mod, Flag, Gender,
        };
    }

    // include / includeOr / exclude - SkyPatcher's own distinction between filterByX and
    // filterByXOr. What "include" does with two values depends on the axis: see
    // NpcGroupResolver.MultiValued.
    public static class NpcGroupMode
    {
        public const string Include = "include";
        public const string IncludeOr = "includeOr";
        public const string Exclude = "exclude";
    }
}
