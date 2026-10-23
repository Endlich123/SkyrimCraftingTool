using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkyrimCraftingTool.Services
{
    // Reads the ContainerString: "{ContainerKey: {LVLiKey,Level[,Count]; ...}; ...}".
    //
    // The trailing Count is an extension, and an entry without it is not an error: every string
    // written before it existed has two fields, and those strings are sitting in the user's item.db
    // AND in their preset files (see PresetFile - same format on both sides). Missing count means 1,
    // which is exactly what the patch wrote before the field existed, so an old string keeps meaning
    // what it always meant.
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

                        // A malformed count falls back to 1 rather than dropping the whole placement:
                        // losing the level too would silently un-place an item the user had set up.
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

    // What the user asked for on one leveled list: from which level, and how many.
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
