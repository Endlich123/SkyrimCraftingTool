using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool;

public static class Bench
{
    private static readonly Dictionary<string, DateTime> _starts = new();

    public static void Start(string name)
    {
        _starts[name] = DateTime.Now;
        Console.WriteLine($"[START] {name}");
    }

    public static void End(string name)
    {
        if (_starts.TryGetValue(name, out var t))
        {
            var ms = (DateTime.Now - t).TotalMilliseconds;
            Console.WriteLine($"[END] {name}: {ms:N0} ms");
        }
        else
        {
            Console.WriteLine($"[WARN] Bench '{name}' wurde nie gestartet.");
        }
    }
}
