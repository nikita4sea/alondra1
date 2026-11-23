using System.Globalization;
using System.Text.RegularExpressions;

namespace Alondra1.Tests;

/// <summary>Client spec M4 §6: text over a background picture stays legible, whatever the picture.</summary>
public class LegibilityTests
{
    private static Dictionary<string, string> Palette(string theme)
    {
        var xaml = File.ReadAllText(Path.Combine(UiPaths.App, "Themes", theme + ".xaml"));
        return Regex.Matches(xaml, @"x:Key=""(\w+)"" Color=""(#[0-9A-Fa-f]{6})""").ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
    }

    private static byte PanelAlpha()
    {
        var source = File.ReadAllText(Path.Combine(UiPaths.App, "ThemeService.cs"));
        return byte.Parse(Regex.Match(source, @"PanelAlphaOverPicture = 0x([0-9A-Fa-f]{2})").Groups[1].Value, NumberStyles.HexNumber);
    }

    private static (double R, double G, double B) Rgb(string hex) =>
        (int.Parse(hex[1..3], NumberStyles.HexNumber), int.Parse(hex[3..5], NumberStyles.HexNumber), int.Parse(hex[5..7], NumberStyles.HexNumber));

    private static double Luminance((double R, double G, double B) color)
    {
        static double Channel(double value)
        {
            value /= 255;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static double Contrast((double, double, double) a, (double, double, double) b)
    {
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Panel_text_keeps_AA_contrast_over_any_picture(string theme)
    {
        var palette = Palette(theme);
        var alpha = PanelAlpha() / 255.0;
        var surface = Rgb(palette["SurfaceBrush"]);
        // The worst picture is the opposite extreme of the panel color: pure white behind a dark panel, black behind a light one.
        var worst = Luminance(surface) < 0.5 ? (255.0, 255.0, 255.0) : (0.0, 0.0, 0.0);
        var panel = ((alpha * surface.R) + ((1 - alpha) * worst.Item1), (alpha * surface.G) + ((1 - alpha) * worst.Item2), (alpha * surface.B) + ((1 - alpha) * worst.Item3));
        foreach (var key in new[] { "TextBrush", "MutedBrush" })
            Assert.True(Contrast(Rgb(palette[key]), panel) >= 4.5, $"{theme} {key}: {Contrast(Rgb(palette[key]), panel):0.00}:1 over the worst picture");
    }
}
