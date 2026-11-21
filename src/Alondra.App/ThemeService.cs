using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Alondra.Core.Config;

namespace Alondra.App;

public static class ThemeService
{
    public static void Apply(AppearanceSettings appearance)
    {
        var light = appearance.Theme switch
        {
            ThemePreference.Light => true,
            ThemePreference.Dark => false,
            _ => SystemIsLight(),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = Defaults(light);
        var colors = light ? appearance.LightColors : appearance.DarkColors;
        foreach (var (key, value) in colors ?? [])
            if (palette[key] is SolidColorBrush && IsHexColor(value))
                palette[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        // Over a background picture, main panels are about 69% opaque so the picture shows through, and the header gets
        // a light scrim of the base color behind its labels. Popups, menus, and inputs keep the opaque SurfaceBrush.
        var active = Shell.BackgroundService.IsActive(appearance.Background);
        if (palette["SurfaceBrush"] is SolidColorBrush surface)
        {
            var color = surface.Color;
            if (active)
                color.A = 0xB0;
            palette["PanelBrush"] = new SolidColorBrush(color);
        }

        if (palette["ChromeBrush"] is SolidColorBrush chrome)
        {
            var scrim = chrome.Color;
            scrim.A = active ? (byte)0x73 : (byte)0x00;
            palette["HeaderScrimBrush"] = new SolidColorBrush(scrim);
        }

        dictionaries[0] = palette;
        // Native title bars of open dialogs follow the new palette too.
        Shell.ShellWindow.ApplyCaptionToAll();
        // The picture's brightness adapts to the theme, so it is re-processed when the theme changes.
        (Application.Current.MainWindow as MainWindow)?.ApplyBackground();
    }

    public static bool IsDark => Application.Current.Resources.MergedDictionaries[0]["ChromeBrush"] is SolidColorBrush { Color: var c } && c.R + c.G + c.B < 200;

    public static ResourceDictionary Defaults(bool light) => new()
    {
        Source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative),
    };

    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    public static bool SystemIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
