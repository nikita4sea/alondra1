using System.Windows;
using System.Windows.Controls;

namespace Alondra1.App.Controls;

/// <summary>
/// The one column layout of the message list, shared by the table header (From / Subject / Received) and every
/// message row, so their columns stay aligned at any width. Set <c>ui:MailColumns.Apply="True"</c> on a Grid.
/// </summary>
public static class MailColumns
{
    /// <summary>Unread-dot gutter, reserved on every row and on the header.</summary>
    public const double IndicatorWidth = 16;
    public const double FromWeight = 1;
    public const double SubjectWeight = 2.55;
    public const double AttachmentWidth = 18;
    public const double ReceivedWidth = 57;

    public static readonly DependencyProperty ApplyProperty = DependencyProperty.RegisterAttached(
        "Apply", typeof(bool), typeof(MailColumns), new PropertyMetadata(false, OnApplyChanged));

    public static bool GetApply(DependencyObject element) => (bool)element.GetValue(ApplyProperty);

    public static void SetApply(DependencyObject element, bool value) => element.SetValue(ApplyProperty, value);

    public static IReadOnlyList<GridLength> Widths { get; } =
    [
        new GridLength(IndicatorWidth),
        new GridLength(FromWeight, GridUnitType.Star),
        new GridLength(SubjectWeight, GridUnitType.Star),
        new GridLength(AttachmentWidth),
        new GridLength(ReceivedWidth),
    ];

    private static void OnApplyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Grid grid || e.NewValue is not true)
            return;
        grid.ColumnDefinitions.Clear();
        foreach (var width in Widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }
}
