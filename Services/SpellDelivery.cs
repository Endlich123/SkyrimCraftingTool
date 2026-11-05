using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Whether a spell can deliver its effects: cast type and target type belong to both records,
    // and the two have to agree.
    //
    // WHY IT IS CHECKED. A created power set to Aimed with a Self effect could be added in the game
    // and did nothing (user, 2026-11-05); set to Self it worked. Measured on 7.573 spells with
    // effects: 7.540 match on every effect, 12 on some, 21 on none. So a mismatch is rare but real,
    // and a few vanilla spells have one - which is why this is a hint and never blocks anything.
    public static class SpellDelivery
    {
        // Empty when everything fits. A value missing on either side is unknown rather than wrong,
        // and so is an effect outside the scan (null here): neither says anything about a mismatch.
        public static string Warning(string castType, string targetType, IEnumerable<MagicEffectsRecords> effects)
        {
            var misfits = (effects ?? Enumerable.Empty<MagicEffectsRecords>())
                .Where(e => e != null && (Differs(castType, e.CastType) || Differs(targetType, e.TargetType)))
                .Select(e => $"{Label(e)} ({e.CastType}/{e.TargetType})")
                .ToList();

            if (misfits.Count == 0) return "";

            return $"This spell is {castType}/{targetType}, but these effects are not: {string.Join(", ", misfits)}. " +
                   "An effect that does not match its spell may do nothing in the game.";
        }

        private static bool Differs(string spell, string effect)
            => !string.IsNullOrEmpty(spell) && !string.IsNullOrEmpty(effect)
               && !string.Equals(spell, effect, StringComparison.OrdinalIgnoreCase);

        private static string Label(MagicEffectsRecords effect)
            => !string.IsNullOrEmpty(effect.Name) ? effect.Name
             : !string.IsNullOrEmpty(effect.EditorID) ? effect.EditorID
             : effect.Key;
    }
}
