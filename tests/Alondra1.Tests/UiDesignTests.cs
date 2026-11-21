using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;
using Alondra1.Core.Config;

namespace Alondra1.Tests;

/// <summary>Checks for the UI modernization: tokens, themes, contrast, icons, backgrounds, and the two-row shell.</summary>
public class UiDesignTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string App => Path.Combine(RepoRoot(), "src", "Alondra1.App");

    // ---------- Shell layout ----------

    [Fact]
    public void Main_window_has_exactly_two_rows_above_the_content()
    {
        var window = XDocument.Load(Path.Combine(App, "MainWindow.xaml"));
        var rootGrid = window.Root!.Element(Wpf + "Border")!.Element(Wpf + "Grid")!;
        var rows = rootGrid.Element(Wpf + "Grid.RowDefinitions")!.Elements(Wpf + "RowDefinition").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("*", (string?)rows[2].Attribute("Height"));
        var header = rootGrid.Elements(Wpf + "Grid").Single(element => (string?)element.Attribute(Xaml + "Name") == "Header");
        var toolbar = rootGrid.Elements(Wpf + "Border").Single(element => (string?)element.Attribute(Xaml + "Name") == "Toolbar");
        Assert.Equal("0", (string?)header.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)toolbar.Attribute("Grid.Row"));
        // The module navigation and search live in the header row, not in a separate row.
        Assert.Contains(header.Descendants(), element => (string?)element.Attribute("ItemsSource") == "{Binding Workspaces}");
        Assert.Contains(header.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "SearchBox");
        Assert.Contains(toolbar.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "AccountBox");
        Assert.Contains(toolbar.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "ToolbarFolders");
        Assert.DoesNotContain(window.Descendants(), element => (string?)element.Attribute("Text") == "{Binding StatusText}");
    }

    [Fact]
    public void Header_and_toolbar_heights_match_the_spec()
    {
        var tokens = Tokens();
        Assert.InRange(double.Parse(tokens["HeaderHeight"], CultureInfo.InvariantCulture), 56, 60);
        Assert.InRange(double.Parse(tokens["ToolbarHeight"], CultureInfo.InvariantCulture), 48, 52);
        Assert.Equal("8", tokens["RadiusSm"]);
        Assert.Equal("30", tokens["ControlSm"]);
        Assert.Equal("36", tokens["ControlMd"]);
        Assert.Equal("40", tokens["ControlLg"]);
        Assert.Equal("14", tokens["FontBody"]);
        Assert.Equal("12", tokens["FontLabel"]);
        Assert.Equal("10", tokens["RadiusMd"]);
        Assert.Equal("12", tokens["RadiusLg"]);
        Assert.Equal("16", tokens["RadiusXl"]);
        Assert.Equal("14", tokens["RadiusSearch"]);
    }

    // ---------- Themes ----------

    [Fact]
    public void Light_and_dark_palettes_define_the_same_tokens_and_every_brush_used()
    {
        var light = Palette("Light.xaml");
        var dark = Palette("Dark.xaml");
        Assert.Equal(light.Keys.Order(), dark.Keys.Order());
        var used = Directory.EnumerateFiles(App, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"DynamicResource\s+(\w+)").Select(match => match.Groups[1].Value))
            .ToHashSet();
        var code = Directory.EnumerateFiles(App, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"SetResourceReference\([^,]+,\s*""(\w+Brush)""\)").Select(match => match.Groups[1].Value));
        used.UnionWith(code);
        foreach (var key in used)
            Assert.True(light.ContainsKey(key), $"Light.xaml is missing {key}");
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Text_and_accent_colors_meet_contrast_requirements(string theme)
    {
        var palette = Palette(theme);
        void Check(string foreground, string background, double minimum) =>
            Assert.True(Contrast(palette[foreground], palette[background]) >= minimum,
                $"{theme}: {foreground} on {background} is {Contrast(palette[foreground], palette[background]):0.00}:1, needs {minimum}:1");
        Check("TextBrush", "SurfaceBrush", 7);
        Check("TextBrush", "ChromeBrush", 7);
        Check("TextBrush", "SelectedBrush", 4.5);
        Check("MutedBrush", "SurfaceBrush", 4.5);
        Check("MutedBrush", "ChromeBrush", 4.5);
        Check("MutedBrush", "SubtleBrush", 4.5);
        Check("BlueBrush", "SelectedBrush", 4.5);
        Check("BlueBrush", "SurfaceBrush", 4.5);
        Check("OnBrandBrush", "BrandBrush", 4.5);
        Check("DangerBrush", "SurfaceBrush", 4.5);
        Check("SuccessBrush", "SurfaceBrush", 3);
        Check("StarBrush", "SurfaceBrush", 3);
    }

    // ---------- Icons ----------

    [Fact]
    public void Icons_are_valid_24_pixel_paths()
    {
        var icons = IconData();
        Assert.True(icons.Count >= 50, $"Only {icons.Count} icon paths");
        foreach (var (key, data) in icons)
        {
            var geometry = Geometry.Parse(data);
            var bounds = geometry.Bounds;
            Assert.False(bounds.IsEmpty, key + " is empty");
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, $"{key} leaves the 24 x 24 box: {bounds}");
        }
    }

    [Fact]
    public void Every_icon_referenced_by_the_app_exists()
    {
        var available = IconData().Keys.Where(key => key.Count(ch => ch == '.') == 1).Select(key => key["Icon.".Length..]).ToHashSet();
        var referenced = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(App, "*.*", SearchOption.AllDirectories).Where(file => file.EndsWith(".xaml") || file.EndsWith(".cs")))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"Kind=""(\w+)""|Kind = ""(\w+)""|Icon = ""(\w+)""|""(\w+)"" => ""(\w+)""|=> ""(\w+)"",|""(\w+)"", ""Search \w+""\)|IsDefault \? ""(\w+)"" : ""(\w+)""|IsUnread == true \? ""(\w+)"" : ""(\w+)""|""Icon\.(\w+)"""))
            {
                foreach (Group group in match.Groups.Values.Skip(1).Where(group => group.Success))
                    referenced.Add(group.Value);
            }
        }

        // Only names that look like icon names (capitalized words) and are used as icons.
        referenced.RemoveWhere(name => !char.IsUpper(name[0]));
        var iconish = new[] { "Mail", "Calendar", "Todo", "Notes", "Contacts", "Chat", "Settings", "Search", "Inbox", "Sent", "Drafts", "Delete", "Folder", "Home", "List", "Group", "Chart", "Sun", "Priority", "Clock", "Alert", "Check", "Unscheduled", "MarkRead" };
        foreach (var name in iconish)
            Assert.Contains(name, available);
        var xamlKinds = Directory.EnumerateFiles(App, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"Kind=""(\w+)""").Select(match => match.Groups[1].Value))
            .Concat(Directory.EnumerateFiles(App, "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"Kind = ""(\w+)""").Select(match => match.Groups[1].Value)));
        foreach (var kind in xamlKinds)
            Assert.True(available.Contains(kind), $"Icon '{kind}' is used but not defined in Icons.xaml");
    }

    // ---------- Backgrounds ----------

    [Fact]
    public void Background_defaults_follow_the_spec_and_old_configs_get_them()
    {
        var settings = new BackgroundSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(0.85, settings.Opacity);
        Assert.Equal("mountains", settings.Image);
        Assert.Equal(2, settings.Blur);
        Assert.Equal(0.1, settings.Desaturation);
        Assert.Equal(BackgroundFit.Cover, settings.Fit);
        Assert.Equal(BackgroundPosition.Center, settings.Position);

        var old = ConfigWorkflow.Parse("""{"schemaVersion":1,"appearance":{"theme":"dark"},"accounts":[]}""");
        Assert.Equal(BackgroundCatalog.Default, old.Appearance.Background.Image);

        var saved = new AppConfig();
        saved.Appearance.Background = new BackgroundSettings { Image = "mountains", Opacity = 0.3, Fit = BackgroundFit.Contain, Position = BackgroundPosition.Top };
        var roundTrip = ConfigWorkflow.Parse(System.Text.Json.JsonSerializer.Serialize(saved, ConfigJson.Options));
        Assert.Equal("mountains", roundTrip.Appearance.Background.Image);
        Assert.Equal(0.3, roundTrip.Appearance.Background.Opacity);
        Assert.Equal(BackgroundFit.Contain, roundTrip.Appearance.Background.Fit);
        Assert.Equal(BackgroundPosition.Top, roundTrip.Appearance.Background.Position);
    }

    [Fact]
    public void Background_values_are_clamped_and_unknown_images_fall_back()
    {
        var wild = new BackgroundSettings { Image = "http://example.com/x.jpg", Opacity = 5, Blur = -3, Desaturation = double.NaN, Brightness = 9, Overlay = 2, Fit = (BackgroundFit)42 }.Normalized();
        Assert.Equal(BackgroundCatalog.Default, wild.Image);
        Assert.Equal(BackgroundSettings.MaxOpacity, wild.Opacity);
        Assert.Equal(0, wild.Blur);
        Assert.Equal(BackgroundSettings.DefaultDesaturation, wild.Desaturation);
        Assert.Equal(0.5, wild.Brightness);
        Assert.Equal(BackgroundSettings.MaxOverlay, wild.Overlay);
        Assert.Equal(BackgroundFit.Cover, wild.Fit);
        Assert.Equal(BackgroundCatalog.Default, new BackgroundSettings { Image = BackgroundCatalog.Custom }.Normalized().Image);
        Assert.Equal(BackgroundCatalog.Custom, new BackgroundSettings { Image = BackgroundCatalog.Custom, CustomPath = "C:\\x.jpg" }.Normalized().Image);
        Assert.Equal(BackgroundCatalog.None, new BackgroundSettings { Image = BackgroundCatalog.None }.Normalized().Image);
    }

    [Fact]
    public void Untouched_old_muted_defaults_are_upgraded_but_chosen_values_are_kept()
    {
        // A config written before the defaults changed: no revision, the old 15% / 8 px / 35% values.
        var old = ConfigWorkflow.Parse("""{"schemaVersion":1,"appearance":{"background":{"enabled":true,"image":"coast","opacity":0.15,"blur":8,"desaturation":0.35}},"accounts":[]}""");
        var upgraded = old.Appearance.Background.Normalized();
        Assert.Equal(BackgroundSettings.DefaultOpacity, upgraded.Opacity);
        Assert.Equal(BackgroundSettings.DefaultBlur, upgraded.Blur);
        Assert.Equal(BackgroundSettings.DefaultDesaturation, upgraded.Desaturation);
        Assert.Equal("coast", upgraded.Image);
        Assert.Equal(BackgroundSettings.CurrentRevision, upgraded.Revision);
        var oldDefaultPicture = ConfigWorkflow.Parse("""{"schemaVersion":1,"appearance":{"background":{"image":"soft-blue","opacity":0.15,"blur":8,"desaturation":0.35}},"accounts":[]}""");
        Assert.Equal(BackgroundCatalog.Default, oldDefaultPicture.Appearance.Background.Normalized().Image);
        var chosenPicture = ConfigWorkflow.Parse("""{"schemaVersion":1,"appearance":{"background":{"image":"soft-blue","opacity":0.4}},"accounts":[]}""");
        Assert.Equal("soft-blue", chosenPicture.Appearance.Background.Normalized().Image);

        var chosen = ConfigWorkflow.Parse("""{"schemaVersion":1,"appearance":{"background":{"opacity":0.3,"blur":8,"desaturation":0.35}},"accounts":[]}""");
        Assert.Equal(0.3, chosen.Appearance.Background.Normalized().Opacity);
        var current = new BackgroundSettings { Revision = BackgroundSettings.CurrentRevision, Opacity = 0.15, Blur = 8, Desaturation = 0.35 };
        Assert.Equal(0.15, current.Normalized().Opacity);
        Assert.Equal(1.0, new BackgroundSettings { Opacity = 1 }.Normalized().Opacity);
    }

    [Fact]
    public void Every_basic_control_has_a_shared_default_style()
    {
        var document = XDocument.Load(Path.Combine(App, "Themes", "Controls.xaml"));
        var implicitTargets = document.Root!.Elements(Wpf + "Style")
            .Where(style => style.Attribute(Xaml + "Key") is null)
            .Select(style => (string?)style.Attribute("TargetType"))
            .ToHashSet();
        foreach (var control in new[] { "Button", "TextBox", "PasswordBox", "ComboBox", "ComboBoxItem", "CheckBox", "RadioButton", "Slider", "ScrollBar", "ListBox", "ContextMenu", "MenuItem", "ToolTip" })
            Assert.True(implicitTargets.Contains(control), $"{control} has no default style, so it would fall back to the Windows look");
        var keys = document.Root.Elements(Wpf + "Style").Select(style => (string?)style.Attribute(Xaml + "Key")).ToHashSet();
        foreach (var variant in new[] { "PrimaryButton", "SecondaryButton", "QuietButton", "DangerButton", "IconButton", "CompactButton", "LargeButton", "SearchBox", "InlineEditor", "DialogFooter" })
            Assert.Contains(variant, keys);
    }

    [Fact]
    public void Dialogs_use_the_themed_frame_and_message_box()
    {
        var dialogs = File.ReadAllText(Path.Combine(App, "Dialogs.cs"));
        Assert.Contains("ShellWindow.ThemeCaption(window)", dialogs, StringComparison.Ordinal);
        Assert.Contains("DialogFooter", dialogs, StringComparison.Ordinal);
        // Native message boxes are used only where the app's styles may not exist yet (start-up and crash handling).
        foreach (var file in Directory.EnumerateFiles(App, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var name = Path.GetFileName(file);
            if (name is "App.xaml.cs" or "Dialogs.cs")
                continue;
            Assert.DoesNotContain("MessageBox.Show(", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Picture_type_is_checked_by_content()
    {
        Assert.Equal(BackgroundImageFile.Kind.Png, BackgroundImageFile.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Equal(BackgroundImageFile.Kind.Jpeg, BackgroundImageFile.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(BackgroundImageFile.Kind.WebP, BackgroundImageFile.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(BackgroundImageFile.Kind.Unknown, BackgroundImageFile.Detect("GIF89a"u8));
        Assert.Equal(BackgroundImageFile.Kind.Unknown, BackgroundImageFile.Detect([]));

        var directory = Directory.CreateTempSubdirectory("alondra1-bg-").FullName;
        var fake = Path.Combine(directory, "photo.png");
        File.WriteAllText(fake, "not really a picture");
        Assert.NotNull(BackgroundImageFile.Check(fake));
        Assert.NotNull(BackgroundImageFile.Check(Path.Combine(directory, "missing.jpg")));
        var real = Path.Combine(App, "Assets", "Backgrounds", "coast.jpg");
        Assert.Null(BackgroundImageFile.Check(real));
    }

    [Fact]
    public void Built_in_backgrounds_are_bundled_locally_and_small()
    {
        foreach (var (id, _) in BackgroundCatalog.BuiltIn)
        {
            var file = new FileInfo(Path.Combine(App, "Assets", "Backgrounds", id + ".jpg"));
            Assert.True(file.Exists, id + " is missing");
            Assert.InRange(file.Length, 1_000, 250_000);
            Assert.Null(BackgroundImageFile.Check(file.FullName));
        }

        var project = File.ReadAllText(Path.Combine(App, "Alondra1.App.csproj"));
        Assert.Contains(@"Assets\Backgrounds\*.jpg", project, StringComparison.Ordinal);
    }

    [Fact]
    public void App_markup_loads_nothing_from_the_network()
    {
        foreach (var file in Directory.EnumerateFiles(App, "*.xaml", SearchOption.AllDirectories))
        {
            var text = Regex.Replace(File.ReadAllText(file), @"xmlns(:\w+)?=""[^""]+""", "");
            Assert.False(Regex.IsMatch(text, @"https?://", RegexOptions.IgnoreCase), Path.GetFileName(file) + " refers to a web address");
        }

        var service = File.ReadAllText(Path.Combine(App, "Shell", "BackgroundService.cs"));
        Assert.DoesNotContain("HttpClient", service, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", service, StringComparison.Ordinal);
    }

    // ---------- Helpers ----------

    private static Dictionary<string, string> Tokens()
    {
        var document = XDocument.Load(Path.Combine(App, "Themes", "Controls.xaml"));
        return document.Root!.Elements()
            .Where(element => element.Attribute(Xaml + "Key") is not null && element.Name.LocalName is "CornerRadius" or "Double")
            .ToDictionary(element => (string)element.Attribute(Xaml + "Key")!, element => element.Value.Trim());
    }

    private static Dictionary<string, Color> Palette(string file)
    {
        var document = XDocument.Load(Path.Combine(App, "Themes", file));
        return document.Root!.Elements(Wpf + "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(Xaml + "Key")!, element => (Color)ColorConverter.ConvertFromString((string)element.Attribute("Color")!));
    }

    private static Dictionary<string, string> IconData()
    {
        var document = XDocument.Load(Path.Combine(App, "Themes", "Icons.xaml"));
        return document.Root!.Elements(Wpf + "StreamGeometry").ToDictionary(element => (string)element.Attribute(Xaml + "Key")!, element => element.Value.Trim());
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Alondra1.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
