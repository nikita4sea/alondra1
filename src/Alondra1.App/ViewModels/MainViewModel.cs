using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alondra1.Core.Config;
using Alondra1.Core.Demo;
using Alondra1.Core.Layout;
using Alondra1.Core.Mail;
using Alondra1.Core.Organizer;
using Alondra1.Core.Sync;
using Alondra1.Persistence;

namespace Alondra1.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, string> _folderIds = new(StringComparer.OrdinalIgnoreCase);
    private List<CalendarEntry> _events = [];
    private List<NoteEntry> _notes = [];
    private List<ContactEntry> _contacts = [];
    private DateTime _month = new(2026, 11, 1);
    private int _chosenDay = 15;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Workspaces =
        [
            new WorkspaceChoice("Mail", "\uE715", false, "Mail", "Search mail"),
            new WorkspaceChoice("Calendar", "\uE787", false, "Calendar", null),
            new WorkspaceChoice("To-do", "\uE9D5", false, "Todo", "Search tasks"),
            new WorkspaceChoice("Notes", "\uE70B", false, "Notes", null),
            new WorkspaceChoice("Contacts", "\uE77B", false, "Contacts", "Search contacts"),
            new WorkspaceChoice("Chat", "\uE8F2", true, "Chat", null),
        ];
        SelectWorkspaceCommand = new RelayCommand<string>(SelectWorkspace);
        NewMailCommand = new RelayCommand(() => ComposeRequested?.Invoke(null));
        ReplyCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildReply(all: false)), () => SelectedMessage is not null);
        ReplyAllCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildReply(all: true)), () => SelectedMessage is not null);
        ForwardCommand = new RelayCommand(() => ComposeRequested?.Invoke(BuildForward()), () => SelectedMessage is not null);
        DeleteCommand = new RelayCommand(async () => await DeleteAsync(), () => SelectedMessage is not null);
        ArchiveCommand = new RelayCommand(async () => await MoveSpecialAsync("Archive"), () => SelectedMessage is not null);
        ToggleReadCommand = new RelayCommand(async () => await ToggleReadAsync(), () => SelectedMessage is not null);
        SyncCommand = new RelayCommand(async () => await SyncAsync());
        PreviousMonthCommand = new RelayCommand(() => ShiftMonth(-1));
        NextMonthCommand = new RelayCommand(() => ShiftMonth(1));
        SelectDayCommand = new RelayCommand<CalendarDay>(SelectDay);
        ToggleTaskCommand = new RelayCommand<TaskRow>(async row => await ToggleTaskAsync(row));
        AddTaskCommand = new RelayCommand(async () => await AddTaskAsync());
        SaveNoteCommand = new RelayCommand(async () => await SaveNoteAsync());
        NewNoteCommand = new RelayCommand(async () => await NewNoteAsync());
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        AccountsCommand = new RelayCommand(() => AccountRequested?.Invoke(null));
        Todo = new Alondra1.App.Tasks.TodoViewModel(services, this);
    }

    public ObservableCollection<WorkspaceChoice> Workspaces { get; }
    public ObservableCollection<string> AccountChoices { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<StoredMessage> Messages { get; } = [];
    public ObservableCollection<CalendarDay> Days { get; } = [];
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<string> NoteTitles { get; } = [];
    public ObservableCollection<ContactEntry> VisibleContacts { get; } = [];

    public IRelayCommand<string> SelectWorkspaceCommand { get; }
    public IRelayCommand NewMailCommand { get; }
    public IRelayCommand ReplyCommand { get; }
    public IRelayCommand ReplyAllCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ArchiveCommand { get; }
    public IRelayCommand ToggleReadCommand { get; }
    public IRelayCommand SyncCommand { get; }
    public IRelayCommand PreviousMonthCommand { get; }
    public IRelayCommand NextMonthCommand { get; }
    public IRelayCommand<CalendarDay> SelectDayCommand { get; }
    public IRelayCommand<TaskRow> ToggleTaskCommand { get; }
    public IRelayCommand AddTaskCommand { get; }
    public IRelayCommand SaveNoteCommand { get; }
    public IRelayCommand NewNoteCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand AccountsCommand { get; }

    public event Action<OutgoingMail?>? ComposeRequested;
    public event Action? SettingsRequested;
    public event Action<AccountConfig?>? AccountRequested;

    public string WorkspaceName { get; private set; } = "Mail";
    public string WorkspaceGlyph { get; private set; } = "\uE715";
    public bool IsMail { get; private set; } = true;
    public bool IsCalendar { get; private set; }
    public bool IsTasks { get; private set; }
    public bool IsNotes { get; private set; }
    public bool IsContacts { get; private set; }
    public bool IsChat { get; private set; }
    public bool IsDemo => _services.Config.DemoMode && _services.Config.Accounts.Count == 0;
    public string DemoBadge => IsDemo ? "Demo" : "";
    public string StatusText { get; private set; } = "";
    public string SearchText { get; set; } = "";
    public string SelectedAccount { get; private set; } = "All accounts";
    public string SelectedFolder { get; private set; } = "Inbox";
    public bool FoldersHidden => FoldersDock == DockEdge.Hidden;
    public bool FoldersAreBand => FoldersDock is DockEdge.Top or DockEdge.Bottom;
    public DockEdge FoldersDock => _services.Config.Layout.FoldersDock;
    public DockEdge ReadingDock => _services.Config.Layout.ReadingDock;
    public double FolderBreadth => LayoutArranger.BreadthFor(FoldersDock, _services.Config.Layout.FolderBreadthSide, _services.Config.Layout.FolderBreadthBand);
    public double ReadingBreadth => LayoutArranger.BreadthFor(ReadingDock, _services.Config.Layout.ReadingBreadthSide, _services.Config.Layout.ReadingBreadthBand);
    public double RowHeight => _services.Config.Appearance.Density == Density.Compact ? 26 : 34;
    public string DensityLabel => _services.Config.Appearance.Density.ToString();
    public StoredMessage? SelectedMessage { get; private set; }
    public string ReaderStatus { get; private set; } = "Reading pane";
    public string ReaderSubject { get; private set; } = "No message selected";
    public string ReaderSender { get; private set; } = "";
    public string ReaderBody { get; private set; } = "";
    public string VisibleCountText { get; private set; } = "";
    public bool ShowTimeColumn { get; private set; } = true;
    public bool ShowAttachmentColumn { get; private set; } = true;
    public string MonthLabel { get; private set; } = "";
    public string AgendaText { get; private set; } = "";
    public string TaskCountText { get; private set; } = "";
    public string SelectedNoteTitle { get; private set; } = "";
    public string NoteBody { get; set; } = "";
    public string ContactQuery { get; set; } = "";
    public bool NoContacts { get; private set; }
    public string OrganizerNotice { get; private set; } = "";
    public string NewTaskTitle { get; set; } = "";
    public bool PlainTextPreferred { get; private set; } = true;
    public string? CurrentHtml { get; private set; }

    public AppConfig Config => _services.Config;

    /// <summary>The local To-do workspace (lists, smart views, calendar, reminders).</summary>
    public Alondra1.App.Tasks.TodoViewModel Todo { get; }

    /// <summary>The shared organizer pane is used by Calendar, Notes, Contacts, and Chat. To-do has its own view.</summary>
    public bool ShowOrganizer => !IsMail && !IsTasks;

    // ---------- Two-row shell: header search, module toolbar, connection status ----------

    public WorkspaceChoice CurrentWorkspace => Workspaces.First(item => item.IsSelected || item.Name == WorkspaceName);

    /// <summary>Calendar, Notes, and Chat have no search function, so the header search is hidden there.</summary>
    public bool HasSearch => CurrentWorkspace.SearchPlaceholder is not null;
    public string SearchPlaceholder => CurrentWorkspace.SearchPlaceholder ?? "";

    /// <summary>The query the header search box should show for the current module.</summary>
    public string ModuleSearchText => IsMail ? SearchText : IsTasks ? Todo.SearchText : IsContacts ? ContactQuery : "";

    /// <summary>Mail, Calendar, Notes, and Contacts are filtered by account; local To-do and Chat are not.</summary>
    public bool HasAccountSelector => IsMail || IsCalendar || IsNotes || IsContacts;

    /// <summary>With folders docked on top, they appear as tabs in the module toolbar instead of a separate pane.</summary>
    public bool ShowToolbarFolders => FoldersDock == DockEdge.Top;
    public bool ShowFolderCombo => FoldersDock == DockEdge.Hidden;
    public DockEdge PaneFoldersDock => FoldersDock == DockEdge.Top ? DockEdge.Hidden : FoldersDock;

    public string ReadToggleLabel => SelectedMessage?.IsUnread == true ? "Mark as read" : "Mark as unread";
    public string ReadToggleIcon => SelectedMessage?.IsUnread == true ? "MarkRead" : "Mail";

    public ConnectionState Connection { get; private set; } = ConnectionState.Ready;

    public string ConnectionLabel => Connection switch
    {
        ConnectionState.Sample => "Sample data",
        ConnectionState.NoAccounts => "No accounts",
        ConnectionState.Syncing => "Syncing…",
        ConnectionState.Offline => "Offline",
        ConnectionState.Error => "Sync failed",
        _ => "Up to date",
    };

    /// <summary>Resource key of the status dot color.</summary>
    public string ConnectionBrushKey => Connection switch
    {
        ConnectionState.Ready => "SuccessBrush",
        ConnectionState.Syncing => "BrandBrush",
        ConnectionState.Error => "DangerBrush",
        _ => "MutedBrush",
    };

    public string ConnectionAccessName => $"Connection: {ConnectionLabel}. {StatusText}";

    // ---------- Responsive header and toolbar (never wraps into another row) ----------

    public bool ShowWordmark { get; private set; } = true;
    public bool ShowNavLabels { get; private set; } = true;
    public bool CompactSearch { get; private set; }
    public bool ShowSearchBox => HasSearch && !CompactSearch;
    public bool ShowSearchButton => HasSearch && CompactSearch;
    public bool HasNavOverflow => Workspaces.Any(item => item.InOverflow);
    public bool ShowStatusLabel { get; private set; } = true;
    public bool ShowSecondaryMailActions { get; private set; } = true;
    public bool ShowFolderLabels { get; private set; } = true;
    public bool FolderTabsAsCombo { get; private set; }
    public bool ShowNewLabel { get; private set; } = true;

    /// <summary>
    /// Adapts the header and toolbar to the window width in steps: hide labels, shorten search, then move less-used
    /// modules into a More menu and collapse search to a button. Everything stays in its single row.
    /// </summary>
    public void SetShellWidth(double width)
    {
        ShowNavLabels = width >= 1120;
        ShowWordmark = width >= 640;
        CompactSearch = width < 720;
        ShowStatusLabel = width >= 1240;
        ShowSecondaryMailActions = width >= 1040;
        ShowFolderLabels = width >= 960;
        FolderTabsAsCombo = width < 760;
        ShowNewLabel = width >= 560;
        string[] overflow = width < 470 ? ["Calendar", "To-do", "Notes", "Contacts", "Chat"]
            : width < 600 ? ["Notes", "Contacts", "Chat"]
            : [];
        foreach (var workspace in Workspaces)
            workspace.InOverflow = overflow.Contains(workspace.Name) && !workspace.IsSelected;
        foreach (var name in new[]
        {
            nameof(ShowNavLabels), nameof(ShowWordmark), nameof(CompactSearch), nameof(ShowSearchBox), nameof(ShowSearchButton), nameof(HasNavOverflow),
            nameof(ShowStatusLabel), nameof(ShowSecondaryMailActions), nameof(ShowFolderLabels), nameof(FolderTabsAsCombo), nameof(ShowNewLabel),
        })
            OnPropertyChanged(name);
    }

    private double _shellWidth = 1180;

    public void RememberShellWidth(double width)
    {
        _shellWidth = width;
        SetShellWidth(width);
    }

    public async Task InitializeAsync()
    {
        if (IsDemo)
            _month = new DateTime(2026, 11, 1);
        else
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _chosenDay = IsDemo ? 15 : DateTime.Today.Day;
        RefreshAccountChoices();
        RefreshFolders();
        await ReloadMessagesAsync().ConfigureAwait(true);
        LoadOrganizerDemoOrEmpty();
        Todo.Refresh();
        UpdateStatus(IsDemo ? "Sample data" : "Ready");
        RaiseChrome();
    }

    public void SelectWorkspace(string? name)
    {
        name ??= "Mail";
        WorkspaceName = name;
        WorkspaceGlyph = Workspaces.First(item => item.Name == name).Glyph;
        IsMail = name == "Mail";
        IsCalendar = name == "Calendar";
        IsTasks = name == "To-do";
        IsNotes = name == "Notes";
        IsContacts = name == "Contacts";
        IsChat = name == "Chat";
        foreach (var workspace in Workspaces)
            workspace.IsSelected = workspace.Name == name;
        SetShellWidth(_shellWidth);
        RaiseChrome();
        if (!IsDemo && !IsMail && !IsChat && !IsTasks)
            _ = LoadOrganizerAsync();
    }

    public void SetAccount(string? account)
    {
        SelectedAccount = string.IsNullOrWhiteSpace(account) ? "All accounts" : account;
        _ = ReloadMessagesAsync();
        OnPropertyChanged(nameof(SelectedAccount));
    }

    public void SetFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        SelectedFolder = folder;
        _ = ReloadMessagesAsync();
        OnPropertyChanged(nameof(SelectedFolder));
    }

    public void ApplySearch(string? text)
    {
        SearchText = text ?? "";
        _ = ReloadMessagesAsync();
    }

    public void SelectMessage(StoredMessage? message)
    {
        SelectedMessage = message;
        if (message is null)
        {
            ReaderStatus = "Reading pane";
            ReaderSubject = "No message selected";
            ReaderSender = "";
            ReaderBody = "";
            CurrentHtml = null;
        }
        else
        {
            ReaderSubject = message.Subject;
            ReaderSender = $"{message.FromName} · {message.AccountName} · {message.TimeLabel}";
            ReaderStatus = message.SendState ?? AvailabilityLabel(message);
            ReaderBody = string.IsNullOrWhiteSpace(message.Preview) ? "" : message.Preview;
            CurrentHtml = null;
            _ = LoadBodyAsync(message);
        }

        ReplyCommand.NotifyCanExecuteChanged();
        ReplyAllCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ToggleReadCommand.NotifyCanExecuteChanged();
        RaiseReader();
    }

    public void UpdateViewport(double width, double height)
    {
        ShowTimeColumn = width > 285;
        ShowAttachmentColumn = width > 360;
        var fit = RowHeight <= 0 ? 0 : (int)Math.Floor(Math.Max(0, height) / RowHeight);
        VisibleCountText = $"{Math.Min(Messages.Count, Math.Max(0, fit))} of {Messages.Count} visible";
        OnPropertyChanged(nameof(ShowTimeColumn));
        OnPropertyChanged(nameof(ShowAttachmentColumn));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    public void SetDock(bool folders, DockEdge edge)
    {
        if (folders)
            _services.Config.Layout.FoldersDock = edge;
        else
            _services.Config.Layout.ReadingDock = edge;
        SaveConfig();
        RaiseLayout();
    }

    public void SetDensity(Density density)
    {
        _services.Config.Appearance.Density = density;
        SaveConfig();
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(DensityLabel));
    }

    public void CommitBreadth(string paneId, double breadth)
    {
        var layout = _services.Config.Layout;
        if (paneId == "folders")
        {
            if (layout.FoldersDock is DockEdge.Left or DockEdge.Right)
                layout.FolderBreadthSide = breadth;
            else
                layout.FolderBreadthBand = breadth;
        }
        else if (layout.ReadingDock is DockEdge.Left or DockEdge.Right)
            layout.ReadingBreadthSide = breadth;
        else
            layout.ReadingBreadthBand = breadth;
        SaveConfig();
        OnPropertyChanged(nameof(FolderBreadth));
        OnPropertyChanged(nameof(ReadingBreadth));
    }

    public async Task SyncAsync()
    {
        if (IsDemo)
        {
            UpdateStatus("Sample data");
            return;
        }

        UpdateStatus("Syncing…", ConnectionState.Syncing);
        foreach (var account in _services.Config.Accounts.ToList())
        {
            // The account may have been deleted while an earlier account was syncing.
            if (!_services.Config.Accounts.Contains(account))
                continue;
            var report = await _services.Sync.SyncAsync(account, CancellationToken.None).ConfigureAwait(true);
            UpdateStatus(report.Detail, report.Ok ? ConnectionState.Ready : report.Offline ? ConnectionState.Offline : ConnectionState.Error);
            if (report.DegradedDedup)
                Dialogs.Alert(report.Detail, "Alondra1", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    public void UpsertAccount(AccountConfig account, string? password)
    {
        var existing = _services.Config.Accounts.FirstOrDefault(item => string.Equals(item.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            _services.Config.Accounts.Add(account);
        else
        {
            var index = _services.Config.Accounts.IndexOf(existing);
            account.Mode = existing.Mode;
            _services.Config.Accounts[index] = account;
        }

        _services.Config.DemoMode = false;
        SaveConfig();
        if (password is not null)
        {
            var bundle = _services.Secrets.LoadAsync(account.Id, CancellationToken.None).GetAwaiter().GetResult();
            bundle.Password = password;
            _services.Secrets.SaveAsync(account.Id, bundle, CancellationToken.None).GetAwaiter().GetResult();
        }

        RefreshAccountChoices();
        _ = InitializeAsync();
    }

    public async Task<string> TestAccountAsync(AccountConfig account, string? password)
    {
        var bundle = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(password))
            bundle.Password = password;
        var incoming = await _services.Mail.ProbeIncomingAsync(account, bundle, CancellationToken.None).ConfigureAwait(true);
        var outgoing = await _services.Mail.ProbeOutgoingAsync(account, bundle, CancellationToken.None).ConfigureAwait(true);
        return incoming.Detail + "\n" + outgoing.Detail;
    }

    public void ApplyImport(AppConfig replacement, bool replace)
    {
        var plan = ConfigWorkflow.Preview(_services.Config, replacement);
        if (!plan.Ok || plan.Merged is null || plan.Replaced is null)
            return;
        var next = replace ? plan.Replaced : plan.Merged;
        next.DemoMode = _services.Config.DemoMode && next.Accounts.Count == 0;
        _services.Config.Appearance = next.Appearance;
        _services.Config.Layout = next.Layout;
        _services.Config.OAuth = next.OAuth;
        _services.Config.Accounts = next.Accounts;
        _services.Config.SchemaVersion = ConfigSchema.Current;
        SaveConfig();
        _services.RefreshOAuth();
        _ = InitializeAsync();
    }

    public string ExportTo(string path)
    {
        var json = ConfigWorkflow.ExportJson(_services.Config);
        File.WriteAllText(path, json);
        return json;
    }

    public (long Database, long Files) DiskUsage() =>
        (_services.Store.DatabaseBytesAsync(CancellationToken.None).GetAwaiter().GetResult(), _services.Files.TotalBytes());

    public async Task CleanupAttachmentsAsync(string accountId)
    {
        var items = await _services.Store.ListUnpinnedAttachmentsAsync(accountId, CancellationToken.None).ConfigureAwait(true);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.CacheRelativePath))
                continue;
            var full = _services.Files.Resolve(item.CacheRelativePath);
            if (File.Exists(full))
                File.Delete(full);
            await _services.Store.ForgetAttachmentFileAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public Task<int> PendingSendCountAsync(AccountConfig account) =>
        _services.Sync.PendingCountAsync(account, CancellationToken.None);

    /// <summary>
    /// Deletes an account from Alondra1: its settings, saved password and sign-in, local mail copy, outbox, and
    /// attachment files. Mail and organizer data on the server are not touched.
    /// </summary>
    public async Task DeleteAccountAsync(AccountConfig account)
    {
        // Remove the account from the configuration first so no new sync starts for it.
        _services.Config.Accounts.RemoveAll(item => string.Equals(item.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        SaveConfig();
        await _services.Sync.RemoveAccountAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        _services.Files.DeleteAccount(account.Id);
        if (SelectedMessage?.AccountId == account.Id)
            SelectMessage(null);
        await InitializeAsync().ConfigureAwait(true);
    }

    public async Task ChangeModeAsync(AccountConfig account, AccountMode target, bool discardPending)
    {
        var pending = await _services.Sync.PendingCountAsync(account, CancellationToken.None).ConfigureAwait(true);
        var kind = ModeTransition.Evaluate(account.Mode, target, pending);
        if (kind == ModeTransitionKind.MustResolveOrDiscardPending && !discardPending)
            throw new InvalidOperationException("Resolve or discard the outbox before leaving Personal mode.");
        if (kind == ModeTransitionKind.ReviewLocalThenRemove && discardPending)
            await _services.Sync.DiscardPendingAsync(account, CancellationToken.None).ConfigureAwait(true);
        if (kind == ModeTransitionKind.MustResolveOrDiscardPending && discardPending)
            await _services.Sync.DiscardPendingAsync(account, CancellationToken.None).ConfigureAwait(true);
        account.Mode = target;
        SaveConfig();
        if (target == AccountMode.Personal)
            await _services.Sync.SyncAsync(account, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    public void SelectDay(CalendarDay? day)
    {
        if (day is not { IsDay: true })
            return;
        _chosenDay = day.Day;
        BuildCalendar();
    }

    public void ApplyContactQuery(string? query)
    {
        ContactQuery = query ?? "";
        RebuildContacts();
    }

    public void SelectNote(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;
        SelectedNoteTitle = title;
        NoteBody = _notes.FirstOrDefault(note => note.Title == title)?.Body ?? "";
        OnPropertyChanged(nameof(SelectedNoteTitle));
        OnPropertyChanged(nameof(NoteBody));
    }

    private async Task ReloadMessagesAsync()
    {
        IReadOnlyList<StoredMessage> found;
        if (IsDemo)
            found = SampleCatalog.Query(SelectedFolder, SelectedAccount, SearchText);
        else
        {
            var list = new List<StoredMessage>();
            foreach (var account in AccountsInView())
            {
                var folderId = ResolveFolder(account, SelectedFolder);
                if (account.Mode == AccountMode.Personal)
                {
                    var rows = string.IsNullOrWhiteSpace(SearchText)
                        ? await _services.Store.ListAsync(account.Id, folderId, CancellationToken.None).ConfigureAwait(true)
                        : await _services.Store.SearchAsync(account.Id, folderId, SearchText, CancellationToken.None).ConfigureAwait(true);
                    list.AddRange(rows);
                }
                else
                {
                    var rows = string.IsNullOrWhiteSpace(SearchText)
                        ? _services.Cache.List(account.Id, folderId)
                        : _services.Cache.Search(account.Id, folderId, SearchText);
                    list.AddRange(rows);
                }
            }

            found = list.OrderByDescending(item => item.ReceivedUtc).ToList();
        }

        Messages.Clear();
        foreach (var message in found)
            Messages.Add(message);
        SelectMessage(Messages.FirstOrDefault());
        OnPropertyChanged(nameof(Messages));
    }

    private async Task LoadBodyAsync(StoredMessage message)
    {
        if (IsDemo)
        {
            ReaderBody = SampleCatalog.BodyFor(message).Text;
            RaiseReader();
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId);
        if (account is null)
            return;
        if (account.Mode == AccountMode.Personal)
        {
            var body = await _services.Store.GetBodyAsync(new MessageKey(message.AccountId, message.FolderId, message.Uid), CancellationToken.None).ConfigureAwait(true);
            if (body is null)
                return;
            ReaderBody = body.Text;
            CurrentHtml = body.Html;
        }
        else
        {
            var cached = _services.Cache.GetBody(new MessageKey(message.AccountId, message.FolderId, message.Uid));
            if (cached is null && message.Uid > 0)
            {
                var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                cached = await _services.Mail.FetchBodyAsync(account, secrets, message.FolderId, message.Uid, CancellationToken.None).ConfigureAwait(true);
                _services.Cache.SetBody(new MessageKey(message.AccountId, message.FolderId, message.Uid), cached);
            }

            if (cached is not null)
            {
                ReaderBody = cached.Text;
                CurrentHtml = cached.Html;
            }
        }

        RaiseReader();
    }

    private async Task DeleteAsync() => await MoveSpecialAsync("Trash");

    private async Task MoveSpecialAsync(string folder)
    {
        var message = SelectedMessage;
        if (message is null)
            return;
        if (IsDemo)
        {
            Messages.Remove(message);
            SelectMessage(Messages.FirstOrDefault());
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId);
        if (account is null)
            return;
        var command = new RemoteMailCommand
        {
            Kind = folder == "Trash" ? MailCommandKind.Delete : MailCommandKind.Archive,
            FolderId = message.FolderId,
            Uid = message.Uid,
            DestinationFolderId = ResolveFolder(account, folder),
        };
        await _services.Sync.ApplyLocalCommandAsync(account, command, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    private async Task ToggleReadAsync()
    {
        var message = SelectedMessage;
        if (message is null)
            return;
        message.IsUnread = !message.IsUnread;
        if (!IsDemo)
        {
            var account = _services.Config.Accounts.First(item => item.Id == message.AccountId);
            await _services.Sync.ApplyLocalCommandAsync(account, new RemoteMailCommand
            {
                Kind = MailCommandKind.SetRead,
                FolderId = message.FolderId,
                Uid = message.Uid,
                Value = !message.IsUnread,
            }, CancellationToken.None).ConfigureAwait(true);
        }

        SelectMessage(message);
    }

    private void LoadOrganizerDemoOrEmpty()
    {
        if (IsDemo)
        {
            _events = SampleCatalog.Events().ToList();
            Tasks.Clear();
            foreach (var task in SampleCatalog.Tasks())
                Tasks.Add(new TaskRow(task));
            _notes = SampleCatalog.Notes().ToList();
            _contacts = SampleCatalog.Contacts().ToList();
            OrganizerNotice = "Sample data · Work";
        }
        else
        {
            _events = [];
            Tasks.Clear();
            _notes = [];
            _contacts = [];
            OrganizerNotice = _services.Config.Accounts.Count == 0
                ? "Add a Microsoft 365 or Google account to use this view."
                : "";
        }

        NoteTitles.Clear();
        foreach (var note in _notes)
            NoteTitles.Add(note.Title);
        SelectNote(_notes.FirstOrDefault()?.Title);
        RebuildContacts();
        BuildCalendar();
        UpdateTaskCount();
    }

    private async Task LoadOrganizerAsync()
    {
        var account = OrganizerAccount();
        if (account is null)
        {
            OrganizerNotice = OrganizerCopy.ManualUnavailable;
            OnPropertyChanged(nameof(OrganizerNotice));
            return;
        }

        try
        {
            var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
            secrets = await _services.OAuth.EnsureFreshAsync(account, secrets, account.Kind == AccountKind.Gmail ? Alondra1.Core.Security.TokenAudience.Google : Alondra1.Core.Security.TokenAudience.Graph, CancellationToken.None).ConfigureAwait(true);
            await _services.Secrets.SaveAsync(account.Id, secrets, CancellationToken.None).ConfigureAwait(true);
            if (IsCalendar)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Calendar, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? $"{status.AccountName}";
                if (status.Available)
                {
                    var start = new DateTimeOffset(_month.Year, _month.Month, 1, 0, 0, 0, TimeSpan.Zero);
                    _events = (await _services.Organizer.ListEventsAsync(account, secrets, start, start.AddMonths(1), CancellationToken.None).ConfigureAwait(true)).ToList();
                    BuildCalendar();
                }
            }
            else if (IsTasks)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Tasks, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? status.AccountName;
                Tasks.Clear();
                if (status.Available)
                {
                    foreach (var task in await _services.Organizer.ListTasksAsync(account, secrets, CancellationToken.None).ConfigureAwait(true))
                        Tasks.Add(new TaskRow(task));
                }

                UpdateTaskCount();
            }
            else if (IsNotes)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Notes, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? OrganizerCopy.NotesAreAppNotes;
                if (status.Available)
                {
                    _notes = (await _services.Organizer.ListNotesAsync(account, secrets, CancellationToken.None).ConfigureAwait(true)).ToList();
                    NoteTitles.Clear();
                    foreach (var note in _notes)
                        NoteTitles.Add(note.Title);
                    SelectNote(_notes.FirstOrDefault()?.Title);
                }
            }
            else if (IsContacts)
            {
                var status = await _services.Organizer.GetStatusAsync(account, secrets, OrganizerFeature.Contacts, CancellationToken.None).ConfigureAwait(true);
                OrganizerNotice = status.Reason ?? status.AccountName;
                if (status.Available)
                    _contacts = (await _services.Organizer.ListContactsAsync(account, secrets, CancellationToken.None).ConfigureAwait(true)).ToList();
                RebuildContacts();
            }
        }
        catch (Exception ex)
        {
            OrganizerNotice = ex.Message;
        }

        OnPropertyChanged(nameof(OrganizerNotice));
    }

    private async Task ToggleTaskAsync(TaskRow? row)
    {
        if (row is null)
            return;
        UpdateTaskCount();
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        row.Source.Completed = row.Done;
        await _services.Organizer.SaveTaskAsync(account, secrets, row.Source, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task AddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle))
            return;
        var entry = new TaskEntry { Id = Guid.NewGuid().ToString("n"), Title = NewTaskTitle.Trim(), AccountName = IsDemo ? "Work" : "" };
        Tasks.Add(new TaskRow(entry));
        NewTaskTitle = "";
        OnPropertyChanged(nameof(NewTaskTitle));
        UpdateTaskCount();
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Organizer.SaveTaskAsync(account, secrets, entry, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task SaveNoteAsync()
    {
        var note = _notes.FirstOrDefault(item => item.Title == SelectedNoteTitle);
        if (note is null)
            return;
        note.Body = NoteBody;
        if (IsDemo)
            return;
        var account = OrganizerAccount();
        if (account is null)
            return;
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Organizer.SaveNoteAsync(account, secrets, note, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task NewNoteAsync()
    {
        var note = new NoteEntry { Id = Guid.NewGuid().ToString("n"), Title = "New note", Body = "", Revision = "1", UpdatedUtc = DateTimeOffset.UtcNow };
        _notes.Add(note);
        NoteTitles.Add(note.Title);
        SelectNote(note.Title);
        if (!IsDemo)
            await SaveNoteAsync().ConfigureAwait(true);
    }

    private void ShiftMonth(int delta)
    {
        _month = _month.AddMonths(delta);
        _chosenDay = 1;
        if (!IsDemo && IsCalendar)
            _ = LoadOrganizerAsync();
        else
            BuildCalendar();
    }

    private void BuildCalendar()
    {
        Days.Clear();
        MonthLabel = _month.ToString("MMMM yyyy");
        foreach (var name in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            Days.Add(new CalendarDay { Text = name, AccessName = name });
        var first = new DateTime(_month.Year, _month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        for (var blank = 0; blank < offset; blank++)
            Days.Add(new CalendarDay { Text = "" });
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (var day = 1; day <= days; day++)
        {
            var events = _events.Where(item => item.Start.Year == _month.Year && item.Start.Month == _month.Month && item.Start.Day == day).ToList();
            var label = events.Count == 0 ? "" : string.Join(", ", events.Select(item => item.Start.ToLocalTime().ToString("HH:mm") + " · " + item.Subject));
            Days.Add(new CalendarDay
            {
                Text = day.ToString(),
                Day = day,
                IsDay = true,
                HasEvent = events.Count > 0,
                IsSelected = day == _chosenDay,
                AccessName = string.IsNullOrEmpty(label) ? $"{day} {MonthLabel}" : $"{day} {MonthLabel}, {label}",
            });
        }

        var chosen = _events.Where(item => item.Start.Year == _month.Year && item.Start.Month == _month.Month && item.Start.Day == _chosenDay).ToList();
        var date = new DateTime(_month.Year, _month.Month, Math.Min(_chosenDay, days));
        AgendaText = date.ToString("ddd, MMM d") + " · " + (chosen.Count == 0 ? "No events" : string.Join("; ", chosen.Select(item => item.Start.ToLocalTime().ToString("HH:mm") + " · " + item.Subject + (string.IsNullOrEmpty(item.AccountName) ? "" : " · " + item.AccountName))));
        OnPropertyChanged(nameof(MonthLabel));
        OnPropertyChanged(nameof(AgendaText));
    }

    private void RebuildContacts()
    {
        VisibleContacts.Clear();
        foreach (var contact in _contacts.Where(item => string.IsNullOrWhiteSpace(ContactQuery) || $"{item.Name} {item.Email}".Contains(ContactQuery, StringComparison.OrdinalIgnoreCase)))
            VisibleContacts.Add(contact);
        NoContacts = VisibleContacts.Count == 0;
        OnPropertyChanged(nameof(NoContacts));
    }

    private void UpdateTaskCount()
    {
        TaskCountText = $"{Tasks.Count(task => !task.Done)} open";
        OnPropertyChanged(nameof(TaskCountText));
    }

    private void RefreshAccountChoices()
    {
        AccountChoices.Clear();
        AccountChoices.Add("All accounts");
        if (IsDemo)
        {
            AccountChoices.Add("Work");
            AccountChoices.Add("Personal");
        }
        else
        {
            foreach (var account in _services.Config.Accounts)
                AccountChoices.Add(account.DisplayName);
        }

        SelectedAccount = "All accounts";
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBadge));
    }

    private void RefreshFolders()
    {
        Folders.Clear();
        foreach (var folder in new[] { "Inbox", "Sent", "Drafts", "Archive", "Trash" })
            Folders.Add(folder);
        SelectedFolder = "Inbox";
        OnPropertyChanged(nameof(SelectedFolder));
    }

    private IEnumerable<AccountConfig> AccountsInView()
    {
        if (SelectedAccount == "All accounts")
            return _services.Config.Accounts;
        return _services.Config.Accounts.Where(account => account.DisplayName == SelectedAccount);
    }

    private AccountConfig? OrganizerAccount()
    {
        var named = SelectedAccount == "All accounts" ? null : SelectedAccount;
        return _services.Config.Accounts.FirstOrDefault(account =>
            account.Kind != AccountKind.Manual && (named is null || account.DisplayName == named))
            ?? _services.Config.Accounts.FirstOrDefault(account => account.Kind != AccountKind.Manual);
    }

    private string ResolveFolder(AccountConfig account, string display) =>
        _folderIds.TryGetValue(account.Id + "|" + display, out var id) ? id : display;

    private OutgoingMail? BuildReply(bool all)
    {
        var message = SelectedMessage;
        if (message is null)
            return null;
        return new OutgoingMail
        {
            To = [message.FromAddress],
            Subject = message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? message.Subject : "Re: " + message.Subject,
            TextBody = $"\n\n---\n{ReaderBody}",
            InReplyTo = message.MessageId,
        };
    }

    private OutgoingMail? BuildForward()
    {
        var message = SelectedMessage;
        if (message is null)
            return null;
        return new OutgoingMail
        {
            Subject = message.Subject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase) ? message.Subject : "Fw: " + message.Subject,
            TextBody = $"\n\n---\nFrom: {message.FromName}\n{ReaderBody}",
        };
    }

    private void UpdateStatus(string detail, ConnectionState? state = null)
    {
        if (IsDemo)
        {
            StatusText = "Demo · Sample data";
            Connection = ConnectionState.Sample;
        }
        else if (_services.Config.Accounts.Count == 0)
        {
            StatusText = "No accounts";
            Connection = ConnectionState.NoAccounts;
        }
        else
        {
            var account = AccountsInView().FirstOrDefault() ?? _services.Config.Accounts[0];
            StatusText = $"{account.DisplayName} · {account.Mode} · {detail}";
            Connection = state ?? (Connection is ConnectionState.Sample or ConnectionState.NoAccounts ? ConnectionState.Ready : Connection);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionBrushKey));
        OnPropertyChanged(nameof(ConnectionAccessName));
        ConnectionChanged?.Invoke();
    }

    /// <summary>Raised when the connection state changes, so the view can recolor the status dot.</summary>
    public event Action? ConnectionChanged;

    private static string AvailabilityLabel(StoredMessage message) => message.Availability switch
    {
        ItemAvailability.Downloaded => message.LocalOnly ? "On this PC" : "Available offline",
        ItemAvailability.RemoteOnly => "On the server",
        ItemAvailability.Pending => "Pending",
        ItemAvailability.Failed => "Failed",
        _ => "Reading pane",
    };

    private void SaveConfig() => _services.SaveConfig();

    private void RaiseChrome()
    {
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspaceGlyph));
        OnPropertyChanged(nameof(IsMail));
        OnPropertyChanged(nameof(IsCalendar));
        OnPropertyChanged(nameof(IsTasks));
        OnPropertyChanged(nameof(IsNotes));
        OnPropertyChanged(nameof(IsContacts));
        OnPropertyChanged(nameof(IsChat));
        OnPropertyChanged(nameof(ShowOrganizer));
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(ShowSearchBox));
        OnPropertyChanged(nameof(ShowSearchButton));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(HasAccountSelector));
        OnPropertyChanged(nameof(CurrentWorkspace));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBadge));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(ShowToolbarFolders));
        OnPropertyChanged(nameof(ShowFolderCombo));
        OnPropertyChanged(nameof(PaneFoldersDock));
        OnPropertyChanged(nameof(FoldersDock));
        OnPropertyChanged(nameof(ReadingDock));
        OnPropertyChanged(nameof(FolderBreadth));
        OnPropertyChanged(nameof(ReadingBreadth));
        OnPropertyChanged(nameof(FoldersHidden));
        OnPropertyChanged(nameof(FoldersAreBand));
        OnPropertyChanged(nameof(RowHeight));
    }

    private void RaiseReader()
    {
        OnPropertyChanged(nameof(ReadToggleLabel));
        OnPropertyChanged(nameof(ReadToggleIcon));
        OnPropertyChanged(nameof(SelectedMessage));
        OnPropertyChanged(nameof(ReaderStatus));
        OnPropertyChanged(nameof(ReaderSubject));
        OnPropertyChanged(nameof(ReaderSender));
        OnPropertyChanged(nameof(ReaderBody));
        OnPropertyChanged(nameof(CurrentHtml));
        OnPropertyChanged(nameof(PlainTextPreferred));
    }

    public void ConfigStoreSave() => SaveConfig();

    /// <summary>Loads To Do or Google Tasks for the "Account tasks" view. Local tasks never call a provider.</summary>
    public Task RefreshAccountTasksAsync() => IsDemo || !IsTasks ? Task.CompletedTask : LoadOrganizerAsync();

    public void ServicesRefresh() => _services.RefreshOAuth();

    public async Task SaveDraft(AccountConfig account, OutgoingMail mail)
    {
        mail.FromAddress = account.Address;
        mail.FromName = account.DisplayName;
        await _services.Sync.SaveDraftAsync(account, mail, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    public async Task<SendResult> Send(AccountConfig account, OutgoingMail mail)
    {
        mail.FromAddress = account.Address;
        mail.FromName = account.DisplayName;
        var result = await _services.Sync.SendAsync(account, mail, CancellationToken.None).ConfigureAwait(true);
        await ReloadMessagesAsync().ConfigureAwait(true);
        UpdateStatus(result.Acceptance + (result.Error is null ? "" : " · " + result.Error));
        return result;
    }

    public void RememberFolders(string accountId, IEnumerable<(string Display, string Id)> folders)
    {
        foreach (var (display, id) in folders)
            _folderIds[accountId + "|" + display] = id;
    }
}

public sealed class WorkspaceChoice : ObservableObject
{
    public WorkspaceChoice(string name, string glyph, bool soon, string icon, string? searchPlaceholder)
    {
        Name = name;
        Glyph = glyph;
        Soon = soon;
        Icon = icon;
        SearchPlaceholder = searchPlaceholder;
    }

    public string Name { get; }
    public string Glyph { get; }
    public bool Soon { get; }

    /// <summary>Alondra1 icon name (Themes/Icons.xaml).</summary>
    public string Icon { get; }

    /// <summary>Placeholder of the header search for this module, or null when the module has no search.</summary>
    public string? SearchPlaceholder { get; }

    public string AccessName => Soon ? Name + ", coming soon" : Name;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _inOverflow;

    /// <summary>True when the window is too narrow and this module is listed in the header's More menu instead.</summary>
    public bool InOverflow
    {
        get => _inOverflow;
        set => SetProperty(ref _inOverflow, value);
    }
}

/// <summary>Connection state shown next to the account selector (replaces the old status row).</summary>
public enum ConnectionState { Sample, NoAccounts, Ready, Syncing, Offline, Error }

public sealed class CalendarDay
{
    public string Text { get; init; } = "";
    public int Day { get; init; }
    public bool IsDay { get; init; }
    public bool HasEvent { get; init; }
    public bool IsSelected { get; init; }
    public string AccessName { get; init; } = "";
}

public sealed class TaskRow : ObservableObject
{
    public TaskRow(TaskEntry source)
    {
        Source = source;
        _done = source.Completed;
    }

    public TaskEntry Source { get; }
    public string Title => Source.Title;
    public string Folder => string.IsNullOrWhiteSpace(Source.ListName) ? "Tasks" : Source.ListName;
    private bool _done;
    public bool Done
    {
        get => _done;
        set => SetProperty(ref _done, value);
    }
}

public sealed class AppServices
{
    public required AppConfig Config { get; set; }
    public required ConfigFileStore ConfigStore { get; init; }
    public required SqlitePersonalStore Store { get; init; }
    public required DpapiSecretStore Secrets { get; init; }
    public required AttachmentFiles Files { get; init; }
    public required Alondra1.Mail.MailKitGateway Mail { get; init; }
    public required Alondra1.Providers.OAuthService OAuth { get; set; }
    public required Alondra1.Providers.OrganizerGateway Organizer { get; set; }
    public required Alondra1.Core.Sync.SyncCoordinator Sync { get; set; }
    public required Alondra1.Core.Ports.SessionCache Cache { get; init; }

    /// <summary>Local tasks (todo.db). Null only when the file could not be opened; <see cref="TaskStoreError"/> says why.</summary>
    public SqliteTaskStore? TaskStore { get; init; }
    public TaskFileStore? TaskFiles { get; init; }
    public Alondra1.Core.Tasks.TaskBoard? Tasks { get; init; }
    public string? TaskStoreError { get; init; }

    /// <summary>Alondra1's data folder (%LOCALAPPDATA%\alondra1, or a temporary folder in demo mode).</summary>
    public string Home { get; init; } = "";
    public Stopwatch Launch { get; } = Stopwatch.StartNew();

    public void SaveConfig() => ConfigStore.Save(Config);

    public void RefreshOAuth()
    {
        OAuth = new Alondra1.Providers.OAuthService(Config.OAuth);
        Organizer = new Alondra1.Providers.OrganizerGateway(Config.OAuth);
        Sync = new SyncCoordinator(Mail, Store, Cache, Secrets, OAuth, new Alondra1.Core.Ports.SystemClock(), new NullConflicts());
    }

    public static async Task<AppServices> CreateAsync(string? home = null)
    {
        var root = home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alondra1");
        Directory.CreateDirectory(root);
        var configStore = new ConfigFileStore(root);
        var config = configStore.Load();
        var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var secrets = new DpapiSecretStore(Path.Combine(root, "secrets"));
        var files = new AttachmentFiles(Path.Combine(root, "files"));
        var cache = new Alondra1.Core.Ports.SessionCache();
        var mail = new Alondra1.Mail.MailKitGateway();
        var oauth = new Alondra1.Providers.OAuthService(config.OAuth);
        var organizer = new Alondra1.Providers.OrganizerGateway(config.OAuth);
        var sync = new SyncCoordinator(mail, store, cache, secrets, oauth, new Alondra1.Core.Ports.SystemClock(), new NullConflicts());
        var (taskStore, taskFiles, board, taskError) = await OpenTasksAsync(root).ConfigureAwait(false);
        return new AppServices
        {
            Home = root,
            TaskStore = taskStore,
            TaskFiles = taskFiles,
            Tasks = board,
            TaskStoreError = taskError,
            Config = config,
            ConfigStore = configStore,
            Store = store,
            Secrets = secrets,
            Files = files,
            Mail = mail,
            OAuth = oauth,
            Organizer = organizer,
            Sync = sync,
            Cache = cache,
        };
    }

    /// <summary>
    /// Opens the local task database. A failure (for example a file written by a newer Alondra1) disables only the
    /// To-do workspace; mail and the other workspaces keep working.
    /// </summary>
    private static async Task<(SqliteTaskStore?, TaskFileStore?, Alondra1.Core.Tasks.TaskBoard?, string?)> OpenTasksAsync(string root)
    {
        var taskStore = new SqliteTaskStore(Path.Combine(root, "todo.db"));
        try
        {
            await taskStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var board = new Alondra1.Core.Tasks.TaskBoard(taskStore, new Alondra1.Core.Ports.SystemClock());
            await board.LoadAsync().ConfigureAwait(false);
            var taskFiles = new TaskFileStore(Path.Combine(root, "task-files"));
            try
            {
                taskFiles.RemoveOrphans(board.Tasks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine("Task file cleanup skipped: " + ex.Message);
            }

            return (taskStore, taskFiles, board, null);
        }
        catch (Exception ex) when (ex is Alondra1.Core.Tasks.TodoStorageException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await taskStore.DisposeAsync().ConfigureAwait(false);
            return (null, null, null, ex.Message);
        }
    }
}

public sealed class NullConflicts : Alondra1.Core.Ports.IConflictSink
{
    public void ReportPreservedBoth(string description) => Debug.WriteLine(description);
}
