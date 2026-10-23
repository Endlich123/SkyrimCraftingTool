using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // Matches item names against a preset's pattern / exclusion fields.
    //
    // Whole words, never substrings: "ear" is inside 318 item names of a real 1,300-mod list, only
    // ~100 of them are ear pieces - the rest is Underwear, Heart, Bear, Pearl, Researcher, Spear ...
    //
    //   - Comma-separated terms, case-insensitive.
    //   - A term matches a whole word: "ear" matches "Ear", not "Bear" or "Underwear".
    //   - A trailing * matches the start of a word: "ear*" matches "Ears", "Earrings", "Earmuffs".
    //   - Several words in one term must follow each other: "cat ear*" matches "Blue Cat Ears".
    //   - Word boundaries are everything that is not a letter or digit (space, -, ', (, ) ...).
    //   - Any exclusion term that matches vetoes the whole match.
    public static class NameMatcher
    {
        // The pattern term that matched, or null. Empty patterns never match - a preset without a
        // pattern takes no part in name-based assignment.
        public static string? Match(string? name, string? patterns, string? exclusions)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var words = Tokenize(name);
            if (words.Count == 0) return null;

            string? hit = ParseTerms(patterns).FirstOrDefault(t => TermMatches(words, t));
            if (hit == null) return null;

            if (ParseTerms(exclusions).Any(t => TermMatches(words, t))) return null;

            return hit;
        }

        public static bool IsMatch(string? name, string? patterns, string? exclusions)
            => Match(name, patterns, exclusions) != null;

        internal static List<string> Tokenize(string text)
        {
            var words = new List<string>();
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
                if (isWordChar && start < 0) start = i;
                else if (!isWordChar && start >= 0)
                {
                    words.Add(text.Substring(start, i - start).ToLowerInvariant());
                    start = -1;
                }
            }
            return words;
        }

        private static IEnumerable<string> ParseTerms(string? field)
        {
            if (string.IsNullOrWhiteSpace(field)) yield break;
            foreach (var raw in field.Split(','))
            {
                var term = raw.Trim();
                if (term.Length > 0) yield return term;
            }
        }

        private static bool TermMatches(List<string> words, string term)
        {
            var parts = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.ToLowerInvariant())
                            .ToList();
            if (parts.Count == 0) return false;

            for (int start = 0; start + parts.Count <= words.Count; start++)
            {
                bool all = true;
                for (int k = 0; k < parts.Count && all; k++)
                    all = WordMatches(words[start + k], parts[k]);
                if (all) return true;
            }
            return false;
        }

        private static bool WordMatches(string word, string part)
        {
            if (part.EndsWith("*"))
            {
                var prefix = part.TrimEnd('*');
                return prefix.Length > 0 && word.StartsWith(prefix, StringComparison.Ordinal);
            }
            return word == part;
        }
    }
}
