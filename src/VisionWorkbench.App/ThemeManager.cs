using System.Windows;
using System.Windows.Media;

namespace VisionWorkbench.App;

public static class ThemeManager
{
    public static void Apply(string? mode)
    {
        var dark = string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase);
        Set("WindowBrush", dark ? "#181A1F" : "#F4F7FB");
        Set("SurfaceBrush", dark ? "#22262D" : "#FFFFFF");
        Set("SurfaceAltBrush", dark ? "#292E37" : "#F8FAFC");
        Set("InputBrush", dark ? "#15181E" : "#FFFFFF");
        Set("DisabledBrush", dark ? "#252A32" : "#EEF2F7");
        Set("GridLineBrush", dark ? "#343B46" : "#E9EEF5");
        Set("ScrollTrackBrush", dark ? "#171A20" : "#EEF2F7");
        Set("ScrollThumbBrush", dark ? "#4B5563" : "#AAB4C3");
        Set("ScrollThumbHoverBrush", dark ? "#6B7280" : "#7C899B");
        Set("BorderBrush", dark ? "#414854" : "#D9E1EC");
        Set("TextBrush", dark ? "#E6E8EB" : "#172033");
        Set("MutedTextBrush", dark ? "#AAB2BF" : "#64748B");
        Set("AccentBrush", dark ? "#4C8DFF" : "#2563EB");
        Set("AccentHoverBrush", dark ? "#6EA4FF" : "#1D4ED8");
        Set("AccentSoftBrush", dark ? "#263A5B" : "#E8F0FF");
        ApplySystemColors(dark);
    }

    public static void ApplyUiScale(double scale)
    {
        scale = Math.Clamp(scale, 0.8, 1.5);
        // Font scaling must not apply a LayoutTransform to the custom chrome Window:
        // doing so offsets native hit testing for minimize/maximize/close buttons.
        System.Windows.Application.Current.Resources["GlobalFontSize"] = 13d * scale;
    }

    private static void Set(string key, string color) =>
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static void ApplySystemColors(bool dark)
    {
        var resources = System.Windows.Application.Current.Resources;
        var input = Brush(dark ? "#15181E" : "#FFFFFF");
        var surface = Brush(dark ? "#22262D" : "#F3F4F6");
        var text = Brush(dark ? "#E6E8EB" : "#172033");
        var muted = Brush(dark ? "#AAB2BF" : "#64748B");
        var accent = Brush(dark ? "#4C8DFF" : "#2563EB");
        resources[SystemColors.WindowBrushKey] = input;
        resources[SystemColors.ControlBrushKey] = surface;
        resources[SystemColors.WindowTextBrushKey] = text;
        resources[SystemColors.ControlTextBrushKey] = text;
        resources[SystemColors.GrayTextBrushKey] = muted;
        resources[SystemColors.HighlightBrushKey] = accent;
        resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
