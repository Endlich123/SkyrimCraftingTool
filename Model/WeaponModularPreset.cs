using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // The weapon half of the modular presets (docs/WeaponPresets-Plan.md). Its own catalog in
    // Output/Presets/Modules/Weapons, apart from armor (decided 2026-10-23), so each can be shared
    // alone.
    //
    // WHY NOT THE ARMOR SHAPE: an armor piece is a share of its set's totals. Vanilla weapons are not
    // shares - a weapon type has a base, and the material adds to it per tier (Sword 7 Iron ... 14
    // Daedric, Greatsword 15 ... 24, both +1 a step). Value is the exception: material value times a
    // factor of the type (a greatsword is worth about twice a sword).
    //
    // Addons, rules, extras, recipes and the selection are the armor classes, reused as they are:
    // a rule's Types and an addon's OnlySlots name WEAPON TYPES here, and slots do not exist.
    public class WeaponCatalog
    {
        public List<WeaponTypeModule> Types { get; set; } = new();
        public List<WeaponMaterialModule> Materials { get; set; } = new();
        public List<KeywordAddon> Addons { get; set; } = new();
        public List<CombinationRule> Rules { get; set; } = new();
        public List<ExtraModule> Extras { get; set; } = new();
    }

    // Sword, Greatsword, Bow, Crossbow, Staff ... The type is RECOGNISED, never assigned: WeapType*
    // keywords follow the animation, and the item editor locks them. Recognition: one of Keywords on
    // the item, else the name or EditorID patterns. Several types can match - mods add their own
    // keyword NEXT TO the vanilla one (a halberd carries WeapTypeBattleaxe and ShWeapTypeHalberd) -
    // and the FIRST matching type in the list wins, so specific types go above general ones. The same
    // rule as for materials.
    //
    // EVERY VALUE IS OPTIONAL (decided 2026-10-23): null = the weapon keeps its own. A Staff type can
    // set value and weight and leave damage alone; anyone who wants staves to be forged gives it
    // recipe factors.
    public class WeaponTypeModule
    {
        public string Name { get; set; } = "";

        // Which material bonus applies: the key into WeaponMaterialModule.Classes ("OneHanded",
        // "TwoHanded", "Bow", "Crossbow", ...). A free name, so a mod type can get a class of its own.
        public string WeaponClass { get; set; } = "";

        public List<string> Keywords { get; set; } = new();
        public string NamePatterns { get; set; } = "";
        public string NameExclusions { get; set; } = "";
        public string EditorIdPatterns { get; set; } = "";

        // Damage = BaseDamage + the material's damage bonus for WeaponClass; Weight the same way.
        public double? BaseDamage { get; set; }
        public double? BaseWeight { get; set; }

        // Value = material BaseValue x ValueFactor.
        public double? ValueFactor { get; set; }

        // The type's own handling - the same for every material in vanilla. A material's class may
        // still set Speed (bows get slower with the material: Iron 1.0 ... Daedric 0.5).
        public double? Speed { get; set; }
        public double? Reach { get; set; }
        public double? Stagger { get; set; }

        // Critical damage = the computed damage x CritFactor, rounded down. Vanilla 0.5 (Iron Sword
        // 7 -> 3, Daedric Sword 14 -> 7). CritChance is written as it is (vanilla 1).
        public double? CritFactor { get; set; }
        public double? CritChance { get; set; }

        // One factor per position of the material's recipe list, as with slot definitions: an iron
        // dagger takes 1 ingot, a sword 2, a mace 3. Empty = the material's base amounts as they are.
        public List<double> RecipeFactors { get; set; } = new();

        // Rounding of the computed weight; 0 = 3 decimals. Damage is always a whole number.
        public double WeightStep { get; set; }
    }

    public class WeaponMaterialModule
    {
        public string Name { get; set; } = "";

        // WeapMaterial*, Plugin|FormID. Empty = no material keyword.
        public string MaterialKeyword { get; set; } = "";

        // Fallback recognition when the item carries no material keyword (see NameMatcher).
        public string NamePatterns { get; set; } = "";
        public string NameExclusions { get; set; } = "";

        // Bonus per weapon class ("OneHanded" -> +7 damage, +7 weight on Daedric). A class without
        // an entry adds nothing.
        public Dictionary<string, WeaponClassBonus> Classes { get; set; } = new();

        // Value of this material's reference weapon; a type multiplies it (WeaponTypeModule.ValueFactor).
        public int? BaseValue { get; set; }

        public MaterialRecipe Craft { get; set; } = new();
        public MaterialRecipe Temper { get; set; } = new();
        public MaterialRecipe Breakdown { get; set; } = new();

        // Exceptions for one weapon type of THIS material, as absolute values - only where vanilla
        // breaks the pattern (a Daedric warhammer is worth 4,000, the factor says 2,750).
        public List<WeaponTypeOverride> TypeOverrides { get; set; } = new();

        public WeaponClassBonus? BonusFor(string weaponClass)
            => Classes?.FirstOrDefault(kv => string.Equals(kv.Key, weaponClass, StringComparison.OrdinalIgnoreCase)).Value;

        public WeaponTypeOverride? OverrideFor(string weaponType)
            => TypeOverrides?.FirstOrDefault(o => string.Equals(o.WeaponType, weaponType, StringComparison.OrdinalIgnoreCase));
    }

    public class WeaponClassBonus
    {
        public double Damage { get; set; }
        public double Weight { get; set; }

        // Null = the weapon type's own speed.
        public double? Speed { get; set; }
    }

    // Null fields = the formula.
    public class WeaponTypeOverride
    {
        public string WeaponType { get; set; } = "";
        public int? Damage { get; set; }
        public double? Weight { get; set; }
        public int? Value { get; set; }
        public double? Speed { get; set; }

        // Vanilla is irregular here per material: Glass daggers, maces and warhammers have critical
        // damage 0, a Glass war axe 8 where half its damage is 6; enchanted Draugr war axes stagger 0.
        public double? Reach { get; set; }
        public double? Stagger { get; set; }
        public int? CritDamage { get; set; }

        // Every enchanted Iron Sword and Orcish War Axe has crit chance 0 in vanilla.
        public double? CritChance { get; set; }

        public List<double>? RecipeFactors { get; set; }
    }
}
