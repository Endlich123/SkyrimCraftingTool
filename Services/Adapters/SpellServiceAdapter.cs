using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.Adapters
{
    // Puts ItemDBHandler behind ISpellService, as EnchantmentServiceAdapter does for enchantments.
    public sealed class SpellServiceAdapter : ISpellService
    {
        private readonly ItemDBHandler _handler;

        public SpellServiceAdapter(ItemDBHandler handler) => _handler = handler;

        public List<SpellRecord> GetAllSpells() => _handler.GetAllSpells();

        public List<MagicEffectsRecords> GetAllMagicEffects()
            => _handler.SearchByType("MagicEffect").Cast<MagicEffectsRecords>().OrderBy(m => m.Name).ToList();

        public List<PerkChoice> GetPerkChoices() => ItemDBHandler.GetPerkChoices();

        public void UpdateSpellName(string key, string name) => ItemDBHandler.UpdateSpellName(key, name);
        public void UpdateSpellBaseCost(string key, int baseCost) => ItemDBHandler.UpdateSpellBaseCost(key, baseCost);
        public void UpdateSpellCastType(string key, string castType) => ItemDBHandler.UpdateSpellCastType(key, castType);
        public void UpdateSpellChargeTime(string key, float chargeTime) => ItemDBHandler.UpdateSpellChargeTime(key, chargeTime);
        public void UpdateSpellHalfCostPerk(string key, string perkKey) => ItemDBHandler.UpdateSpellHalfCostPerk(key, perkKey);
        public void UpdateSpellFlags(string key, int flags) => ItemDBHandler.UpdateSpellFlags(key, flags);
        public void UpdateSpellKeywords(string key, string csv) => ItemDBHandler.UpdateSpellKeywords(key, csv);

        public SpellRecord GetOriginalSpell(string key) => ItemDBHandler.GetOriginalSpell(key);
        public void ResetSpellEdits(string key) => ItemDBHandler.ResetSpellEdits(key);

        public void SaveSpellEffects(string spellKey, List<SpellEffectRecord> effects)
            => ItemDBHandler.SaveSpellEffects(spellKey, effects);

        public List<SpellEffectRecord> GetOriginalSpellEffects(string spellKey)
            => ItemDBHandler.GetOriginalSpellEffects(spellKey);

        public void ResetSpellEffects(string spellKey) => ItemDBHandler.ResetSpellEffects(spellKey);

        public SpellRecord CreateSpell(string editorId, string name) => ItemDBHandler.CreateSpell(editorId, name);
        public bool DeleteSpell(string key) => ItemDBHandler.DeleteSpell(key);

        public void UpdateUserSpellField(string key, string column, object value)
            => ItemDBHandler.UpdateUserSpellField(key, column, value);
    }
}
