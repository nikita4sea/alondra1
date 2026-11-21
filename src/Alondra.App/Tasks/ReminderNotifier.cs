using System.Windows;
using System.Windows.Threading;
using Alondra.Core.Tasks;

namespace Alondra.App.Tasks;

/// <summary>
/// Checks for due reminders every 15 seconds while Alondra runs. A due reminder is marked delivered before it is shown,
/// so it is never shown twice. It appears in the window's reminder bar (with Snooze and Dismiss) and, when enabled,
/// as a Windows notification from the notification-area icon. Alondra has no background service: reminders that
/// come due while it is closed are shown the next time it starts.
/// </summary>
public sealed class ReminderNotifier : IDisposable
{
    private readonly TodoViewModel _model;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private System.Windows.Forms.NotifyIcon? _icon;
    private string? _lastTaskId;
    private bool _busy;

    public ReminderNotifier(TodoViewModel model)
    {
        _model = model;
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        if (_model.Board is null)
            return;
        _timer.Start();
        _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (_busy || _model.Board is not { } board)
            return;
        _busy = true;
        try
        {
            var due = board.DueReminders();
            foreach (var item in due)
            {
                await board.MarkReminderDeliveredAsync(item.Task.Id, item.Reminder.Id);
                _model.ShowReminder(item);
            }

            if (due.Count > 0 && _model.Preferences.NotificationsEnabled)
                Notify(due);
        }
        catch (TodoStorageException ex)
        {
            _model.Error(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Notify(IReadOnlyList<DueReminder> due)
    {
        try
        {
            _icon ??= CreateIcon();
            var first = due[0];
            _lastTaskId = first.Task.Id;
            var text = due.Count == 1
                ? first.Task.Title + (first.Task.DueDate is { } date ? " · due " + TodoFormat.Date(date, _model.Today) : "")
                : $"{first.Task.Title} and {due.Count - 1} more";
            _icon.ShowBalloonTip(10_000, "Alondra reminder", text, System.Windows.Forms.ToolTipIcon.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Notifications are optional. The in-window reminder bar still shows the reminder.
            System.Diagnostics.Debug.WriteLine("Notification not shown: " + ex.Message);
        }
    }

    private System.Windows.Forms.NotifyIcon CreateIcon()
    {
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Alondra",
            Icon = LoadIcon(),
            Visible = true,
        };
        icon.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.Invoke(OpenLast);
        icon.Click += (_, _) => Application.Current.Dispatcher.Invoke(OpenLast);
        return icon;
    }

    /// <summary>The app icon at the notification area's size (16 px at 100%, larger at higher DPI).</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/alondra.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (IOException)
        {
        }

        return System.Drawing.SystemIcons.Information;
    }

    private void OpenLast()
    {
        var window = Application.Current.MainWindow;
        if (window is not null)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }

        if (_lastTaskId is { } id)
            _model.OpenTask(id);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
