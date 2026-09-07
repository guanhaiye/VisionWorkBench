using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VisionWorkbench.App;

public static class ThemeManager
{
    private static readonly string[] ThemeBrushKeys =
    [
        "AccentBrush", "AccentHoverBrush", "AccentSoftBrush",
        "WindowBrush", "SurfaceBrush", "SurfaceAltBrush", "InputBrush",
        "DisabledBrush", "GridLineBrush", "ScrollTrackBrush",
        "ScrollThumbBrush", "ScrollThumbHoverBrush", "BorderBrush",
        "TextBrush", "MutedTextBrush", "SuccessBrush", "DangerBrush",
        "SidebarBrush", "SidebarBorderBrush", "SidebarTextBrush", "SidebarMutedTextBrush",
        "SidebarHoverBrush", "SidebarPressedBrush", "SidebarIconStrokeBrush",
        "SidebarSelectedBrush", "SidebarSelectedTextBrush",
        "SidebarStatusTextBrush", "SidebarStatusMutedBrush",
        "GlobalFontSize",
    ];

    public static void Apply(string? mode)
    {
        var dark = string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase);
        Set("WindowBrush", dark ? "#181A1F" : "#F4F7FB");
        Set("SurfaceBrush", dark ? "#22262D" : "#FFFFFF");
        Set("SurfaceAltBrush", dark ? "#292E37" : "#F8FAFC");
        Set("InputBrush", dark ? "#15181E" : "#FFFFFF");
        Set("DisabledBrush", dark ? "#252A32" : "#E2E8F0");
        Set("GridLineBrush", dark ? "#343B46" : "#D7DEE8");
        Set("ScrollTrackBrush", dark ? "#171A20" : "#E7ECF3");
        Set("ScrollThumbBrush", dark ? "#4B5563" : "#94A3B8");
        Set("ScrollThumbHoverBrush", dark ? "#6B7280" : "#64748B");
        Set("BorderBrush", dark ? "#414854" : "#B8C4D3");
        Set("TextBrush", dark ? "#E6E8EB" : "#0F172A");
        Set("MutedTextBrush", dark ? "#AAB2BF" : "#475569");
        Set("SidebarBrush", dark ? "#0F1B2D" : "#FFFFFF");
        Set("SidebarBorderBrush", dark ? "#243550" : "#D7DEE8");
        Set("SidebarTextBrush", dark ? "#DCE6F5" : "#334155");
        Set("SidebarMutedTextBrush", dark ? "#8FA4C2" : "#64748B");
        Set("SidebarHoverBrush", dark ? "#1E3150" : "#E8F0FF");
        Set("SidebarPressedBrush", dark ? "#294267" : "#DCE8FF");
        Set("SidebarSelectedBrush", dark ? "#2563EB" : "#93C5FD");
        Set("SidebarSelectedTextBrush", dark ? "#FFFFFF" : "#0F172A");
        Set("SidebarIconStrokeBrush", dark ? "#DCE6F5" : "#334155");
        Set("SidebarStatusTextBrush", dark ? "#AFC0D8" : "#475569");
        Set("SidebarStatusMutedBrush", dark ? "#607796" : "#94A3B8");
        Set("AccentBrush", dark ? "#4C8DFF" : "#2563EB");
        Set("AccentHoverBrush", dark ? "#6EA4FF" : "#1D4ED8");
        Set("AccentSoftBrush", dark ? "#263A5B" : "#E8F0FF");
        ApplySystemColors(dark);
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            SyncWindowResources(window);
        }
    }

    public static void SyncWindowResources(Window window)
    {
        var applicationResources = System.Windows.Application.Current.Resources;
        foreach (var key in ThemeBrushKeys)
        {
            if (applicationResources.Contains(key))
            {
                window.Resources[key] = applicationResources[key];
            }
        }
    }

    public static void ApplyPageTextBrush(FrameworkElement element)
    {
        if (System.Windows.Application.Current.Resources["TextBrush"] is not Brush textBrush)
        {
            return;
        }

        if (element is Control control)
        {
            control.Foreground = textBrush;
        }

        element.SetValue(TextElement.ForegroundProperty, textBrush);
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
        var text = Brush(dark ? "#E6E8EB" : "#0F172A");
        var muted = Brush(dark ? "#AAB2BF" : "#475569");
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
