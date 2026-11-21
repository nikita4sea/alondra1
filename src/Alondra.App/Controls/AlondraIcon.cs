using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace Alondra.App.Controls;

/// <summary>
/// Draws one icon from the Alondra icon family (Themes/Icons.xaml) as a stroked vector at 16, 20, or 24 px.
/// The stroke uses the inherited text color, so icons follow hover, selected, disabled, and theme changes like text.
/// Icons are decorative: put the accessible name on the button that contains them.
/// </summary>
public sealed class AlondraIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(AlondraIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(AlondraIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(AlondraIcon),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(AlondraIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(AlondraIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public AlondraIcon()
    {
        SetResourceReference(AccentProperty, "AccentWarmBrush");
        Focusable = false;
        SnapsToDevicePixels = false;
    }

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>True when the icon family defines <paramref name="kind"/>.</summary>
    public static bool Exists(string kind) => Application.Current?.TryFindResource("Icon." + kind) is Geometry;

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (string.IsNullOrEmpty(Kind) || TryFindResource("Icon." + Kind) is not Geometry geometry)
            return;
        var scale = IconSize / 24.0;
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        if (Accent is { } accent && TryFindResource("Icon." + Kind + ".Accent") is Geometry accentGeometry)
            drawingContext.DrawGeometry(accent, null, accentGeometry);
        if (TryFindResource("Icon." + Kind + ".Fill") is Geometry fill)
            drawingContext.DrawGeometry(Foreground, null, fill);
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }

    // Decorative: no automation peer, so screen readers read the containing button's name instead.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
