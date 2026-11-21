using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Shape = System.Windows.Shapes.Shape;
using Path = System.IO.Path;
using Alondra.App.Shell;
using Alondra.App.ViewModels;
using Alondra.Core.Config;
using Alondra.Core.Layout;
using Alondra.Core.Mail;

namespace Alondra.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _filling;
    private bool _syncSelection;
    private bool _syncingSearch;
    private Alondra.App.Tasks.ReminderNotifier? _reminders;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        ShellWindow.Attach(this);
        FolderDockBox.ItemsSource = Enum.GetNames<DockEdge>();
        ReaderDockBox.ItemsSource = Enum.GetNames<DockEdge>();
        DensityBox.ItemsSource = Enum.GetNames<Density>();
        MailGrouping.SelectedIndex = model.Config.Appearance.GroupMailByAccount ? 1 : 0;
        SetGrouping(model.Messages, nameof(StoredMessage.AccountId), model.Config.Appearance.GroupMailByAccount);
        model.ComposeRequested += ShowCompose;
        model.SettingsRequested += () => Dialogs.ShowSettings(this, model);
        model.AccountRequested += account => Dialogs.ShowAccount(this, model, account);
        model.ConnectionChanged += UpdateStatusDot;
        model.Todo.SearchFocusRequested += FocusSearch;
        model.Todo.PropertyChanged += Todo_PropertyChanged;
        ApplyBackground();
        Loaded += async (_, _) =>
        {
            await model.InitializeAsync();
            await StartTasksAsync();
            FillSelectors();
            UpdateStatusDot();
            WriteLaunchLog();
            if (App.Arguments.Contains("--perf"))
                await RunPerfAsync();
            else if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--snapshot=", StringComparison.Ordinal)) is { } snapshot)
                await SnapshotAsync(snapshot["--snapshot=".Length..]);
            else if (model.Config.Accounts.Count == 0 && !model.IsDemo && !App.Arguments.Contains("--demo"))
                Dialogs.ShowWelcome(this, model);
        };
    }

    /// <summary>Applies Settings → Appearance → Background to the window. Also used for live preview.</summary>
    public void ApplyBackground() => BackgroundService.Apply(_model.Config.Appearance.Background, BackgroundImage, BackgroundOverlay);

    private static void SetGrouping(System.Collections.IEnumerable items, string property, bool enabled)
    {
        var view = CollectionViewSource.GetDefaultView(items);
        using var refresh = view.DeferRefresh();
        view.GroupDescriptions.Clear();
        if (enabled)
            view.GroupDescriptions.Add(new PropertyGroupDescription(property));
    }

    private void MailGrouping_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || MailGrouping.SelectedIndex < 0)
            return;
        var enabled = MailGrouping.SelectedIndex == 1;
        _model.Config.Appearance.GroupMailByAccount = enabled;
        SetGrouping(_model.Messages, nameof(StoredMessage.AccountId), enabled);
        _model.ConfigStoreSave();
    }

    private async Task StartTasksAsync()
    {
        var todo = _model.Todo;
        if (App.Services.Tasks is { } board && _model.IsDemo && board.Tasks.Count == 0)
            await Alondra.App.Tasks.TodoSamples.SeedAsync(board);
        await todo.InitializeAsync();
        _reminders = new Alondra.App.Tasks.ReminderNotifier(todo);
        _reminders.Start();
        Closed += (_, _) => _reminders?.Dispose();
        if (App.Services.TaskStore is { } store && !_model.IsDemo)
            _ = Alondra.App.Tasks.TodoDataActions.AutomaticBackupAsync(store);
    }

    private void FillSelectors()
    {
        _filling = true;
        AccountBox.ItemsSource = _model.AccountChoices;
        AccountBox.SelectedItem = _model.SelectedAccount;
        HiddenFolderBox.ItemsSource = _model.Folders;
        SyncFolderSelectors(_model.SelectedFolder);
        NoteBox.ItemsSource = _model.NoteTitles;
        NoteBox.SelectedItem = _model.SelectedNoteTitle;
        FolderDockBox.SelectedItem = _model.FoldersDock.ToString();
        ReaderDockBox.SelectedItem = _model.ReadingDock.ToString();
        DensityBox.SelectedItem = _model.Config.Appearance.Density.ToString();
        ApplyFolderOrientation();
        SyncSearchBox();
        _filling = false;
        MessageList.ItemsSource = _model.Messages;
        MessageList.SelectedItem = _model.SelectedMessage;
        MessageList_SizeChanged(MessageList, null!);
    }

    /// <summary>The folder pane is a vertical list when docked left or right, and a wrapping strip when docked at the bottom.</summary>
    private void ApplyFolderOrientation()
    {
        var panel = new FrameworkElementFactory(_model.FoldersAreBand ? typeof(WrapPanel) : typeof(StackPanel));
        FolderList.ItemsPanel = new ItemsPanelTemplate(panel);
    }

    private void UpdateStatusDot() => StatusDot.SetResourceReference(Shape.FillProperty, _model.ConnectionBrushKey);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => _model.RememberShellWidth(e.NewSize.Width);

    private void WriteLaunchLog()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alondra", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launch.log"), $"{DateTimeOffset.Now:O} launch {App.Services.Launch.ElapsedMilliseconds} ms{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private async Task RunPerfAsync()
    {
        var batch = new PersonalBatch();
        for (var i = 0; i < 10000; i++)
        {
            batch.Upserts.Add(new StoredMessage
            {
                AccountId = "perf",
                AccountName = "Perf",
                FolderId = "Inbox",
                Uid = i + 1,
                Subject = "Indexed message " + i,
                FromName = "Sender " + (i % 50),
                FromAddress = "sender@example.com",
                ReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                TimeLabel = "09:00",
                Preview = "Body token " + (i % 100),
                BodyDownloaded = true,
                Availability = ItemAvailability.Downloaded,
            });
        }

        var started = Stopwatch.StartNew();
        await App.Services.Store.CommitAsync(batch, CancellationToken.None);
        var commitMs = started.ElapsedMilliseconds;
        started.Restart();
        var found = await App.Services.Store.SearchAsync("perf", "Inbox", "token 7", CancellationToken.None);
        var searchMs = started.ElapsedMilliseconds;
        _model.Messages.Clear();
        foreach (var message in await App.Services.Store.ListAsync("perf", "Inbox", CancellationToken.None))
            _model.Messages.Add(message);
        var scroll = FindScrollViewer(MessageList);
        var frames = 0;
        var watch = Stopwatch.StartNew();
        EventHandler handler = (_, _) => frames++;
        System.Windows.Media.CompositionTarget.Rendering += handler;
        if (scroll is not null)
        {
            while (watch.ElapsedMilliseconds < 800)
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 240);
                await Task.Delay(16);
            }
        }

        System.Windows.Media.CompositionTarget.Rendering -= handler;
        var working = Process.GetCurrentProcess().WorkingSet64;
        var report = $"commit {commitMs} ms; search {found.Count} hits in {searchMs} ms; scroll frames {frames}; working set {working / (1024 * 1024)} MB";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alondra", "perf.txt");
        File.WriteAllText(path, report);
        if (App.Arguments.Contains("--exit"))
            Close();
    }

    /// <summary>
    /// Diagnostics: renders the window to a PNG (for UI review and visual checks), then closes when --exit is given.
    /// --open=&lt;module&gt; and --width=&lt;pixels&gt; pick what to capture. Popups are not part of the image.
    /// </summary>
    private async Task SnapshotAsync(string path)
    {
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--width=", StringComparison.Ordinal)) is { } width
            && double.TryParse(width["--width=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels))
            Width = pixels;
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--height=", StringComparison.Ordinal)) is { } height
            && double.TryParse(height["--height=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tall))
            Height = tall;
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--open=", StringComparison.Ordinal)) is { } open)
            OpenWorkspace(open["--open=".Length..]);
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--select=", StringComparison.Ordinal)) is { } select)
            _model.Todo.Select(select["--select=".Length..]);
        await Task.Delay(1500);
        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--detail", StringComparison.Ordinal)) is not null && _model.Todo.Items.FirstOrDefault() is { } first)
        {
            _model.Todo.ShowDetail(first.Id);
            await Task.Delay(600);
        }

        if (App.Arguments.FirstOrDefault(argument => argument.StartsWith("--dialog=", StringComparison.Ordinal)) is { } dialog)
        {
            Action show = dialog["--dialog=".Length..] switch
            {
                "compose" => () => Dialogs.ShowCompose(this, _model, null),
                "settings" => () => Dialogs.ShowSettings(this, _model),
                "background" => () => Dialogs.ShowSettings(this, _model, 1),
                "account" => () => Dialogs.ShowAccount(this, _model, null),
                "welcome" => () => Dialogs.ShowWelcome(this, _model),
                "list" => () => Alondra.App.Tasks.TodoDialogs.Prompt(this, "New list", "Name (emoji welcome)", "Untitled list"),
                "alert" => () => Dialogs.Alert(this, "Replace the account list with this file? Existing account modes stay as they are, and saved mail is not deleted.", "Alondra", MessageBoxButton.OKCancel),
                _ => () => { },
            };
            Shell.Snapshot.CaptureDialog(this, show, path);
        }
        else
        {
            Shell.Snapshot.RenderElement((FrameworkElement)Content, path);
            File.WriteAllText(path + ".txt", $"header {Header.ActualHeight:0} toolbar {Toolbar.ActualHeight:0} window {ActualWidth:0}x{ActualHeight:0}; {Shell.Snapshot.Measure(this)}");
        }

        if (App.Arguments.Contains("--exit"))
            Close();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    // ---------- Window chrome ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInteractive(source))
            return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or TextBox or ComboBox)
                return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        ShellWindow.ApplyCorners(this);
    }

    // ---------- Module navigation ----------

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string name })
            OpenWorkspace(name);
    }

    private void OpenWorkspace(string name)
    {
        _model.SelectWorkspace(name);
        LayoutButton.IsChecked = false;
        SearchToggle.IsChecked = false;
        FillSelectors();
    }

    private void NavMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = NavMoreButton.ContextMenu!;
        menu.Items.Clear();
        foreach (var workspace in _model.Workspaces.Where(item => item.InOverflow))
        {
            var item = new MenuItem { Header = workspace.Soon ? workspace.Name + " (soon)" : workspace.Name, Icon = new Alondra.App.Controls.AlondraIcon { Kind = workspace.Icon, IconSize = 16 } };
            var name = workspace.Name;
            item.Click += (_, _) => OpenWorkspace(name);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = NavMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------- Contextual search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RouteSearch(SearchBox.Text, from: SearchBox);

    private void CompactSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RouteSearch(CompactSearchBox.Text, from: CompactSearchBox);

    /// <summary>Sends the query to the module that owns it: mail search, task search, or contact filtering.</summary>
    private void RouteSearch(string text, TextBox from)
    {
        if (_filling || _syncingSearch)
            return;
        _syncingSearch = true;
        var other = from == SearchBox ? CompactSearchBox : SearchBox;
        if (other.Text != text)
            other.Text = text;
        _syncingSearch = false;
        if (_model.IsMail)
        {
            _model.ApplySearch(text);
            MessageList.SelectedItem = _model.SelectedMessage;
        }
        else if (_model.IsTasks)
            _model.Todo.ApplySearch(text);
        else if (_model.IsContacts)
            _model.ApplyContactQuery(text);
    }

    private void SyncSearchBox()
    {
        _syncingSearch = true;
        SearchBox.Text = _model.ModuleSearchText;
        CompactSearchBox.Text = _model.ModuleSearchText;
        _syncingSearch = false;
    }

    private void Todo_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Alondra.App.Tasks.TodoViewModel.SearchText) && _model.IsTasks && SearchBox.Text != _model.Todo.SearchText)
            SyncSearchBox();
    }

    private void FocusSearch()
    {
        if (!_model.HasSearch)
            return;
        if (_model.CompactSearch)
            SearchToggle.IsChecked = true;
        else
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box)
            return;
        e.Handled = true;
        if (box.Text.Length > 0)
            box.Clear();
        else
        {
            SearchToggle.IsChecked = false;
            Keyboard.ClearFocus();
        }
    }

    private void SearchToggle_Checked(object sender, RoutedEventArgs e) => SyncSearchBox();

    private void SearchPopup_Opened(object sender, EventArgs e) => Dispatcher.InvokeAsync(() => { CompactSearchBox.Focus(); CompactSearchBox.SelectAll(); });

    // ---------- Accounts and folders ----------

    private void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || AccountBox.SelectedItem is not string account)
            return;
        _model.SetAccount(account);
        MessageList.SelectedItem = _model.SelectedMessage;
        UpdateStatusDot();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ChooseFolder(FolderList.SelectedItem);
    private void HiddenFolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ChooseFolder(HiddenFolderBox.SelectedItem);
    private void ToolbarFolders_SelectionChanged(object sender, SelectionChangedEventArgs e) => ChooseFolder(ToolbarFolders.SelectedItem);
    private void ToolbarFolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ChooseFolder(ToolbarFolderBox.SelectedItem);

    /// <summary>All folder selectors (toolbar tabs, narrow toolbar selector, folder pane, list-header selector) show the same folder.</summary>
    private void ChooseFolder(object? selected)
    {
        if (_filling || _syncSelection || selected is not string folder)
            return;
        SyncFolderSelectors(folder);
        _model.SetFolder(folder);
        MessageList.SelectedItem = _model.SelectedMessage;
    }

    private void SyncFolderSelectors(string folder)
    {
        _syncSelection = true;
        FolderList.SelectedItem = folder;
        HiddenFolderBox.SelectedItem = folder;
        ToolbarFolders.SelectedItem = folder;
        ToolbarFolderBox.SelectedItem = folder;
        _syncSelection = false;
    }

    private void FolderDockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || FolderDockBox.SelectedItem is not string value || !Enum.TryParse<DockEdge>(value, out var edge))
            return;
        _model.SetDock(folders: true, edge);
        ApplyFolderOrientation();
        SyncFolderSelectors(_model.SelectedFolder);
    }

    private void ReaderDockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ReaderDockBox.SelectedItem is not string value || !Enum.TryParse<DockEdge>(value, out var edge))
            return;
        _model.SetDock(folders: false, edge);
    }

    private void DensityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || DensityBox.SelectedItem is not string value || !Enum.TryParse<Density>(value, out var density))
            return;
        _model.SetDensity(density);
        MessageList.Items.Refresh();
        MessageList_SizeChanged(MessageList, null!);
    }

    private void Dock_BreadthCommitted(object sender, BreadthChangedEventArgs e) => _model.CommitBreadth(e.PaneId, e.Breadth);

    // ---------- Mail ----------

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
            return;
        _model.SelectMessage(MessageList.SelectedItem as StoredMessage);
    }

    private void MessageList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewer = FindScrollViewer(MessageList);
        var height = viewer?.ViewportHeight ?? MessageList.ActualHeight;
        _model.UpdateViewport(MessageList.ActualWidth, height > 1 ? height : MessageList.ActualHeight);
    }

    /// <summary>Less common mail actions, plus the actions hidden from the toolbar when the window is narrow.</summary>
    private void MailMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = MailMoreButton.ContextMenu!;
        menu.Items.Clear();
        if (!_model.ShowSecondaryMailActions)
        {
            menu.Items.Add(Command("Reply all", "ReplyAll", _model.ReplyAllCommand));
            menu.Items.Add(Command("Forward", "Forward", _model.ForwardCommand, "Ctrl+Shift+F"));
            menu.Items.Add(Command("Archive", "Archive", _model.ArchiveCommand));
            menu.Items.Add(Command(_model.ReadToggleLabel, _model.ReadToggleIcon, _model.ToggleReadCommand));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Command("Sync now", "Refresh", _model.SyncCommand, "F5"));
        menu.Items.Add(Command("Show plain text", "Notes", null, null, () => PlainBody.Visibility = Visibility.Visible));
        menu.PlacementTarget = MailMoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem Command(string header, string icon, ICommand? command, string? gesture = null, Action? action = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            InputGestureText = gesture ?? "",
            Icon = new Alondra.App.Controls.AlondraIcon { Kind = icon, IconSize = 16 },
        };
        if (action is not null)
            item.Click += (_, _) => action();
        return item;
    }

    private void NoteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || NoteBox.SelectedItem is not string title)
            return;
        _model.SelectNote(title);
    }

    private void Note_LostFocus(object sender, RoutedEventArgs e) => _model.SaveNoteCommand.Execute(null);

    private void PlainText_Click(object sender, RoutedEventArgs e) => PlainBody.Visibility = Visibility.Visible;

    // ---------- Keyboard ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D6)
        {
            OpenWorkspace(_model.Workspaces[e.Key - Key.D1].Name);
            e.Handled = true;
            return;
        }

        // In To-do, task shortcuts apply and mail shortcuts (reply, forward, delete message) do not.
        if (_model.IsTasks)
        {
            if (TodoPanel.HandleKey(e))
                e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.F && _model.HasSearch)
        {
            FocusSearch();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is >= Key.D1 and <= Key.D5)
        {
            var folders = new[] { "Inbox", "Sent", "Drafts", "Archive", "Trash" };
            _model.SetFolder(folders[e.Key - Key.D1]);
            FillSelectors();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            _model.NewMailCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R)
        {
            _model.ReplyCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            _model.ForwardCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _model.SyncCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && MessageList.IsKeyboardFocusWithin)
        {
            _model.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ShowCompose(OutgoingMail? mail) => Dialogs.ShowCompose(this, _model, mail);
}
