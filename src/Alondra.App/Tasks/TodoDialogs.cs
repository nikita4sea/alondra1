using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Alondra.Core.Tasks;

namespace Alondra.App.Tasks;

/// <summary>Small dialogs for the To-do workspace, built with the same helpers as the rest of Alondra's dialogs.</summary>
public static class TodoDialogs
{
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var window = Small(title, owner);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title(title));
        var field = Dialogs.Field(initial);
        AutomationName(field, label);
        panel.Children.Add(Dialogs.Labeled(label, field));
        string? result = null;
        var actions = Actions(window);
        var ok = Dialogs.Button("OK", () =>
        {
            if (field.Text.Trim().Length == 0)
                return;
            result = field.Text.Trim();
            window.DialogResult = true;
        });
        ok.IsDefault = true;
        actions.Children.Add(Cancel(window));
        Primary(ok);
        actions.Children.Add(ok);
        window.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        window.ShowDialog();
        return result;
    }

    public static bool Confirm(Window owner, string title, string message, string action)
    {
        var window = Small(title, owner);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title(title));
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var confirmed = false;
        var actions = Actions(window);
        var ok = Dialogs.Button(action, () => { confirmed = true; window.DialogResult = true; });
        ok.Style = (Style)Application.Current.FindResource("DangerButton");
        ok.Margin = new Thickness(0);
        var cancel = Cancel(window);
        cancel.IsDefault = true;
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        window.Loaded += (_, _) => cancel.Focus();
        window.ShowDialog();
        return confirmed;
    }

    public sealed record ListResult(string Name, string? Color, bool Tint, string? GroupId);

    public static ListResult? ListProperties(Window owner, TaskList list, IReadOnlyList<ListGroup> groups)
    {
        var window = Small("List", owner, 420);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title("List settings"));
        var name = Dialogs.Field(list.Name);
        AutomationName(name, "List name");
        panel.Children.Add(Dialogs.Labeled("Name (emoji welcome, for example 🛒 Groceries)", name));

        panel.Children.Add(new TextBlock { Text = "Color", Margin = new Thickness(0, 4, 0, 4) });
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var chosen = list.Color;
        var buttons = new List<RadioButton>();
        foreach (var (label, hex) in TodoFormat.Palette)
        {
            var swatch = new RadioButton
            {
                GroupName = "color",
                IsChecked = string.Equals(hex, list.Color, StringComparison.OrdinalIgnoreCase) || (hex is null && list.Color is null),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = label,
                Tag = hex,
                Cursor = Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, label);
            var dot = new Ellipse { Width = 20, Height = 20, StrokeThickness = 1 };
            dot.SetResourceReference(Shape.StrokeProperty, "LineBrush");
            if (hex is null)
                dot.SetResourceReference(Shape.FillProperty, "BlueBrush");
            else
                dot.Fill = TodoFormat.Brush(hex);
            swatch.Content = dot;
            swatch.Checked += (_, _) => chosen = hex;
            buttons.Add(swatch);
            swatches.Children.Add(swatch);
        }

        panel.Children.Add(swatches);
        var tint = Dialogs.Check("Tint the list background with this color", list.TintBackground);
        panel.Children.Add(tint);

        var groupBox = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        groupBox.Items.Add(new ComboBoxItem { Content = "No group", Tag = null });
        foreach (var group in groups)
            groupBox.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Id });
        groupBox.SelectedIndex = Math.Max(0, groups.ToList().FindIndex(group => group.Id == list.GroupId) + 1);
        AutomationName(groupBox, "Group");
        panel.Children.Add(Dialogs.Labeled("Group", groupBox));

        ListResult? result = null;
        var actions = Actions(window);
        var ok = Dialogs.Button("Save", () =>
        {
            result = new ListResult(name.Text.Trim().Length == 0 ? list.Name : name.Text.Trim(), chosen, tint.IsChecked == true && chosen is not null,
                (groupBox.SelectedItem as ComboBoxItem)?.Tag as string);
            window.DialogResult = true;
        });
        ok.IsDefault = true;
        actions.Children.Add(Cancel(window));
        Primary(ok);
        actions.Children.Add(ok);
        window.Loaded += (_, _) => name.Focus();
        window.ShowDialog();
        return result;
    }

    public static RecurrenceRule? CustomRecurrence(Window owner, RecurrenceRule? current)
    {
        var rule = current ?? new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly };
        var window = Small("Repeat", owner, 420);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title("Custom repeat"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = "Every", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var interval = new TextBox { Text = Math.Max(1, rule.Interval).ToString(CultureInfo.CurrentCulture), Width = 56 };
        AutomationName(interval, "Interval");
        row.Children.Add(interval);
        var unit = new ComboBox { ItemsSource = new[] { "day(s)", "week(s)", "month(s)", "year(s)" }, SelectedIndex = (int)rule.Frequency, Margin = new Thickness(8, 0, 0, 0), MinWidth = 110 };
        AutomationName(unit, "Unit");
        row.Children.Add(unit);
        panel.Children.Add(row);

        var days = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var checks = new List<CheckBox>();
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var check = new CheckBox { Content = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), Tag = day, IsChecked = rule.DaysOfWeek.Contains(day), Margin = new Thickness(0, 0, 10, 4) };
            check.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            checks.Add(check);
            days.Children.Add(check);
        }

        var dayLabel = new TextBlock { Text = "On these days (weekly only)", Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(dayLabel);
        panel.Children.Add(days);
        void SyncDays()
        {
            var weekly = unit.SelectedIndex == 1;
            days.IsEnabled = weekly;
            dayLabel.Opacity = days.Opacity = weekly ? 1 : 0.5;
        }

        unit.SelectionChanged += (_, _) => SyncDays();
        SyncDays();
        var until = Dialogs.Field(rule.Until?.ToString("d", CultureInfo.CurrentCulture) ?? "");
        AutomationName(until, "End date");
        panel.Children.Add(Dialogs.Labeled("End date (optional)", until));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        panel.Children.Add(error);

        RecurrenceRule? result = null;
        var actions = Actions(window);
        var ok = Dialogs.Button("Save", () =>
        {
            if (!int.TryParse(interval.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var every) || every is < 1 or > 999)
            {
                error.Text = "Enter a number from 1 to 999.";
                return;
            }

            DateOnly? end = null;
            if (until.Text.Trim().Length > 0 && (end = TodoFormat.ParseDate(until.Text, DateTime.Now)) is null)
            {
                error.Text = "The end date is not a date Alondra understands.";
                return;
            }

            result = new RecurrenceRule
            {
                Frequency = (RecurrenceFrequency)Math.Max(0, unit.SelectedIndex),
                Interval = every,
                DaysOfWeek = unit.SelectedIndex == 1 ? checks.Where(check => check.IsChecked == true).Select(check => (DayOfWeek)check.Tag).ToList() : [],
                Until = end,
            };
            window.DialogResult = true;
        });
        ok.IsDefault = true;
        actions.Children.Add(Cancel(window));
        Primary(ok);
        actions.Children.Add(ok);
        window.Loaded += (_, _) => { interval.Focus(); interval.SelectAll(); };
        window.ShowDialog();
        return result;
    }

    public static DateTime? CustomReminder(Window owner, DateTime suggested)
    {
        var window = Small("Reminder", owner);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title("Remind me"));
        var date = Dialogs.Field(suggested.ToString("d", CultureInfo.CurrentCulture));
        AutomationName(date, "Reminder date");
        var time = Dialogs.Field(suggested.ToString("t", CultureInfo.CurrentCulture));
        AutomationName(time, "Reminder time");
        panel.Children.Add(Dialogs.Labeled("Date (for example 12/24, tomorrow, fri)", date));
        panel.Children.Add(Dialogs.Labeled("Time (for example 09:00 or 2:30 PM)", time));
        panel.Children.Add(Dialogs.Note("Reminders use this PC's time zone and follow daylight-saving changes. They are shown while Alondra is running."));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        panel.Children.Add(error);
        DateTime? result = null;
        var actions = Actions(window);
        var ok = Dialogs.Button("Set reminder", () =>
        {
            var now = DateTime.Now;
            if (TodoFormat.ParseDate(date.Text, now) is not { } day || TodoFormat.ParseTime(time.Text, now) is not { } clock)
            {
                error.Text = "Enter a date and a time Alondra understands.";
                return;
            }

            result = day.ToDateTime(clock);
            window.DialogResult = true;
        });
        ok.IsDefault = true;
        actions.Children.Add(Cancel(window));
        Primary(ok);
        actions.Children.Add(ok);
        window.Loaded += (_, _) => date.Focus();
        window.ShowDialog();
        return result;
    }

    public static void Shortcuts(Window owner)
    {
        var window = Small("Keyboard shortcuts", owner, 460);
        var panel = Dialogs.Panel(window);
        panel.Children.Add(Dialogs.Title("To-do keyboard shortcuts"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new (string Key, string Action)[]
        {
            ("Ctrl+N", "Add a task"), ("Ctrl+Shift+N", "New list"), ("Ctrl+F or Ctrl+E", "Search all tasks"),
            ("Space or Ctrl+Enter", "Complete or reopen"), ("Ctrl+I", "Mark important"), ("Ctrl+T", "Add to My Day"),
            ("Ctrl+D", "Duplicate"), ("Del", "Delete (undo available)"), ("Ctrl+Z", "Undo"), ("Enter / F2", "Edit details / rename"),
            ("Alt+Up / Alt+Down", "Move task in My order"), ("Shift or Ctrl + click", "Select several tasks"), ("Ctrl+A", "Select all"),
            ("Alt+Left / Alt+Right", "Previous / next in Calendar"), ("Ctrl+P", "Print"), ("Esc", "Hide details"), ("Ctrl+1 … Ctrl+6", "Switch workspace"),
        };
        foreach (var (key, action) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keyText = new TextBlock { Text = key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 8, 2) };
            var actionText = new TextBlock { Text = action, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(keyText, grid.RowDefinitions.Count - 1);
            Grid.SetRow(actionText, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(actionText, 1);
            grid.Children.Add(keyText);
            grid.Children.Add(actionText);
        }

        panel.Children.Add(grid);
        var close = Dialogs.Button("Close", () => window.Close());
        close.IsCancel = true;
        close.IsDefault = true;
        Primary(close);
        Actions(window).Children.Add(close);
        window.ShowDialog();
    }

    private static Window Small(string title, Window owner, double width = 380)
    {
        var window = Dialogs.Create(title, owner, width, sizeToContent: true);
        window.ResizeMode = ResizeMode.NoResize;
        return window;
    }

    private static StackPanel Actions(Window window) => Dialogs.Footer(window);

    private static void Primary(Button button)
    {
        button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        button.Margin = new Thickness(0);
    }

    private static Button Cancel(Window window)
    {
        var cancel = Dialogs.Button("Cancel", () => window.DialogResult = false);
        cancel.IsCancel = true;
        return cancel;
    }

    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
