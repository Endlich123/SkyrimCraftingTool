using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public static class ContainerStringParser
    {
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
                Debug.WriteLine($"Parsing part: {part}");
                var trimmed = part.Trim();
                if (!trimmed.Contains(":"))
                    continue;

                var split = trimmed.Split(':');
                var containerKey = split[0].Trim();

                var lvliPart = split[1].Trim();
                lvliPart = lvliPart.Trim('{', '}');

                var levels = new Dictionary<string, int>();

                if (!string.IsNullOrWhiteSpace(lvliPart))
                {
                    var lvliEntries = lvliPart.Split(';', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var lvli in lvliEntries)
                    {
                        var lv = lvli.Trim();
                        if (!lv.Contains(",")) continue;

                        var s = lv.Split(',');
                        var lvliKey = s[0].Trim();
                        var lvl = int.Parse(s[1].Trim());

                        levels[lvliKey] = lvl;
                    }
                }

                result.Add(new ParsedContainerEntry(containerKey, levels));
            }

            return result;
        }
    }

    public class ParsedContainerEntry
    {
        public string ContainerKey { get; }
        public Dictionary<string, int> Levels { get; }

        public ParsedContainerEntry(string key, Dictionary<string, int> levels)
        {
            ContainerKey = key;
            Levels = levels;
        }
    }
}
