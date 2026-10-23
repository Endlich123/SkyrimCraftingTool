using System;
using System.Runtime.InteropServices;
using Color = System.Windows.Media.Color;

namespace SkyrimCraftingTool.Services
{
    // Recolors a window's native title bar via DWM so it matches the app's dark theme/accent.
    // DWMWA_CAPTION_COLOR only exists on Windows 11 22000+; DwmSetWindowAttribute just fails
    // silently on older Windows, leaving the default title bar - no fallback needed.
    internal static class DwmTitleBarService
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        public static void ApplyAccentCaption(IntPtr hwnd, Color captionColor, Color textColor)
        {
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

            int caption = ToColorRef(captionColor);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

            int text = ToColorRef(textColor);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }

        private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    }
}
