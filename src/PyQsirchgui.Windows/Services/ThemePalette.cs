using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace PyQsirchgui.Windows.Services;

public static class ThemePalette
{
    public static void Apply(ResourceDictionary resources, string theme)
    {
        if (ShouldUseDarkTheme(theme))
        {
            SetBrush(resources, "AppWindowBrush", "#202020");
            SetBrush(resources, "AppSurfaceBrush", "#1B1B1B");
            SetBrush(resources, "AppPanelBrush", "#242424");
            SetBrush(resources, "AppElevatedBrush", "#2B2B2B");
            SetBrush(resources, "AppBorderBrush", "#454545");
            SetBrush(resources, "AppSubtleBorderBrush", "#333333");
            SetBrush(resources, "AppTextBrush", "#F3F3F3");
            SetBrush(resources, "AppMutedTextBrush", "#B8B8B8");
            SetBrush(resources, "AppAccentBrush", "#4CC2FF");
            SetBrush(resources, "AppAccentSoftBrush", "#19384B");
            SetBrush(resources, "AppSelectionBrush", "#0E639C");
            SetBrush(resources, "AppSelectionTextBrush", "#FFFFFF");
            SetBrush(resources, "AppDisabledBrush", "#303030");
            SetBrush(resources, "AppDisabledTextBrush", "#D4D4D4");
            SetBrush(resources, SystemColors.ControlBrushKey, "#242424");
            SetBrush(resources, SystemColors.ControlLightBrushKey, "#2B2B2B");
            SetBrush(resources, SystemColors.ControlDarkBrushKey, "#111111");
            SetBrush(resources, SystemColors.ControlTextBrushKey, "#F3F3F3");
            SetBrush(resources, SystemColors.WindowBrushKey, "#1B1B1B");
            SetBrush(resources, SystemColors.WindowTextBrushKey, "#F3F3F3");
            SetBrush(resources, SystemColors.GrayTextBrushKey, "#B8B8B8");
            SetBrush(resources, SystemColors.ActiveBorderBrushKey, "#454545");
            SetBrush(resources, SystemColors.HighlightBrushKey, "#0E639C");
            SetBrush(resources, SystemColors.HighlightTextBrushKey, "#FFFFFF");
            SetBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, "#3A3D41");
            SetBrush(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, "#FFFFFF");
            return;
        }

        SetBrush(resources, "AppWindowBrush", "#F3F3F3");
        SetBrush(resources, "AppSurfaceBrush", "#FFFFFF");
        SetBrush(resources, "AppPanelBrush", "#FAFAFA");
        SetBrush(resources, "AppElevatedBrush", "#FFFFFF");
        SetBrush(resources, "AppBorderBrush", "#D0D0D0");
        SetBrush(resources, "AppSubtleBorderBrush", "#E5E5E5");
        SetBrush(resources, "AppTextBrush", "#1B1B1B");
        SetBrush(resources, "AppMutedTextBrush", "#666666");
        SetBrush(resources, "AppAccentBrush", "#0067C0");
        SetBrush(resources, "AppAccentSoftBrush", "#E5F1FB");
        SetBrush(resources, "AppSelectionBrush", "#CCE8FF");
        SetBrush(resources, "AppSelectionTextBrush", "#000000");
        SetBrush(resources, "AppDisabledBrush", "#E9E9E9");
        SetBrush(resources, "AppDisabledTextBrush", "#555555");
        SetBrush(resources, SystemColors.ControlBrushKey, "#F3F3F3");
        SetBrush(resources, SystemColors.ControlLightBrushKey, "#FAFAFA");
        SetBrush(resources, SystemColors.ControlDarkBrushKey, "#A0A0A0");
        SetBrush(resources, SystemColors.ControlTextBrushKey, "#1B1B1B");
        SetBrush(resources, SystemColors.WindowBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.WindowTextBrushKey, "#1B1B1B");
        SetBrush(resources, SystemColors.GrayTextBrushKey, "#666666");
        SetBrush(resources, SystemColors.ActiveBorderBrushKey, "#D0D0D0");
        SetBrush(resources, SystemColors.HighlightBrushKey, "#CCE8FF");
        SetBrush(resources, SystemColors.HighlightTextBrushKey, "#000000");
        SetBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, "#E8E8E8");
        SetBrush(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, "#000000");
    }

    private static bool ShouldUseDarkTheme(string theme)
    {
        if (theme.Equals("dark", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (theme.Equals("light", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (int?)key?.GetValue("AppsUseLightTheme") == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetBrush(ResourceDictionary resources, object key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
