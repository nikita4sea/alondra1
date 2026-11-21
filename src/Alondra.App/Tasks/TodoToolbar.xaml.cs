using System.Windows;
using System.Windows.Controls;

namespace Alondra.App.Tasks;

public partial class TodoToolbar : UserControl
{
    public TodoToolbar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ImportantLabel.Visibility = ActualWidth < 640 ? Visibility.Collapsed : Visibility.Visible;
    }

    private TodoViewModel? Model => DataContext as TodoViewModel;
    private Window Owner => Window.GetWindow(this);

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            Model?.SetSort(SortBox.SelectedIndex);
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e) => Model?.ToggleSortDirection();

    private void StatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            Model?.SetStatusFilter(StatusBox.SelectedIndex);
    }

    private void ImportantFilter_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            model.SetImportantOnly(!model.ImportantOnly);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { IsAvailable: true } model || MoreButton.ContextMenu is not { } menu)
            return;
        menu.Items.Clear();
        menu.Items.Add(Menu("Print current view…", model.RequestPrint, "Ctrl+P"));
        menu.Items.Add(new Separator());
        var export = new MenuItem { Header = "Export" };
        export.Items.Add(Menu("All tasks and lists as JSON…", () => TodoDataActions.ExportJson(Owner, model)));
        export.Items.Add(Menu("All tasks as CSV (spreadsheet)…", () => TodoDataActions.ExportCsv(Owner, model)));
        export.Items.Add(Menu("All tasks as iCalendar (.ics)…", () => TodoDataActions.ExportIcs(Owner, model)));
        menu.Items.Add(export);
        menu.Items.Add(Menu("Import from JSON…", () => _ = TodoDataActions.ImportJsonAsync(Owner, model)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Back up now…", () => _ = TodoDataActions.BackupAsync(Owner, model)));
        menu.Items.Add(Menu("Restore from backup…", () => _ = TodoDataActions.RestoreAsync(Owner, model)));
        menu.Items.Add(Menu("Open backup folder", () => TodoDataActions.OpenBackupFolder(model)));
        menu.Items.Add(new Separator());
        var notify = new MenuItem { Header = "Windows notifications for reminders", IsCheckable = true, IsChecked = model.Preferences.NotificationsEnabled };
        notify.Click += (_, _) => model.SetNotifications(notify.IsChecked);
        menu.Items.Add(notify);
        var counts = new MenuItem { Header = "Show task counts", IsCheckable = true, IsChecked = model.ShowCounts };
        counts.Click += (_, _) => model.SetShowCounts(counts.IsChecked);
        menu.Items.Add(counts);
        menu.Items.Add(Menu("Keyboard shortcuts", () => TodoDialogs.Shortcuts(Owner)));
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem Menu(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }
}
