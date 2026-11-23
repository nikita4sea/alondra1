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
        // Always enabled so the button keeps showing its status while a sync runs; SyncAsync ignores a second press.
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
    /// <summary>Grouping by account only applies when every account is in view.</summary>
    public bool IsAllAccounts => SelectedAccount == "All accounts";
    public string SelectedFolder { get; private set; } = "Inbox";
    public bool FoldersHidden => FoldersDock == DockEdge.Hidden;
    public bool FoldersAreBand => FoldersDock is DockEdge.Top or DockEdge.Bottom;
    public DockEdge FoldersDock => _services.Config.Layout.FoldersDock;
    public DockEdge ReadingDock => _services.Config.Layout.ReadingDock;
    public double FolderBreadth => LayoutArranger.BreadthFor(FoldersDock, _services.Config.Layout.FolderBreadthSide, _services.Config.Layout.FolderBreadthBand);
    public double ReadingBreadth => LayoutArranger.BreadthFor(ReadingDock, _services.Config.Layout.ReadingBreadthSide, _services.Config.Layout.ReadingBreadthBand);
    /// <summary>Message row height: 32 px compact (default), 40 px comfortable (client spec §6).</summary>
    public double RowHeight => _services.Config.Appearance.Density == Density.Compact ? 32 : 40;
    public string DensityLabel => _services.Config.Appearance.Density.ToString();
    public StoredMessage? SelectedMessage { get; private set; }
    public string ReaderStatus { get; private set; } = "Reading pane";
    public string ReaderSubject { get; private set; } = "No message selected";
    public string ReaderSender { get; private set; } = "";
    public string ReaderBody { get; private set; } = "";
    /// <summary>List header count: "24 messages", "5 of 24 messages", "Loading…", "No messages" (see <see cref="MessageCount"/>).</summary>
    public string CountText => MessageCount.Describe(Messages.Count, _unfilteredTotal, _loadingMessages, !string.IsNullOrWhiteSpace(SearchText), _selectionCount);
    private int? _unfilteredTotal;
    private bool _loadingMessages;
    private int _selectionCount;
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
    // ---------- Reader (client spec §4) ----------

    private StoredBody? _body;
    private bool? _htmlChoice;
    private bool _imagesLoadedForMessage;
    private string? _htmlFailure;
    private MimeKit.MimeMessage? _fixture;

    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasHtml => !string.IsNullOrWhiteSpace(_body?.Html) && _htmlFailure is null;
    public bool ShowingHtml => ReaderPolicy.OpenAsHtml(_services.Config.Reading, HasHtml, _htmlChoice);
    public bool ShowingPlain => !ShowingHtml;
    public string ReaderTo { get; private set; } = "";
    public bool HasReaderTo => !string.IsNullOrWhiteSpace(ReaderTo);
    public string ReaderMeta { get; private set; } = "";

    /// <summary>The document the reader shows: sanitized HTML, or the plain text as a linkified page.</summary>
    public string ReaderDocument { get; private set; } = "";
    public bool ReaderAllowRemote { get; private set; }
    public int BlockedImages { get; private set; }
    public bool ShowImageBar => ShowingHtml && BlockedImages > 0 && !ReaderAllowRemote;
    public string ImageBarText => BlockedImages == 1
        ? "1 remote image is blocked to protect your privacy."
        : $"{BlockedImages} remote images are blocked to protect your privacy.";
    public string TrustSenderLabel => SelectedMessage is { FromAddress.Length: > 0 } message ? $"Always load from {message.FromAddress}" : "Always load from this sender";
    public string? HtmlFailure => _htmlFailure;
    public bool ShowHtmlFailure => _htmlFailure is not null && !string.IsNullOrWhiteSpace(_body?.Html);

    public IRelayCommand ShowHtmlCommand => _showHtml ??= new RelayCommand(() => ChooseFormat(html: true), () => HasHtml);
    public IRelayCommand ShowPlainCommand => _showPlain ??= new RelayCommand(() => ChooseFormat(html: false));
    public IRelayCommand LoadImagesCommand => _loadImages ??= new RelayCommand(() => { _imagesLoadedForMessage = true; RenderReader(); });
    public IRelayCommand TrustSenderCommand => _trustSender ??= new RelayCommand(TrustSender);
    private IRelayCommand? _showHtml;
    private IRelayCommand? _showPlain;
    private IRelayCommand? _loadImages;
    private IRelayCommand? _trustSender;

    /// <summary>Theme colors for plain-text documents; set by the window from the current theme.</summary>
    public Alondra1.Core.Html.MailHtml.PlainColors PlainColors { get; set; } = new("transparent", "#2a2940", "#4b4fa8");

    /// <summary>Switches between HTML and plain text for this message only; no re-fetch.</summary>
    public void ChooseFormat(bool html)
    {
        _htmlChoice = html;
        RenderReader();
    }

    private void TrustSender()
    {
        if (SelectedMessage is null || IsDemo && _fixture is null)
        {
            _imagesLoadedForMessage = true;
            RenderReader();
            return;
        }

        if (ReaderPolicy.Trust(_services.Config.Reading, SelectedMessage.FromAddress))
            SaveConfig();
        RenderReader();
    }

    /// <summary>The view reports that HTML could not be shown; the reader switches to plain text and says so.</summary>
    public void ReportHtmlFailure(string reason)
    {
        if (_htmlFailure is not null)
            return;
        _htmlFailure = reason;
        RenderReader();
    }

    /// <summary>Builds the reader document for the current message, format, and image policy.</summary>
    public void RenderReader()
    {
        if (_body is null)
        {
            ReaderDocument = Alondra1.Core.Html.MailHtml.PlainDocument(ReaderBody, PlainColors);
            ReaderAllowRemote = false;
            BlockedImages = 0;
        }
        else if (ShowingHtml)
        {
            ReaderAllowRemote = ReaderPolicy.AllowRemoteImages(_services.Config.Reading, SelectedMessage?.FromAddress, _imagesLoadedForMessage);
            var result = Alondra1.Core.Html.MailHtml.Render(_body.Html, new Alondra1.Core.Html.MailRenderOptions { AllowRemoteImages = ReaderAllowRemote });
            if (result.Failed)
            {
                // Diagnostic without content: the reason names the problem, never the message text.
                Debug.WriteLine("Reader: HTML not shown: " + result.FailureReason);
                _htmlFailure = "Unable to display HTML.";
                RenderReader();
                return;
            }

            ReaderDocument = result.Document;
            BlockedImages = result.BlockedRemoteImages;
        }
        else
        {
            ReaderDocument = Alondra1.Core.Html.MailHtml.PlainDocument(_body.Text, PlainColors);
            ReaderAllowRemote = false;
            BlockedImages = 0;
        }

        foreach (var name in new[]
        {
            nameof(ReaderDocument), nameof(ReaderAllowRemote), nameof(BlockedImages), nameof(ShowImageBar), nameof(ImageBarText), nameof(TrustSenderLabel),
            nameof(HasHtml), nameof(ShowingHtml), nameof(ShowingPlain), nameof(HtmlFailure), nameof(ShowHtmlFailure), nameof(ReaderBody),
        })
            OnPropertyChanged(name);
        ShowHtmlCommand.NotifyCanExecuteChanged();
    }

    private void SetBody(StoredBody? body, string? recipients = null)
    {
        _body = body;
        if (body is not null)
            ReaderBody = body.Text;
        if (!string.IsNullOrWhiteSpace(recipients))
            ReaderTo = "To " + recipients;
        Attachments.Clear();
        foreach (var attachment in body?.Attachments ?? [])
            Attachments.Add(new AttachmentRow(attachment));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ReaderTo));
        OnPropertyChanged(nameof(HasReaderTo));
        RenderReader();
    }

    /// <summary>Writes an attachment to <paramref name="destination"/>: from the saved copy when there is one, otherwise from the server.</summary>
    public async Task SaveAttachmentAsync(AttachmentRow row, Stream destination)
    {
        var source = row.Source;
        if (_fixture is not null)
        {
            var entity = Alondra1.Mail.MimeBody.FindAttachment(_fixture, source.FileName) ?? throw new InvalidOperationException("That attachment is not in the message.");
            await Alondra1.Mail.MimeBody.WriteAsync(entity, destination, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (IsDemo)
            throw new InvalidOperationException("Sample messages have no files to open or save.");
        if (!string.IsNullOrEmpty(source.CacheRelativePath) && File.Exists(_services.Files.Resolve(source.CacheRelativePath)))
        {
            await using var cached = File.OpenRead(_services.Files.Resolve(source.CacheRelativePath));
            await cached.CopyToAsync(destination).ConfigureAwait(true);
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == source.AccountId)
            ?? throw new InvalidOperationException("The account of this message was removed.");
        var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
        await _services.Mail.FetchAttachmentAsync(account, secrets, source.FolderId, source.Uid, source.FileName, destination, null, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Diagnostics (--open-eml): shows a message file in the reader through the normal pipeline.</summary>
    public void ShowMessageFile(string path)
    {
        var message = MimeKit.MimeMessage.Load(path);
        _fixture = message;
        var from = message.From.Mailboxes.FirstOrDefault();
        var stored = new StoredMessage
        {
            AccountId = "file",
            AccountName = "File",
            FolderId = "Inbox",
            Uid = 1,
            Subject = message.Subject ?? "",
            FromName = string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? "" : from.Name,
            FromAddress = from?.Address ?? "",
            ReceivedUtc = message.Date,
            TimeLabel = MailText.FormatTime(message.Date, DateTimeOffset.Now),
            Availability = ItemAvailability.Downloaded,
            LocalOnly = true,
        };
        Messages.Clear();
        Messages.Add(stored);
        _unfilteredTotal = 1;
        SelectMessage(stored);
        SetBody(Alondra1.Mail.MimeBody.Extract(message, "file", "Inbox", 1), string.Join(", ", message.To.Mailboxes.Select(item => item.Address)));
        RaiseListState();
    }

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
    /// <summary>Mail chooses its account in the view settings; the other workspaces keep the toolbar selector.</summary>
    public bool ShowToolbarAccountSelector => IsCalendar || IsNotes || IsContacts;
    public bool HasSyncButton => HasAccountSelector;
    public bool IsSyncing { get; private set; }
    public string SyncToolTip => (IsSyncing ? "Syncing…" : "Sync now (F5)") + (string.IsNullOrWhiteSpace(StatusText) ? "" : "\n" + StatusText);
    public string SyncAccessName => $"Sync now. Status: {ConnectionLabel}. {StatusText}";

    /// <summary>With folders docked on top, they appear as tabs in the module toolbar instead of a separate pane.</summary>
    public bool ShowToolbarFolders => FoldersDock == DockEdge.Top;
    public bool ShowFolderCombo => FoldersDock == DockEdge.Hidden;
    public DockEdge PaneFoldersDock => FoldersDock == DockEdge.Top ? DockEdge.Hidden : FoldersDock;

    public string ReadToggleLabel => SelectedMessage is null ? "Mark as read or unread — Select a message first"
        : SelectedMessage.IsUnread ? "Mark as read" : "Mark as unread";
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
        // Breakpoints follow the width of the workspace tab strip (docs/UI-DESIGN.md → Responsive behavior).
        ShowNavLabels = width >= 1200;
        ShowWordmark = width >= 640;
        CompactSearch = width < 860;
        ShowStatusLabel = width >= 1240;
        ShowSecondaryMailActions = width >= 1040;
        ShowFolderLabels = width >= 960;
        FolderTabsAsCombo = width < 760;
        ShowNewLabel = width >= 560;
        string[] overflow = width < 560 ? ["Calendar", "To-do", "Notes", "Contacts", "Chat"]
            : width < 720 ? ["Notes", "Contacts", "Chat"]
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
        SelectedAccount = string.IsNullOrWhiteSpace(account) || !AccountChoices.Contains(account) ? "All accounts" : account;
        var remembered = IsAllAccounts ? null : SelectedAccount;
        if (!IsDemo && _services.Config.Layout.MailAccount != remembered)
        {
            _services.Config.Layout.MailAccount = remembered;
            SaveConfig();
        }

        _ = ReloadMessagesAsync();
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsAllAccounts));
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
        // Selecting the message that is already open (the list echoing a selection) keeps its body, format, and images.
        if (message is not null && ReferenceEquals(message, SelectedMessage) && _body is not null)
            return;
        SelectedMessage = message;
        // A new message starts in the default format with remote images blocked (unless allowed by settings).
        _htmlChoice = null;
        _imagesLoadedForMessage = false;
        _htmlFailure = null;
        _body = null;
        ReaderTo = "";
        Attachments.Clear();
        if (message is null)
        {
            ReaderStatus = "Reading pane";
            ReaderSubject = "No message selected";
            ReaderSender = "";
            ReaderMeta = "";
            ReaderBody = "";
        }
        else
        {
            ReaderSubject = message.Subject;
            ReaderSender = string.IsNullOrWhiteSpace(message.FromAddress) || message.FromName == message.FromAddress
                ? message.FromName
                : $"{message.FromName} <{message.FromAddress}>";
            ReaderMeta = $"{message.AccountName} · {message.ReceivedUtc.ToLocalTime():ddd d MMM yyyy, HH:mm}";
            ReaderTo = string.IsNullOrWhiteSpace(message.Recipients) ? "" : "To " + message.Recipients;
            ReaderStatus = message.SendState ?? AvailabilityLabel(message);
            ReaderBody = string.IsNullOrWhiteSpace(message.Preview) ? "" : message.Preview;
            if (_fixture is null || message.AccountId != "file")
                _ = LoadBodyAsync(message);
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ReaderMeta));
        OnPropertyChanged(nameof(ReaderTo));
        OnPropertyChanged(nameof(HasReaderTo));
        RenderReader();

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
        OnPropertyChanged(nameof(ShowTimeColumn));
        OnPropertyChanged(nameof(ShowAttachmentColumn));
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

        if (IsSyncing)
            return;
        IsSyncing = true;
        OnPropertyChanged(nameof(IsSyncing));
        SyncCommand.NotifyCanExecuteChanged();
        try
        {
            UpdateStatus("Syncing…", ConnectionState.Syncing, summary: true);
            // Every account is synced and reported: one failing account is never hidden behind another's success.
            var failures = new List<string>();
            var allOffline = true;
            var synced = 0;
            foreach (var account in _services.Config.Accounts.ToList())
            {
                // The account may have been deleted while an earlier account was syncing.
                if (!_services.Config.Accounts.Contains(account))
                    continue;
                UpdateStatus($"Syncing {account.DisplayName}…", ConnectionState.Syncing, summary: true);
                var report = await _services.Sync.SyncAsync(account, CancellationToken.None).ConfigureAwait(true);
                if (report.Ok)
                {
                    synced++;
                    // Remember where this server keeps Inbox, Sent, Drafts, Archive, and Trash (for example "INBOX.INBOX.Sent").
                    if (SpecialFolders.Learn(account, report.Folders))
                        SaveConfig();
                }
                else
                {
                    failures.Add($"{account.DisplayName}: {report.Detail}");
                    allOffline &= report.Offline;
                }

                if (report.DegradedDedup)
                    Dialogs.Alert(report.Detail, "Alondra1", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            if (failures.Count == 0)
                UpdateStatus(synced == 1 ? "1 account up to date" : $"{synced} accounts up to date", ConnectionState.Ready, summary: true);
            else
                UpdateStatus(string.Join("\n", failures), allOffline ? ConnectionState.Offline : ConnectionState.Error, summary: true);
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
        finally
        {
            IsSyncing = false;
            OnPropertyChanged(nameof(IsSyncing));
            OnPropertyChanged(nameof(SyncToolTip));
            SyncCommand.NotifyCanExecuteChanged();
        }
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
        _services.ApplyImported(replace ? plan.Replaced : plan.Merged);
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
        _loadingMessages = true;
        RaiseListState();
        var searching = !string.IsNullOrWhiteSpace(SearchText);
        IReadOnlyList<StoredMessage> found;
        var total = 0;
        try
        {
            if (IsDemo)
            {
                found = SampleCatalog.Query(SelectedFolder, SelectedAccount, SearchText);
                total = searching ? SampleCatalog.Query(SelectedFolder, SelectedAccount, "").Count : found.Count;
            }
            else
            {
                var list = new List<StoredMessage>();
                foreach (var account in AccountsInView())
                {
                    var folderId = ResolveFolder(account, SelectedFolder);
                    if (account.Mode == AccountMode.Personal)
                    {
                        var all = await _services.Store.ListAsync(account.Id, folderId, CancellationToken.None).ConfigureAwait(true);
                        total += all.Count;
                        list.AddRange(searching
                            ? await _services.Store.SearchAsync(account.Id, folderId, SearchText, CancellationToken.None).ConfigureAwait(true)
                            : all);
                    }
                    else
                    {
                        var all = _services.Cache.List(account.Id, folderId);
                        total += all.Count;
                        list.AddRange(searching ? _services.Cache.Search(account.Id, folderId, SearchText) : all);
                    }
                }

                found = list.OrderByDescending(item => item.ReceivedUtc).ToList();
            }
        }
        finally
        {
            _loadingMessages = false;
        }

        _unfilteredTotal = total;
        _selectionCount = 0;
        _selection = [];
        Messages.Clear();
        foreach (var message in found)
            Messages.Add(message);
        SelectMessage(Messages.FirstOrDefault());
        OnPropertyChanged(nameof(Messages));
        RaiseListState();
    }

    /// <summary>Called by the list when its selection changes; two or more selected rows are counted in the header.</summary>
    public void SetSelectionCount(int count)
    {
        _selectionCount = count;
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>What the list shows when it has no rows: loading, an empty folder, no search results, or a sync problem.</summary>
    public string ListState => _loadingMessages && Messages.Count == 0 ? "loading"
        : Messages.Count > 0 ? "rows"
        : !string.IsNullOrWhiteSpace(SearchText) ? "nomatch"
        : Connection is ConnectionState.Error ? "error"
        : Connection is ConnectionState.Offline ? "offline"
        : "empty";

    public bool ShowListPlaceholder => ListState != "rows";

    public string EmptyTitle => ListState switch
    {
        "loading" => "Loading messages…",
        "nomatch" => "No messages match your search",
        "error" => "Sync failed",
        "offline" => "You are offline",
        _ => "No messages here",
    };

    public string EmptyDetail => ListState switch
    {
        "loading" => "",
        "nomatch" => $"Nothing in {SelectedFolder} matches “{SearchText.Trim()}”.",
        "error" => "Messages could not be loaded. Point at the Sync button for details, or press F5 to try again.",
        "offline" => "Messages already on this PC show here. Press F5 when you are back online.",
        _ => $"Messages in {SelectedFolder} appear here.",
    };

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ListState));
        OnPropertyChanged(nameof(ShowListPlaceholder));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }

    private async Task LoadBodyAsync(StoredMessage message)
    {
        if (IsDemo)
        {
            SetBody(SampleCatalog.BodyFor(message));
            RaiseReader();
            return;
        }

        var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId);
        if (account is null)
            return;
        StoredBody? body;
        var key = new MessageKey(message.AccountId, message.FolderId, message.Uid);
        try
        {
            if (account.Mode == AccountMode.Personal)
            {
                body = await _services.Store.GetBodyAsync(key, CancellationToken.None).ConfigureAwait(true);
                if (body is not null)
                    body.Attachments = (await _services.Store.ListAttachmentsAsync(key, CancellationToken.None).ConfigureAwait(true)).ToList();
            }
            else
            {
                body = _services.Cache.GetBody(key);
                if (body is null && message.Uid > 0)
                {
                    var secrets = await _services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                    body = await _services.Mail.FetchBodyAsync(account, secrets, message.FolderId, message.Uid, CancellationToken.None).ConfigureAwait(true);
                    _services.Cache.SetBody(key, body);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or MailKit.Security.AuthenticationException or MailKit.ProtocolException)
        {
            // The preview stays; the reader says the full message could not be loaded (no message content is logged).
            ReaderStatus = "Could not load the full message: " + ex.GetType().Name;
            RaiseReader();
            return;
        }

        // The user may have moved on while the body loaded.
        if (!ReferenceEquals(SelectedMessage, message))
            return;
        if (body is not null)
            SetBody(body);
        RaiseReader();
    }

    private async Task DeleteAsync() => await MoveSpecialAsync("Trash");

    /// <summary>The selected rows (two or more with Shift/Ctrl); actions apply to all of them.</summary>
    private IReadOnlyList<StoredMessage> _selection = [];

    /// <summary>Called by the list with every selected row; the reader keeps showing the focused one.</summary>
    public void SetSelection(IReadOnlyList<StoredMessage> selection)
    {
        _selection = selection;
        SetSelectionCount(selection.Count);
    }

    private IReadOnlyList<StoredMessage> ActionTargets() =>
        _selection.Count > 1 ? _selection.ToList() : SelectedMessage is null ? [] : [SelectedMessage];

    private async Task MoveSpecialAsync(string folder)
    {
        var targets = ActionTargets();
        if (targets.Count == 0)
            return;
        if (IsDemo)
        {
            foreach (var message in targets)
                Messages.Remove(message);
            _unfilteredTotal = Math.Max(0, (_unfilteredTotal ?? targets.Count) - targets.Count);
            _selection = [];
            _selectionCount = 0;
            SelectMessage(Messages.FirstOrDefault());
            RaiseListState();
            return;
        }

        foreach (var message in targets)
        {
            var account = _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId);
            if (account is null)
                continue;
            var command = new RemoteMailCommand
            {
                Kind = folder == "Trash" ? MailCommandKind.Delete : MailCommandKind.Archive,
                FolderId = message.FolderId,
                Uid = message.Uid,
                DestinationFolderId = ResolveFolder(account, folder),
            };
            await _services.Sync.ApplyLocalCommandAsync(account, command, CancellationToken.None).ConfigureAwait(true);
        }

        _selection = [];
        await ReloadMessagesAsync().ConfigureAwait(true);
    }

    private async Task ToggleReadAsync()
    {
        var targets = ActionTargets();
        if (targets.Count == 0)
            return;
        // Several rows: mark all read when any is unread, otherwise all unread (like other mail apps).
        var markRead = targets.Count > 1 ? targets.Any(message => message.IsUnread) : targets[0].IsUnread;
        foreach (var message in targets)
        {
            message.IsUnread = !markRead;
            if (!IsDemo && _services.Config.Accounts.FirstOrDefault(item => item.Id == message.AccountId) is { } account)
            {
                await _services.Sync.ApplyLocalCommandAsync(account, new RemoteMailCommand
                {
                    Kind = MailCommandKind.SetRead,
                    FolderId = message.FolderId,
                    Uid = message.Uid,
                    Value = markRead,
                }, CancellationToken.None).ConfigureAwait(true);
            }

            MessageStateChanged?.Invoke(message);
        }

        RaiseReader();
    }

    // ---------- One pane at a time (client spec §5.1) ----------

    /// <summary>True when list and reader are shown one at a time; the reader then has a Back button.</summary>
    public bool IsSinglePane { get; private set; }
    public bool ShowBackButton { get; private set; }

    public void SetSinglePane(bool single, bool readerShown)
    {
        if (IsSinglePane == single && ShowBackButton == (single && readerShown))
            return;
        IsSinglePane = single;
        ShowBackButton = single && readerShown;
        OnPropertyChanged(nameof(IsSinglePane));
        OnPropertyChanged(nameof(ShowBackButton));
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

        // The last chosen account comes back after a restart, as long as it still exists.
        var remembered = IsDemo ? null : _services.Config.Layout.MailAccount;
        SelectedAccount = remembered is not null && AccountChoices.Contains(remembered) ? remembered : "All accounts";
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsAllAccounts));
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

    private static string ResolveFolder(AccountConfig account, string display) => SpecialFolders.Resolve(account, display);

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

    private void UpdateStatus(string detail, ConnectionState? state = null, bool summary = false)
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
            // A summary covers every account; other messages are about the account in view.
            StatusText = summary ? detail : $"{account.DisplayName} · {account.Mode} · {detail}";
            Connection = state ?? (Connection is ConnectionState.Sample or ConnectionState.NoAccounts ? ConnectionState.Ready : Connection);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionBrushKey));
        OnPropertyChanged(nameof(SyncToolTip));
        OnPropertyChanged(nameof(SyncAccessName));
        RaiseListState();
        ConnectionChanged?.Invoke();
    }

    /// <summary>Raised when a message's own state (read or unread) changed, so the list can redraw that row.</summary>
    public event Action<StoredMessage>? MessageStateChanged;

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
        OnPropertyChanged(nameof(ShowToolbarAccountSelector));
        OnPropertyChanged(nameof(HasSyncButton));
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
        OnPropertyChanged(nameof(ReaderMeta));
        OnPropertyChanged(nameof(HasAttachments));
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

    /// <summary>
    /// Diagnostics for the reinstall check (--send-check): sends a message from the first manual account to the second,
    /// then syncs until it arrives (up to about two minutes). Returns a one-line result without addresses or content.
    /// </summary>
    public async Task<(bool Ok, string Detail)> RunSendCheckAsync()
    {
        var manual = _services.Config.Accounts.Where(account => account.Kind == AccountKind.Manual).ToList();
        if (manual.Count < 2)
            return (false, "send-check needs two manual accounts");
        var (from, to) = (manual[0], manual[1]);
        var token = "Alondra1 reinstall check " + Guid.NewGuid().ToString("N")[..12];
        var result = await Send(from, new OutgoingMail { To = [to.Address], Subject = token, TextBody = "Automatic check after a reinstall. You can delete this message." }).ConfigureAwait(true);
        if (result.Acceptance != SmtpAcceptance.Accepted)
            return (false, $"send-check: {from.DisplayName} could not send ({result.Acceptance})");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 5 : 10)).ConfigureAwait(true);
            var report = await _services.Sync.SyncAsync(to, CancellationToken.None).ConfigureAwait(true);
            if (!report.Ok)
                continue;
            var inbox = SpecialFolders.Resolve(to, "Inbox");
            var arrived = to.Mode == AccountMode.Personal
                ? (await _services.Store.ListAsync(to.Id, inbox, CancellationToken.None).ConfigureAwait(true)).Any(message => message.Subject == token)
                : _services.Cache.List(to.Id, inbox).Any(message => message.Subject == token);
            if (arrived)
                return (true, $"send-check: {from.DisplayName} → {to.DisplayName} received after {attempt + 1} sync(s)");
        }

        return (false, $"send-check: sent by {from.DisplayName} but not received by {to.DisplayName} in time");
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

}

/// <summary>An attachment in the reader: name, kind, size, and the stored description it came from.</summary>
public sealed class AttachmentRow(StoredAttachment source)
{
    public StoredAttachment Source { get; } = source;
    public string FileName => Source.FileName;
    public string SizeText => Source.Size switch
    {
        <= 0 => "",
        < 1024 => $"{Source.Size} B",
        < 1024 * 1024 => $"{Source.Size / 1024.0:0} KB",
        _ => $"{Source.Size / (1024.0 * 1024):0.0} MB",
    };
    public string Kind => Source.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Image" : "Attachment";
    public string AccessName => string.IsNullOrEmpty(SizeText) ? FileName : $"{FileName}, {SizeText}";
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

    /// <summary>Applies a reviewed import (the Merged or Replaced result of <see cref="ConfigWorkflow.Preview"/>) and saves it.</summary>
    public void ApplyImported(AppConfig next)
    {
        next.DemoMode = Config.DemoMode && next.Accounts.Count == 0;
        Config.Appearance = next.Appearance;
        Config.Layout = next.Layout;
        Config.Reading = next.Reading;
        Config.OAuth = next.OAuth;
        Config.Accounts = next.Accounts;
        Config.SchemaVersion = ConfigSchema.Current;
        SaveConfig();
        RefreshOAuth();
    }

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
