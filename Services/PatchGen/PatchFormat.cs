using System.Globalization;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // All numbers in a SkyPatcher INI must be invariant-culture ("1.5", never "1,5") or the
    // file breaks on a German locale. Central helpers so no call site formats by hand.
    public static class PatchFormat
    {
        // Trailing-zero-free fixed point: 50 -> "50", 1.5 -> "1.5", 0.75 -> "0.75".
        public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

        // FormID as SkyPatcher / xEdit copy form: 8 hex, upper-case, zero-padded, no "0x".
        public static string FormId8(string? formId)
        {
            var trimmed = (formId ?? "").Trim().TrimStart('0').ToUpperInvariant();
            if (trimmed.Length == 0) trimmed = "0";
            return trimmed.PadLeft(8, '0');
        }

        // "Plugin.esp|0ABCDE" -> "Plugin.esp|000ABCDE". Passes anything without a single '|'
        // straight through unchanged.
        public static string RefKey8(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int bar = key.IndexOf('|');
            if (bar < 0 || key.IndexOf('|', bar + 1) >= 0) return key;
            return key.Substring(0, bar + 1) + FormId8(key.Substring(bar + 1));
        }
    }
}
