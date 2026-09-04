using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SkyrimCraftingTool.Model
{
    // Tiny key-value store for UI preferences (toggle states, remembered choices) that don't
    // belong in item.db / settings.db. Backed by Input\prefs.json. Robust to a missing or
    // corrupt file — every getter falls back to the caller's default.
    public static class AppPrefs
    {
        private static string PrefsPath => Path.Combine(GlobalState.Tool.InputFolder, "prefs.json");

        private static readonly object _gate = new();
        private static Dictionary<string, string>? _cache;

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            lock (_gate)
            {
                if (_cache != null) return _cache;
                try
                {
                    _cache = File.Exists(PrefsPath)
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PrefsPath)) ?? new()
                        : new();
                }
                catch
                {
                    _cache = new();
                }
                return _cache;
            }
        }

        public static bool GetBool(string key, bool fallback = false)
            => Load().TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

        public static void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

        public static string GetString(string key, string fallback = "")
            => Load().TryGetValue(key, out var v) ? v : fallback;

        public static void SetString(string key, string value) => Set(key, value ?? "");

        private static void Set(string key, string value)
        {
            lock (_gate)
            {
                Load()[key] = value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
                    File.WriteAllText(PrefsPath,
                        JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("AppPrefs save failed", ex);
                }
            }
        }
    }
}
