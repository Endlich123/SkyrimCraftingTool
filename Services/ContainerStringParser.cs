using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkyrimCraftingTool.Services
{
    public static class ContainerStringParser
    {
        public const int DefaultCount = 1;

        public static List<ParsedContainerEntry> Parse(string input)
        {
            var result = new List<ParsedContainerEntry>();
            if (string.IsNullOrWhiteSpace(input))
                return result;

            input = input.Trim();
            input = input.Trim('{', '}');

            var containerParts = input.Split("};", StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in containerParts)
            {
                var trimmed = part.Trim();
                if (!trimmed.Contains(":"))
                    continue;

                var split = trimmed.Split(':');
                var containerKey = split[0].Trim();

                var lvliPart = split[1].Trim();
                lvliPart = lvliPart.Trim('{', '}');

                var placements = new Dictionary<string, LvliPlacement>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(lvliPart))
                {
                    var lvliEntries = lvliPart.Split(';', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var lvli in lvliEntries)
                    {
                        var lv = lvli.Trim();
                        if (!lv.Contains(",")) continue;

                        var s = lv.Split(',');
                        var lvliKey = s[0].Trim();

                        if (!int.TryParse(s[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                            continue;

                        int count = DefaultCount;
                        if (s.Length > 2 &&
                            int.TryParse(s[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) &&
                            parsedCount > 0)
                        {
                            count = parsedCount;
                        }

                        placements[lvliKey] = new LvliPlacement(level, count);
                    }
                }

                result.Add(new ParsedContainerEntry(containerKey, placements));
            }

            return result;
        }
    }

    public readonly record struct LvliPlacement(int Level, int Count);

    public class ParsedContainerEntry
    {
        public string ContainerKey { get; }
        public Dictionary<string, LvliPlacement> Placements { get; }

        // Kept so the existing callers that only care about levels stay readable. Same data.
        public Dictionary<string, int> Levels
        {
            get
            {
                var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Placements) levels[p.Key] = p.Value.Level;
                return levels;
            }
        }

        public ParsedContainerEntry(string key, Dictionary<string, LvliPlacement> placements)
        {
            ContainerKey = key;
            Placements = placements;
        }
    }
}
