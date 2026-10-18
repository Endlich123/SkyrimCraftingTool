using System.Collections.Generic;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // What the Spells tab asks of the database.
    //
    // Grows one phase at a time rather than being written ahead of its implementation: S-P2 added
    // the read, S-P3 the effect editing, S-P5 the record's own fields.
    //
    // EVERY FIELD HERE HAS A SkyPatcher OPERATION behind it, per docs/Spell_Patcher.txt. There is no
    // UpdateSpellTargetType, no UpdateSpellRange and no UpdateSpellType, because the patcher has no
    // way to write them - an editor for a value that cannot reach the game is worse than none.
    public interface ISpellService
    {
        List<SpellRecord> GetAllSpells();

        // The scanned magic effects. Feeds the "add effect" picker and tells each effect row which
        // of magnitude / duration / area the record can actually hold.
        List<MagicEffectsRecords> GetAllMagicEffects();

        // Every perk the scan knows, for the half-cost perk picker - a perk is the only thing
        // halfCostPerk= accepts, so the field offers nothing else (user, 2026-10-18).
        List<PerkChoice> GetPerkChoices();

        // The record's own fields (S-P5).
        void UpdateSpellName(string key, string name);
        void UpdateSpellBaseCost(string key, int baseCost);
        void UpdateSpellCastType(string key, string castType);
        void UpdateSpellChargeTime(string key, float chargeTime);
        void UpdateSpellHalfCostPerk(string key, string perkKey);
        void UpdateSpellFlags(string key, int flags);
        void UpdateSpellKeywords(string key, string keywordsCsv);

        // What the scan found, for the "what would Reset go back to" question.
        SpellRecord GetOriginalSpell(string key);

        // Clears every edited FIELD. Not the effects - those have their own snapshot and their own
        // reset, so that one button cannot throw away the other's work.
        void ResetSpellEdits(string key);

        // Effects (S-P3). Replaced wholesale, in one transaction, after freezing the pristine rows.
        void SaveSpellEffects(string spellKey, List<SpellEffectRecord> effects);
        List<SpellEffectRecord> GetOriginalSpellEffects(string spellKey);
        void ResetSpellEffects(string spellKey);

        // Spells of the user's own (S-P6). They exist in no plugin - the generated ESP brings them
        // into the game - so they carry Original = 0 and the scan leaves them alone.
        SpellRecord CreateSpell(string editorId, string name);
        bool DeleteSpell(string key);

        // The fields a tool-created record writes DIRECTLY, with no shadow column: SkyPatcher has no
        // operation for any of them, so they are unreachable on a scanned record and perfectly
        // writable on one the ESP builds from scratch.
        void UpdateUserSpellField(string key, string column, object value);
    }
}
