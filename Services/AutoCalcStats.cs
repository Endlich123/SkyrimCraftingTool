namespace SkyrimCraftingTool.Services
{
    // What the engine computes for an NPC whose AutoCalcStats flag is set.
    //
    // ================== THIS IS A HYPOTHESIS, NOT A READING ==================
    //
    // 4.487 of 6.642 NPCs (67,5 %) carry that flag, and for them the game ignores the stored
    // Health/Magicka/Stamina and recomputes from race, class and level. Measured in game: a record
    // saying 35 Health produced 300. So the stored number is not what anyone sees, and a tool that
    // shows it is showing something the game threw away.
    //
    // The formula below is the published modder description of that calculation. It has NOT been
    // confirmed against this engine, and it CANNOT be confirmed from the database alone:
    //
    //   * the stored values are no reference - they are whatever the Creation Kit last wrote, and
    //     measured across the bandit tiers they are not even linear in level (35, 109, 238, 318,
    //     398, 489 at levels 1, 5, 9, 14, 19, 25).
    //   * an in-game reading is no reference either unless you know which record spawned. An
    //     encounter comes out of a leveled list, so "the bandit in front of me" may be any tier.
    //
    // The only way to settle it is a console reading on an actor whose record is known. Until then
    // every number this produces is shown as a prediction and NOTHING is written from it - see the
    // note on writing at the bottom.
    //
    // =========================================================================
    public readonly record struct AutoCalcInputs(
        float RaceHealth, float RaceMagicka, float RaceStamina,
        int HealthWeight, int MagickaWeight, int StaminaWeight,
        int Level);

    public readonly record struct AutoCalcResult(int Health, int Magicka, int Stamina);

    public static class AutoCalcStats
    {
        // The engine hands out this many points per level, split between the three attributes by
        // the class weights.
        private const int PointsPerLevel = 10;

        public static AutoCalcResult Compute(AutoCalcInputs inputs)
        {
            int weightSum = inputs.HealthWeight + inputs.MagickaWeight + inputs.StaminaWeight;

            // A class with no weights at all would divide by zero. Nothing in the load order does -
            // all 165 name at least one - but a mod can ship anything, and a crash in a display is
            // a poor way to find out.
            if (weightSum <= 0)
                return new AutoCalcResult(
                    (int)inputs.RaceHealth, (int)inputs.RaceMagicka, (int)inputs.RaceStamina);

            // Level 1 is the base: the first level grants nothing, the gain starts at level 2.
            int levels = inputs.Level > 1 ? inputs.Level - 1 : 0;

            return new AutoCalcResult(
                Attribute(inputs.RaceHealth, inputs.HealthWeight, weightSum, levels),
                Attribute(inputs.RaceMagicka, inputs.MagickaWeight, weightSum, levels),
                Attribute(inputs.RaceStamina, inputs.StaminaWeight, weightSum, levels));
        }

        // Rounded DOWN, and the gain is rounded before it is multiplied - the description is
        // explicit that the engine truncates intermediate results. Which of the two roundings the
        // engine really does is one of the things an in-game reading has to settle: for a class
        // weighted 3/0/2 they agree, for one weighted 3/1/3 they do not.
        private static int Attribute(float raceBase, int weight, int weightSum, int levels)
        {
            int gainPerLevel = (int)(PointsPerLevel * (float)weight / weightSum);
            return (int)raceBase + gainPerLevel * levels;
        }
    }
}
