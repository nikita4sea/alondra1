using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Alondra1.App.Controls;

/// <summary>
/// Explains a disabled action: <c>ui:ActionHint.WhenDisabled="Select a message first"</c> adds the reason to the
/// tooltip (which then also shows while disabled) and to the accessible help text, and removes it again when enabled.
/// </summary>
public static class ActionHint
{
    public static readonly DependencyProperty WhenDisabledProperty = DependencyProperty.RegisterAttached(
        "WhenDisabled", typeof(string), typeof(ActionHint), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyProperty BaseToolTipProperty = DependencyProperty.RegisterAttached(
        "BaseToolTip", typeof(object), typeof(ActionHint), new PropertyMetadata(null));

    public static string? GetWhenDisabled(DependencyObject element) => (string?)element.GetValue(WhenDisabledProperty);

    public static void SetWhenDisabled(DependencyObject element, string? value) => element.SetValue(WhenDisabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        ToolTipService.SetShowOnDisabled(element, true);
        element.IsEnabledChanged -= Element_IsEnabledChanged;
        element.IsEnabledChanged += Element_IsEnabledChanged;
        element.Loaded -= Element_Loaded;
        element.Loaded += Element_Loaded;
        Apply(element);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply((FrameworkElement)sender);

    /// <summary>The tooltip text for an action, with the reason appended while it is disabled.</summary>
    public static string Describe(string? tip, string? reason, bool enabled) =>
        enabled || string.IsNullOrWhiteSpace(reason) ? tip ?? "" : string.IsNullOrWhiteSpace(tip) ? reason : $"{tip} — {reason}";

    private static void Apply(FrameworkElement element)
    {
        // A bound tooltip (for example "Mark as read" / "Mark as unread") stays bound; only the help text changes.
        if (System.Windows.Data.BindingOperations.IsDataBound(element, FrameworkElement.ToolTipProperty))
        {
            AutomationProperties.SetHelpText(element, element.IsEnabled ? "" : GetWhenDisabled(element) ?? "");
            return;
        }

        if (element.GetValue(BaseToolTipProperty) is null)
            element.SetValue(BaseToolTipProperty, element.ToolTip ?? "");
        var tip = element.GetValue(BaseToolTipProperty) as string;
        var reason = GetWhenDisabled(element);
        element.ToolTip = Describe(tip, reason, element.IsEnabled);
        AutomationProperties.SetHelpText(element, element.IsEnabled ? "" : reason ?? "");
    }
}
