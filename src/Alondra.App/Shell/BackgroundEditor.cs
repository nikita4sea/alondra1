using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Alondra.App.ViewModels;
using Alondra.Core.Config;
using Microsoft.Win32;

namespace Alondra.App.Shell;

/// <summary>
/// Settings → Appearance → Background: gallery, local picture, adjustments, live preview on the main window.
/// Changes are kept only when the Settings dialog is saved; closing it without saving restores the previous background
/// and removes any picture copied during this session.
/// </summary>
public sealed class BackgroundEditor
{
    private readonly Window _dialog;
    private readonly MainViewModel _model;
    private readonly BackgroundSettings _original;
    private readonly List<string> _imported = [];
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private BackgroundSettings _working;
    private bool _committed;
    private bool _wasActive;
    private WrapPanel _gallery = new();
    private Button _removeCustom = new();
    private readonly List<(Slider Slider, Func<BackgroundSettings, double> Get)> _sliders = [];
    private ComboBox _position = new();
    private ComboBox _fit = new();
    private CheckBox _enabled = new();
    private bool _loading;

    public BackgroundEditor(Window dialog, MainViewModel model)
    {
        _dialog = dialog;
        _model = model;
        _original = model.Config.Appearance.Background.Normalized();
        _working = _original.Clone();
        _wasActive = BackgroundService.IsActive(_original);
        _preview.Tick += (_, _) => { _preview.Stop(); ApplyPreview(); };
        dialog.Closed += (_, _) =>
        {
            if (!_committed)
                Revert();
        };
    }

    public UIElement Build()
    {
        var panel = new StackPanel();
        panel.Children.Add(Dialogs.Title("Background"));
        panel.Children.Add(Dialogs.Note("The main window shows each change right away. Save settings to keep it."));
        _enabled = Dialogs.Check("Show a background behind the workspace", _working.Enabled);
        _enabled.Checked += (_, _) => Update(s => s.Enabled = true);
        _enabled.Unchecked += (_, _) => Update(s => s.Enabled = false);
        panel.Children.Add(_enabled);

        panel.Children.Add(Dialogs.Subtitle("Picture"));
        // Fixed item width keeps every tile, Plain color included, on the same grid.
        _gallery = new WrapPanel { ItemWidth = 128, Margin = new Thickness(-4, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(_gallery, "Background pictures");
        panel.Children.Add(_gallery);
        BuildGallery();

        _removeCustom = Dialogs.Button("Remove my picture", RemovePicture);
        panel.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Choose picture…", ChoosePicture), _removeCustom));
        panel.Children.Add(Dialogs.Note("PNG, JPEG, or WebP up to 25 MB. Alondra keeps its own copy in its data folder, so moving or deleting the original does not matter. Nothing is uploaded."));

        panel.Children.Add(Dialogs.Subtitle("Adjustments"));

        panel.Children.Add(SliderRow("Picture opacity", 0, BackgroundSettings.MaxOpacity, 0.01, s => s.Opacity, (s, v) => s.Opacity = v, v => $"{v * 100:0}%"));
        panel.Children.Add(SliderRow("Blur", 0, BackgroundSettings.MaxBlur, 1, s => s.Blur, (s, v) => s.Blur = v, v => $"{v:0} px"));
        panel.Children.Add(SliderRow("Desaturation", 0, 1, 0.01, s => s.Desaturation, (s, v) => s.Desaturation = v, v => $"{v * 100:0}%"));
        panel.Children.Add(SliderRow("Brightness", -0.5, 0.5, 0.01, s => s.Brightness, (s, v) => s.Brightness = v, v => $"{v * 100:+0;-0;0}%"));
        panel.Children.Add(SliderRow("Overlay", 0, BackgroundSettings.MaxOverlay, 0.01, s => s.Overlay, (s, v) => s.Overlay = v, v => $"{v * 100:0}%"));

        panel.Children.Add(Dialogs.Subtitle("Placement"));
        var placement = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        placement.ColumnDefinitions.Add(new ColumnDefinition());
        placement.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        placement.ColumnDefinitions.Add(new ColumnDefinition());
        _position = new ComboBox { ItemsSource = Enum.GetNames<BackgroundPosition>(), SelectedItem = _working.Position.ToString() };
        _position.SelectionChanged += (_, _) => { if (Enum.TryParse<BackgroundPosition>(_position.SelectedItem as string, out var value)) Update(s => s.Position = value); };
        _fit = new ComboBox { ItemsSource = new[] { "Cover", "Contain" }, SelectedIndex = (int)_working.Fit };
        _fit.SelectionChanged += (_, _) => Update(s => s.Fit = (BackgroundFit)Math.Max(0, _fit.SelectedIndex));
        System.Windows.Automation.AutomationProperties.SetName(_position, "Picture position");
        System.Windows.Automation.AutomationProperties.SetName(_fit, "Picture fit");
        var position = Dialogs.Labeled("Position", _position);
        var fit = Dialogs.Labeled("Fit", _fit);
        Grid.SetColumn(fit, 2);
        placement.Children.Add(position);
        placement.Children.Add(fit);
        panel.Children.Add(placement);

        panel.Children.Add(Dialogs.ButtonRow(Dialogs.Button("Reset background to default", ResetDefaults)));
        SyncControls();
        return panel;
    }

    /// <summary>Called by Save settings: keeps the working background and deletes pictures that are no longer used.</summary>
    public void Commit()
    {
        _committed = true;
        _model.Config.Appearance.Background = _working.Normalized();
        var keep = _working.Image == BackgroundCatalog.Custom ? _working.CustomPath : null;
        foreach (var path in _imported.Where(path => path != keep))
            BackgroundService.DeleteCustom(path);
        if (_original.CustomPath is { } old && old != keep)
            BackgroundService.DeleteCustom(old);
    }

    private void Revert()
    {
        _preview.Stop();
        foreach (var path in _imported)
            BackgroundService.DeleteCustom(path);
        _model.Config.Appearance.Background = _original;
        ApplyPreview();
    }

    private void Update(Action<BackgroundSettings> change)
    {
        if (_loading)
            return;
        change(_working);
        _working = _working.Normalized();
        _model.Config.Appearance.Background = _working;
        _preview.Stop();
        _preview.Start();
        SyncControls();
    }

    private void ApplyPreview()
    {
        if (Application.Current.MainWindow is MainWindow main)
            main.ApplyBackground();
        // Panel translucency depends on whether a picture is showing; re-theme only when that changes.
        var active = BackgroundService.IsActive(_model.Config.Appearance.Background);
        if (active != _wasActive)
        {
            _wasActive = active;
            ThemeService.Apply(_model.Config.Appearance);
        }
    }

    private void BuildGallery()
    {
        _gallery.Children.Clear();
        _gallery.Children.Add(Tile(BackgroundCatalog.None, "Plain color", null));
        foreach (var (id, name) in BackgroundCatalog.BuiltIn)
            _gallery.Children.Add(Tile(id, name, BackgroundService.Thumbnail(id, null)));
        if (_working.CustomPath is { } path && BackgroundService.Thumbnail(BackgroundCatalog.Custom, path) is { } custom)
            _gallery.Children.Add(Tile(BackgroundCatalog.Custom, "My picture", custom));
    }

    /// <summary>Every option, Plain color included, uses the same 112 x 70 face inside the shared TileButton frame.</summary>
    private Button Tile(string id, string name, ImageSource? image)
    {
        var face = new Border { Width = 112, Height = 70, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1) };
        face.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        if (image is null)
            face.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
        else
            face.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        var label = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(2, 6, 2, 2), TextTrimming = TextTrimming.CharacterEllipsis, Width = 112, TextAlignment = TextAlignment.Center };
        var stack = new StackPanel();
        stack.Children.Add(face);
        stack.Children.Add(label);
        var button = new Button { Content = stack, CommandParameter = id, Tag = false, ToolTip = name, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, "TileButton");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Update(s => { s.Image = id; s.Enabled = true; });
        return button;
    }

    /// <summary>Label, slider, and value in fixed columns so every adjustment lines up.</summary>
    private UIElement SliderRow(string label, double min, double max, double step, Func<BackgroundSettings, double> get, Action<BackgroundSettings, double> set, Func<double, string> format)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, Value = get(_working), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var value = new TextBlock { Text = format(get(_working)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        value.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            Update(s => set(s, e.NewValue));
        };
        _sliders.Add((slider, get));
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private void SyncControls()
    {
        _loading = true;
        _enabled.IsChecked = _working.Enabled;
        foreach (var (slider, get) in _sliders)
            slider.Value = get(_working);
        _position.SelectedItem = _working.Position.ToString();
        _fit.SelectedIndex = (int)_working.Fit;
        _removeCustom.IsEnabled = _working.CustomPath is not null;
        foreach (var tile in _gallery.Children.OfType<Button>())
            tile.Tag = (string)tile.CommandParameter == _working.Image;

        _loading = false;
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Title = "Choose a background picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp|All files|*.*" };
        if (dialog.ShowDialog(_dialog) != true)
            return;
        try
        {
            var stored = BackgroundService.ImportCustom(dialog.FileName, previous: null);
            _imported.Add(stored);
            Update(s => { s.Image = BackgroundCatalog.Custom; s.CustomPath = stored; s.Enabled = true; });
            BuildGallery();
            SyncControls();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Alert(_dialog, ex.Message, "Background", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemovePicture()
    {
        Update(s =>
        {
            if (s.Image == BackgroundCatalog.Custom)
                s.Image = BackgroundCatalog.Default;
            s.CustomPath = null;
        });
        BuildGallery();
        SyncControls();
    }

    private void ResetDefaults()
    {
        var keepCustom = _working.CustomPath;
        Update(s =>
        {
            var defaults = new BackgroundSettings { CustomPath = keepCustom };
            s.Enabled = defaults.Enabled;
            s.Image = defaults.Image;
            s.CustomPath = defaults.CustomPath;
            s.Opacity = defaults.Opacity;
            s.Blur = defaults.Blur;
            s.Desaturation = defaults.Desaturation;
            s.Brightness = defaults.Brightness;
            s.Overlay = defaults.Overlay;
            s.Fit = defaults.Fit;
            s.Position = defaults.Position;
        });
    }
}
