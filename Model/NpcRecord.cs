using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // One NPC row as the scan reads it and the editor shows it (Prio 8 / N-P1, docs/NPC-Plan.md).
    // Mirrors the Npc table column for column - see ItemDBHandler.Schema.cs for why a field does or
    // does not have a shadow twin there.
    public class NpcRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";
        public string ShortName { get; set; } = "";

        // The two tree levels below the plugin. Every NPC has exactly one class - measured on the
        // real load order, none is without - which is what lets Plugin -> Class -> NPC hold every
        // NPC exactly once (NPC-Plan.md §8).
        public string ClassKey { get; set; } = "";
        public string RaceKey { get; set; } = "";

        // Either a fixed level or a multiple of the player's, never both. UsesPcLevelMult says which
        // of the two below to read; CalcMin/CalcMax only bound the multiplier case.
        public bool UsesPcLevelMult { get; set; }
        public int Level { get; set; }
        public float LevelMult { get; set; }
        public int CalcMinLevel { get; set; }
        public int CalcMaxLevel { get; set; }

        public int Health { get; set; }
        public int Magicka { get; set; }
        public int Stamina { get; set; }

        // The ACBS offsets, kept apart from the three values above because it is not established
        // which of the two SkyPatcher's changeStats actually writes (NPC-Plan.md §7, Frage 4).
        public int HealthOffset { get; set; }
        public int MagickaOffset { get; set; }
        public int StaminaOffset { get; set; }

        // Raw bits, edited through setFlags/removeFlags and setTemplateFlags/removeTemplateFlags.
        public uint Flags { get; set; }
        public uint TemplateFlags { get; set; }

        // What this NPC inherits from, and the reason the editor has to say so out loud: 61,4 % of
        // NPCs take their stats from here, and for those a stat edit writes a field the game never
        // reads (NPC-Plan.md §4).
        public string TemplateKey { get; set; } = "";

        public string VoiceKey { get; set; } = "";
        public string DefaultOutfitKey { get; set; } = "";
        public string SleepOutfitKey { get; set; } = "";
        public string DeathItemKey { get; set; } = "";
        public string SkinKey { get; set; } = "";

        // Scanned and shown, but not editable: SkyPatcher has no operation for either, and an ESP
        // override of an NPC record freezes the FaceGen data 65 % of them carry (NPC-Plan.md §1).
        public string CombatStyleKey { get; set; } = "";
        public string CrimeFactionKey { get; set; } = "";

        public float Weight { get; set; }
        public float Height { get; set; }

        // Stored as the token the matching SkyPatcher operation takes ("aggressive", "brave", ...),
        // so nothing has to translate later.
        public string Aggression { get; set; } = "";
        public string Confidence { get; set; } = "";
        public string Assistance { get; set; } = "";
        public string Morality { get; set; } = "";
        public string Mood { get; set; } = "";

        // The one AI value with no operation of its own. Morality above comes from the record field
        // the layout calls Responsibility - there is no separate numeric one.
        public int EnergyLevel { get; set; }

        public List<string> Keywords { get; set; } = new();

        // What the scan read for the two list fields, against the effective lists above. Held
        // separately for the same reason as NpcScannedValues: the editor marks added and removed
        // entries by comparing, and the rule builder diffs the same two lists.
        public List<string> ScannedKeywords { get; set; } = new();

        // Child rows, loaded with the record.
        public List<NpcFactionRecord> Factions { get; set; } = new();
        public List<NpcFactionRecord> ScannedFactions { get; set; } = new();

        // Spells, shouts and leveled spells in one list, the way the record keeps them. Kind says
        // which of SkyPatcher.s three operations an entry belongs to.
        public List<NpcSpellRecord> Spells { get; set; } = new();
        public List<NpcSpellRecord> ScannedSpells { get; set; } = new();

        public List<NpcPerkRecord> Perks { get; set; } = new();
        public List<NpcPerkRecord> ScannedPerks { get; set; } = new();

        public List<NpcItemRecord> Items { get; set; } = new();
        public List<NpcItemRecord> ScannedItems { get; set; } = new();
        public List<NpcSkillRecord> Skills { get; set; } = new();

        public bool IsEdited { get; set; }

        // What the SCAN read, next to the effective values above. The editor needs both: a field is
        // marked as changed by comparing them, and a reset has to be able to say what it would go
        // back to without re-reading the plugin.
        //
        // Only the fields that are editable today - later phases extend this alongside the columns
        // they unlock.
        public NpcScannedValues Scanned { get; set; } = new();
    }

    public class NpcScannedValues
    {
        public bool UsesPcLevelMult { get; set; }
        public int Level { get; set; }
        public int CalcMinLevel { get; set; }
        public int CalcMaxLevel { get; set; }
        public int Health { get; set; }
        public int Magicka { get; set; }
        public int Stamina { get; set; }

        // N-P4: the links, flags and AI values.
        public string ClassKey { get; set; } = "";
        public string RaceKey { get; set; } = "";
        public string VoiceKey { get; set; } = "";
        public string DefaultOutfitKey { get; set; } = "";
        public string SleepOutfitKey { get; set; } = "";
        public string DeathItemKey { get; set; } = "";
        public string SkinKey { get; set; } = "";
        public uint Flags { get; set; }
        public uint TemplateFlags { get; set; }
        public float Weight { get; set; }
        public float Height { get; set; }
        public string Aggression { get; set; } = "";
        public string Confidence { get; set; } = "";
        public string Assistance { get; set; } = "";
        public string Morality { get; set; } = "";
        public string Mood { get; set; } = "";

        // N-P6: editable through an ESP override rather than a rule.
        public string CombatStyleKey { get; set; } = "";
        public string CrimeFactionKey { get; set; } = "";
    }

    // A faction membership is the faction PLUS a rank - factionsToAdd takes both. 857 of 14.832
    // memberships in the real load order carry a rank other than 0.
    public class NpcFactionRecord
    {
        public string FactionKey { get; set; } = "";
        public int Rank { get; set; }
    }

    // One entry in ActorEffect. Kind is "spell", "shout" or "levspell" - decided by the scan, because
    // the link itself carries only a FormKey and the three need three different operations. Empty
    // means the key resolved to none of the three, which is a dead reference.
    public class NpcSpellRecord
    {
        public string SpellKey { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    // A perk placement. The rank is scanned and shown; perksToAdd does not take one, so it is never
    // patched - and there is no perksToRemove at all.
    public class NpcPerkRecord
    {
        public string PerkKey { get; set; } = "";
        public int Rank { get; set; }
    }

    // One inventory line. objectsToAdd carries the count; objectsToRemove does not.
    public class NpcItemRecord
    {
        public string ItemKey { get; set; } = "";
        public int Count { get; set; }
    }

    // One of the 18 skills. Skill is SkyPatcher.s own spelling ("twohanded", "heavyarmor").
    public class NpcSkillRecord
    {
        public string Skill { get; set; } = "";
        public int Value { get; set; }

        // The record carries a base value and an offset; changeSkills only addresses one of them, so
        // the offset stays visible rather than being folded into the value.
        public int Offset { get; set; }

        // What the scan read, against Value above, which may be the user.s. Same reason as
        // NpcScannedValues: the change marker and the reset both need the original.
        public int ScannedValue { get; set; }
    }
}
