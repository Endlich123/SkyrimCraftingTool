using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Applies a color palette to the running app.
    //
    // HOW: a palette (Styles/Themes/*.xaml) is a dictionary of brushes, one per token, merged into
    // Application.Resources. Switching the theme REPLACES that dictionary, and the ~330
    // {DynamicResource ColorX} references across the UI re-resolve against the new one. Nothing is
    // written into a brush and nothing has to be re-created.
    //
    // WHY NOT the other way round - keep one set of brushes and write Color into them, which needs
    // no DynamicResource at all: WPF freezes Freezables in any ResourceDictionary that gets an
    // owner, and Application.Resources is one, so the writes are simply refused. Binding each
    // brush's Color to a DynamicResource does keep it unfrozen, but then the two mechanisms fight:
    // some writes stick and others are reverted the next time WPF re-resolves that reference, which
    // is exactly as confusing as it sounds - half the UI changes theme and half does not.
    //
    // The one thing a dictionary swap cannot reach is code that hands a Brush to a binding, because
    // a binding with a converter is not re-evaluated when the palette changes. That is what
    // LiveBrush below is for.
    public static class ThemeService
    {
        public const string Dark = "Dark";
        public const string Light = "Light";

        // Themes that ship with the tool. A palette file must exist under Styles/Themes/<name>.xaml
        // defining every token the other palettes define.
        public static readonly IReadOnlyList<string> Available = new[] { Dark, Light };

        private const string PrefKey = "ui.theme";

        // "SkyrimCraftingTool;" - the assembly the palettes are compiled into. Read from the type
        // rather than spelled out, so renaming the assembly cannot leave a dead URI behind.
        private static readonly string PaletteAssembly =
            typeof(ThemeService).Assembly.GetName().Name + ";";

        public static string Current { get; private set; } = Dark;

        // Reads the remembered choice (falling back to Dark) and applies it. Called once at startup.
        public static void ApplySaved()
        {
            var saved = AppPrefs.GetString(PrefKey, Dark);
            if (!Available.Contains(saved, StringComparer.OrdinalIgnoreCase))
                saved = Dark;

            Apply(saved, remember: false);
        }

        public static void Apply(string themeName, bool remember = true)
        {
            if (string.IsNullOrWhiteSpace(themeName)) return;

            var app = Application.Current;
            if (app == null) return;   // designer / unit test context

            ResourceDictionary palette;
            try
            {
                // Assembly-qualified rather than the shorter "/Styles/Themes/x.xaml": a relative
                // pack URI is resolved against the ENTRY assembly, which is this one only as long as
                // the app is started the normal way.
                palette = new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/{PaletteAssembly}component/Styles/Themes/{themeName}.xaml",
                        UriKind.Absolute),
                };
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ThemeService: palette '{themeName}' could not be loaded", ex);
                return;
            }

            var index = FindPaletteIndex(app);
            if (index < 0)
            {
                AppLogger.LogWarning(
                    "ThemeService: no palette found in Application.Resources - the theme cannot be " +
                    "switched. App.xaml must merge Styles/Themes/<name>.xaml as its FIRST dictionary.");
                return;
            }

            app.Resources.MergedDictionaries[index] = palette;

            var missing = new List<string>();
            RefreshLiveBrushes(palette, missing);

            if (missing.Count > 0)
                AppLogger.LogWarning(
                    $"ThemeService: {missing.Count} token(s) missing from palette '{themeName}': " +
                    string.Join(", ", missing));

            Current = themeName;
            if (remember) AppPrefs.SetString(PrefKey, themeName);

            ThemeChanged?.Invoke();
        }

        // For the last corner a resource reference cannot reach: the window caption. Its colors are
        // handed to the OS through DWM once, when the window's handle appears, so a palette swap
        // leaves the title bar showing the theme the app happened to start in.
        public static event Action? ThemeChanged;

        // The palette is identified by content, not by position: it is the merged dictionary that
        // carries the token every palette must define. Hard-coding index 0 would silently theme the
        // wrong dictionary the first time someone adds another one to App.xaml.
        private const string MarkerToken = "ColorBackgroundBase";

        private static int FindPaletteIndex(Application app)
        {
            var merged = app.Resources.MergedDictionaries;
            for (int i = 0; i < merged.Count; i++)
                if (merged[i].Contains(MarkerToken))
                    return i;

            return -1;
        }

        // --- Brushes for code that cannot use DynamicResource ---
        //
        // The three value converters (chip grounds, edited-field borders, dead-reference borders)
        // hand a Brush straight to a binding. A binding with a converter is only re-evaluated when
        // its SOURCE changes, so a palette swap would leave every chip and every border showing the
        // previous theme's brush until the underlying value happened to change.
        //
        // These brushes solve that the way the palette cannot: they are created in code and never
        // put into a ResourceDictionary, so nothing freezes them and writing Color works. They are
        // shared instances, so one write repaints every chip at once.
        private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new(StringComparer.Ordinal);

        public static SolidColorBrush LiveBrush(string paletteKey)
        {
            if (LiveBrushes.TryGetValue(paletteKey, out var existing))
                return existing;

            // Seeded from whatever the palette currently holds. Transparent only if this runs before
            // any palette is merged, which the next Apply() corrects.
            var seed = Application.Current?.TryFindResource(paletteKey) as SolidColorBrush;
            var brush = new SolidColorBrush(seed?.Color ?? Colors.Transparent);
            LiveBrushes[paletteKey] = brush;
            return brush;
        }

        private static void RefreshLiveBrushes(ResourceDictionary palette, List<string> missing)
        {
            foreach (var pair in LiveBrushes)
            {
                if (palette[pair.Key] is SolidColorBrush source)
                    pair.Value.Color = source.Color;
                else
                    missing.Add($"{pair.Key} (live brush)");
            }
        }
    }
}
