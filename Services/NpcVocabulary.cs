using System;
using System.Collections.Generic;
using System.Linq;

// Domain vocabulary, not view-model state: these tables describe what SkyPatcher and the record
// layout call things, and the scan needs them just as much as the screen does.
namespace SkyrimCraftingTool.Services
{
    // The names SkyPatcher uses for NPC flags, skills and template flags, and the bit values behind
    // them (Prio 8, docs/NPC-Plan.md).
    //
    // Everything here is spelled the way the patch string spells it, so the rule builder never has
    // to translate between "what the screen says" and "what goes in the INI". The bit values come
    // from the record layout; the names come from docs/NPC_Patcher.txt.

    // ACBS flags. setFlags/removeFlags take these names.
    [Flags]
    public enum NpcFlag : uint
    {
        None = 0,
        Female = 0x0000_0001,
        Essential = 0x0000_0002,
        IsCharGenFacePreset = 0x0000_0004,
        Respawn = 0x0000_0008,
        AutoCalcStats = 0x0000_0010,
        Unique = 0x0000_0020,
        DoesntAffectStealthMeter = 0x0000_0040,
        PcLevelMult = 0x0000_0080,
        UseTemplate = 0x0000_0100,
        Protected = 0x0000_0800,
        Summonable = 0x0000_4000,
        DoesNotBleed = 0x0001_0000,
        BleedoutOverride = 0x0004_0000,
        OppositeGenderAnims = 0x0008_0000,
        SimpleActor = 0x0010_0000,
        LoopedScript = 0x0020_0000,
        LoopedAudio = 0x1000_0000,
        IsGhost = 0x2000_0000,
        Invulnerable = 0x8000_0000,
    }

    // setTemplateFlags/removeTemplateFlags, and the reason a stat edit can land on a field the game
    // never reads (NPC-Plan.md §4).
    [Flags]
    public enum NpcTemplateFlag : uint
    {
        None = 0,
        Traits = 0x0001,
        Stats = 0x0002,
        Factions = 0x0004,
        SpellList = 0x0008,
        AIData = 0x0010,
        AIPackages = 0x0020,
        ModelAnimation = 0x0040,
        BaseData = 0x0080,
        Inventory = 0x0100,
        Script = 0x0200,
        DefPackList = 0x0400,
        AttackData = 0x0800,
        Keywords = 0x1000,
    }

    public static class NpcFlagNames
    {
        // Spelled out rather than derived from the enum names, because the two do not agree:
        // SkyPatcher writes "usestemplate" where the record layout says UseTemplate, and
        // "doesntbleed" where it says DoesNotBleed. Deriving the name lowercased the enum and
        // produced "usetemplate" - a token SkyPatcher does not know, in a patch string that would
        // have looked perfectly fine. The list in docs/NPC_Patcher.txt is the authority here.
        private static readonly (NpcFlag Flag, string Name)[] Known =
        {
            (NpcFlag.Female, "female"),
            (NpcFlag.Essential, "essential"),
            (NpcFlag.IsCharGenFacePreset, "ischargenfacepreset"),
            (NpcFlag.Respawn, "respawn"),
            (NpcFlag.AutoCalcStats, "autocalcstats"),
            (NpcFlag.Unique, "unique"),
            (NpcFlag.DoesntAffectStealthMeter, "doesntaffectstealthmeter"),
            (NpcFlag.PcLevelMult, "pclevelmult"),
            (NpcFlag.UseTemplate, "usestemplate"),
            (NpcFlag.Protected, "protected"),
            (NpcFlag.Summonable, "summonable"),
            (NpcFlag.DoesNotBleed, "doesntbleed"),
            (NpcFlag.BleedoutOverride, "bleedoutoverride"),
            (NpcFlag.OppositeGenderAnims, "oppositegenderanims"),
            (NpcFlag.SimpleActor, "simpleactor"),
            (NpcFlag.LoopedScript, "loopedscript"),
            (NpcFlag.LoopedAudio, "loopedaudio"),
            (NpcFlag.IsGhost, "isghost"),
            (NpcFlag.Invulnerable, "invulnerable"),
        };

        // SkyPatcher documents three more flags - calcforalltemplates, norumors and noactivation -
        // whose bit positions this tool has no way to confirm. Their place in the documented list
        // suggests where they sit, but a guessed bit would set the WRONG flag on an NPC, and that
        // fails silently. They are left out until someone can confirm them, and Describe() below
        // reports any bit it cannot name instead of dropping it, so an NPC carrying one says so.

        public static IReadOnlyList<string> All => Known.Select(k => k.Name).ToList();

        // Every bit this table cannot name is reported as "unknown(0x…)" rather than dropped. A
        // silently swallowed bit would make an NPC look plainer than it is, and the three
        // unconfirmed flags above are exactly the ones that would disappear.
        public static List<string> Describe(uint bits)
        {
            var named = Known.Where(k => (bits & (uint)k.Flag) != 0).ToList();

            uint accounted = named.Aggregate(0u, (acc, k) => acc | (uint)k.Flag);
            uint leftover = bits & ~accounted;

            var result = named.Select(k => k.Name).ToList();
            if (leftover != 0) result.Add($"unknown(0x{leftover:X8})");
            return result;
        }

        public static uint Parse(IEnumerable<string> names)
        {
            uint bits = 0;
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                var hit = Known.FirstOrDefault(k =>
                    string.Equals(k.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
                bits |= (uint)hit.Flag;
            }
            return bits;
        }

        // Strict single-token lookup, for callers that must NOT read an unknown name as "no flags".
        // Parse above ORs in NpcFlag.None for anything it does not recognise, which is right when the
        // caller is collecting bits from its own checkbox list and wrong when the token came out of
        // the database: a group filtering on a misspelled flag would then filter on nothing at all
        // and quietly reach every NPC (see NpcGroupResolver).
        public static bool TryParse(string? name, out NpcFlag flag)
        {
            flag = NpcFlag.None;
            if (string.IsNullOrWhiteSpace(name)) return false;

            foreach (var known in Known)
            {
                if (!string.Equals(known.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                flag = known.Flag;
                return true;
            }
            return false;
        }

    }

    public static class NpcTemplateFlagNames
    {
        private static readonly (NpcTemplateFlag Flag, string Name)[] Known =
        {
            (NpcTemplateFlag.Traits, "traits"),
            (NpcTemplateFlag.Stats, "stats"),
            (NpcTemplateFlag.Factions, "factions"),
            (NpcTemplateFlag.SpellList, "spells"),
            (NpcTemplateFlag.AIData, "aidata"),
            (NpcTemplateFlag.AIPackages, "aipackages"),
            (NpcTemplateFlag.ModelAnimation, "unused"),
            (NpcTemplateFlag.BaseData, "basedata"),
            (NpcTemplateFlag.Inventory, "inventory"),
            (NpcTemplateFlag.Script, "script"),
            (NpcTemplateFlag.DefPackList, "aidefpacklist"),
            (NpcTemplateFlag.AttackData, "attackdata"),
            (NpcTemplateFlag.Keywords, "keywords"),
        };

        public static IReadOnlyList<string> All => Known.Select(k => k.Name).ToList();

        public static List<string> Describe(uint bits) =>
            Known.Where(k => (bits & (uint)k.Flag) != 0).Select(k => k.Name).ToList();

        public static uint Parse(IEnumerable<string> names)
        {
            uint bits = 0;
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                var hit = Known.FirstOrDefault(k =>
                    string.Equals(k.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
                bits |= (uint)hit.Flag;
            }
            return bits;
        }
    }

    // The 18 skills changeSkills knows, in the order the screen shows them.
    public static class NpcSkillNames
    {
        public static readonly IReadOnlyList<string> All = new[]
        {
            "onehanded", "twohanded", "marksman", "block", "smithing",
            "heavyarmor", "lightarmor", "pickpocket", "lockpicking", "sneak",
            "alchemy", "speechcraft", "alteration", "conjuration", "destruction",
            "illusion", "restoration", "enchanting",
        };

        // What a reader expects to see, against the token the patch string uses: "marksman" is
        // Archery in the game, and "speechcraft" is Speech.
        private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["onehanded"] = "One-Handed",
            ["twohanded"] = "Two-Handed",
            ["marksman"] = "Archery",
            ["block"] = "Block",
            ["smithing"] = "Smithing",
            ["heavyarmor"] = "Heavy Armor",
            ["lightarmor"] = "Light Armor",
            ["pickpocket"] = "Pickpocket",
            ["lockpicking"] = "Lockpicking",
            ["sneak"] = "Sneak",
            ["alchemy"] = "Alchemy",
            ["speechcraft"] = "Speech",
            ["alteration"] = "Alteration",
            ["conjuration"] = "Conjuration",
            ["destruction"] = "Destruction",
            ["illusion"] = "Illusion",
            ["restoration"] = "Restoration",
            ["enchanting"] = "Enchanting",
        };

        public static string Label(string skill) =>
            skill != null && Labels.TryGetValue(skill, out var label) ? label : skill ?? "";

        // The same eighteen, ordered by what they actually do TO AN NPC rather than by the order the
        // skill menu shows them in (G7, docs/NPC-Gruppen-Plan.md section 10.6).
        //
        // The vanilla engine reads a weapon skill for damage, an armour skill for the armour rating, the
        // magic schools for spell effect, Sneak for detection and Speech for a merchant's prices. It
        // reads none of the last five on an NPC: nobody tempers, brews, enchants, picks a lock or lifts
        // a purse but the player. The measured values agree - across the whole load order Pickpocket
        // tops out at 46 and Lockpicking at 39, while One-Handed reaches 100.
        //
        // They are all still offered, because a combat overhaul may read what the base game ignores.
        // They are just not what a picker should put first.
        public static readonly IReadOnlyList<string> ByEffect = new[]
        {
            "onehanded", "twohanded", "marksman", "block", "heavyarmor", "lightarmor",
            "destruction", "restoration", "conjuration", "alteration", "illusion",
            "sneak", "speechcraft",
            "smithing", "alchemy", "enchanting", "lockpicking", "pickpocket",
        };

        // Record value -> patch token. The record layout names two of them the way the game does
        // (Archery, Speech) where the patch string uses the older internal names, so lowercasing
        // alone would produce "archery" - a token changeSkills does not know.
        private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Archery"] = "marksman",
            ["Speech"] = "speechcraft",
        };

        public static string Token(object recordSkill)
        {
            var name = recordSkill?.ToString();
            if (string.IsNullOrWhiteSpace(name)) return "";
            return Renamed.TryGetValue(name, out var token) ? token : name.ToLowerInvariant();
        }
    }

    // The AI-data tokens, exactly as setAggression/setConfidence/... take them - and the mapping
    // from what the record actually stores.
    //
    // The two do not simply lowercase into each other, which is the reason this exists:
    //   * the record calls the morality field Responsibility, and its values are the ones
    //     setMorality takes - ViolenceAgainstEnemies against the token "violenceagainstenemy";
    //   * Assistance.HelpsFriendsAndAllies is "helpsfriends";
    //   * "calmed" exists as a token but not as a stored value - it is a runtime state, so a scan
    //     never produces it, though a patch may set it.
    public static class NpcAiTokens
    {
        // Record value -> patch token. Anything not listed lowercases cleanly and needs no entry;
        // the two that do not are named here so the difference is visible rather than inferred.
        private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ViolenceAgainstEnemies"] = "violenceagainstenemy",
            ["HelpsFriendsAndAllies"] = "helpsfriends",
        };

        // Turns a record value into the token the patch string uses. An unknown value is lowercased
        // rather than dropped: it is still what the record says, and losing it would be worse than
        // showing something the patcher may not know.
        public static string Token(object recordValue)
        {
            var name = recordValue?.ToString();
            if (string.IsNullOrWhiteSpace(name)) return "";
            return Renamed.TryGetValue(name, out var token) ? token : name.ToLowerInvariant();
        }

        public static readonly IReadOnlyList<string> Aggression =
            new[] { "calmed", "unaggressive", "aggressive", "veryaggressive", "frenzied" };

        public static readonly IReadOnlyList<string> Confidence =
            new[] { "cowardly", "cautious", "average", "brave", "foolhardy" };

        public static readonly IReadOnlyList<string> Assistance =
            new[] { "helpsnobody", "helpsallies", "helpsfriends" };

        public static readonly IReadOnlyList<string> Morality =
            new[] { "anycrime", "violenceagainstenemy", "propertycrimeonly", "nocrime" };

        public static readonly IReadOnlyList<string> Mood =
            new[] { "neutral", "angry", "fear", "happy", "sad", "surprised", "puzzled", "disgusted" };
    }

    // Key -> readable name, for everything an NPC points at. The view models take this rather than a
    // pile of dictionaries so they can be built in a test without a database behind them.
    public interface INpcLabels
    {
        // The inputs of the engine.s own stat calculation. Null where the scan has not supplied
        // them - an item.db from before they were read still opens, it just cannot predict.
        (float Health, float Magicka, float Stamina)? RaceStats(string key);
        (int Health, int Magicka, int Stamina)? ClassStatWeights(string key);

        string Faction(string key);
        string Class(string key);
        string Race(string key);
        string Outfit(string key);
        string VoiceType(string key);
        string CombatStyle(string key);
        string LeveledItem(string key);

        // A levelled CHARACTER list, for the NPCs drawn from one (G8). Not the same catalogue as
        // LeveledItem above: that one names item lists.
        string LeveledNpc(string key);
        string Armor(string key);
        string Npc(string key);
        string Spell(string key);
        string Shout(string key);
        string LeveledSpell(string key);
        string Perk(string key);
        string Weapon(string key);
        string Item(string key);

        // The WHOLE perk catalogue, for a picker. Perk(key) answers "what is this one", and that is not
        // enough for the group editor: a group adds perks its members do not carry yet, so the list it
        // offers cannot be built out of the members.
        IReadOnlyList<Model.FormIDRecord> PerkChoices();
    }

    public sealed class NpcLabels : INpcLabels
    {
        public static readonly NpcLabels Empty = new();

        public Dictionary<string, string> Factions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Classes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Races { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Outfits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> VoiceTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CombatStyles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LeveledItems { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LeveledNpcs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Armors { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Npcs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Spells { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Shouts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LeveledSpells { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Perks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Weapons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        // The inputs of the engine.s own stat calculation, for the 67,5 % of NPCs whose stored
        // Health/Magicka/Stamina the game ignores. Names alone cannot answer "what will this NPC
        // actually have" - see Services/AutoCalcStats.cs, which consumes these.
        public Dictionary<string, (float Health, float Magicka, float Stamina)> RaceBase { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, (int Health, int Magicka, int Stamina)> ClassWeights { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);

        public (float Health, float Magicka, float Stamina)? RaceStats(string key)
            => key != null && RaceBase.TryGetValue(key, out var v) ? v : null;

        public (int Health, int Magicka, int Stamina)? ClassStatWeights(string key)
            => key != null && ClassWeights.TryGetValue(key, out var v) ? v : null;

        public string Faction(string key) => Look(Factions, key);
        public string Class(string key) => Look(Classes, key);
        public string Race(string key) => Look(Races, key);
        public string Outfit(string key) => Look(Outfits, key);
        public string VoiceType(string key) => Look(VoiceTypes, key);
        public string CombatStyle(string key) => Look(CombatStyles, key);
        public string LeveledItem(string key) => Look(LeveledItems, key);
        public string LeveledNpc(string key) => Look(LeveledNpcs, key);
        public string Armor(string key) => Look(Armors, key);
        public string Npc(string key) => Look(Npcs, key);
        public string Spell(string key) => Look(Spells, key);
        public string Shout(string key) => Look(Shouts, key);
        public string LeveledSpell(string key) => Look(LeveledSpells, key);
        public string Perk(string key) => Look(Perks, key);
        public string Weapon(string key) => Look(Weapons, key);

        // Built once and held: a picker compares its SelectedItem by reference, so a getter that rebuilt
        // the list on every call would hand WPF a fresh object each time and the box would never show a
        // selection. Same reason the group editor caches its axis catalogues.
        private IReadOnlyList<Model.FormIDRecord>? _perkChoices;

        public IReadOnlyList<Model.FormIDRecord> PerkChoices() => _perkChoices ??= Perks
            .Select(kv => new Model.FormIDRecord { Key = kv.Key, Name = kv.Value })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An inventory line can point at anything - armour, a weapon, a potion, a leveled list. The
        // catalogues are searched in turn rather than one being guessed at from the key.
        public string Item(string key)
        {
            foreach (var map in new[] { Armors, Weapons, LeveledItems, Spells })
                if (map.TryGetValue(key ?? "", out var name)) return name;

            return Look(Armors, key);
        }

        // An unset link reads as "-", a set one whose target is missing keeps the key. Falling back
        // to "-" for both would hide a dead reference, and a dead reference is exactly the thing a
        // patch has to stop at.
        private static string Look(Dictionary<string, string> map, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "-";
            return map.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name) ? name : key;
        }
    }
}
