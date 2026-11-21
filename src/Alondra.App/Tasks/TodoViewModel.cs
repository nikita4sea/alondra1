using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alondra.App.ViewModels;
using Alondra.Core.Tasks;
using Alondra.Persistence;

namespace Alondra.App.Tasks;

/// <summary>
/// Presentation state for the local To-do workspace. All task rules live in <see cref="TaskBoard"/>; this class turns
/// them into rows, keeps preferences, and reports results.
/// </summary>
public sealed partial class TodoViewModel : ObservableObject
{
    public const string CalendarKey = "calendar";
    public const string SummaryKey = "summary";
    public const string AccountKey = "account";

    private readonly TaskBoard? _board;
    private readonly AppServices _services;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private TaskPreferences _prefs = new();
    private IReadOnlyList<string> _selectedIds = [];
    private string? _detailId;
    private bool _titleDirty;
    private bool _notesDirty;
    private DateOnly _calendarAnchor;
    private DateOnly _calendarSelected;

    public TodoViewModel(AppServices services, MainViewModel host)
    {
        _services = services;
        Host = host;
        _board = services.Tasks;
        StoreError = services.TaskStoreError;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = ""; OnPropertyChanged(nameof(ToastText)); OnPropertyChanged(nameof(HasToast)); };
        if (_board is not null)
            _board.Changed += () => Refresh();

        QuickAddCommand = new AsyncRelayCommand(QuickAddAsync);
        ToggleCompleteCommand = new AsyncRelayCommand<TaskItemVm>(async item => { if (item is not null) await Run(() => _board!.SetCompletedAsync([item.Id], !item.Completed), item.Completed ? "Task reopened" : "Task completed"); });
        ToggleImportantCommand = new AsyncRelayCommand<TaskItemVm>(async item => { if (item is not null) await Run(() => _board!.UpdateTaskAsync(item.Id, task => task.Important = !task.Important, "Change importance")); });
        UndoCommand = new AsyncRelayCommand(UndoAsync);
        ToggleStepCommand = new AsyncRelayCommand<SubtaskVm>(async step => { if (step is not null && _detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => SubtaskEdits.Toggle(task, step.Id, _board.Now), "Complete step")); });
        DeleteStepCommand = new AsyncRelayCommand<SubtaskVm>(async step => { if (step is not null && _detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => SubtaskEdits.Remove(task, step.Id), "Delete step"), "Step deleted"); });
        StepUpCommand = new AsyncRelayCommand<SubtaskVm>(async step => { if (step is not null && _detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => SubtaskEdits.Move(task, step.Id, -1), "Reorder steps")); });
        StepDownCommand = new AsyncRelayCommand<SubtaskVm>(async step => { if (step is not null && _detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => SubtaskEdits.Move(task, step.Id, 1), "Reorder steps")); });
        PromoteStepCommand = new AsyncRelayCommand<SubtaskVm>(PromoteStepAsync);
        AddStepCommand = new AsyncRelayCommand(AddStepAsync);
        RemoveReminderCommand = new AsyncRelayCommand<ReminderVm>(async reminder => { if (reminder is not null && _detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => task.Reminders.RemoveAll(item => item.Id == reminder.Id), "Remove reminder")); });
        AddReminderCommand = new AsyncRelayCommand<string>(AddReminderPresetAsync);
        SetDueCommand = new AsyncRelayCommand<string>(SetDuePresetAsync);
        ToggleMyDayCommand = new AsyncRelayCommand(async () => { if (_detailId is { } id) await ToggleMyDayAsync([id]); });
        ToggleDetailImportantCommand = new AsyncRelayCommand(async () => { if (_detailId is { } id) await Run(() => _board!.UpdateTaskAsync(id, task => task.Important = !task.Important, "Change importance")); });
        ToggleDetailCompleteCommand = new AsyncRelayCommand(async () => { if (_detailId is { } id && _board!.FindTask(id) is { } task) await Run(() => _board.SetCompletedAsync([id], !task.Completed), task.Completed ? "Task reopened" : "Task completed"); });
        CloseDetailCommand = new RelayCommand(() => { _prefs.DetailsOpen = false; SavePrefs(); RaiseDetail(); });
        CalendarPrevCommand = new RelayCommand(() => { _calendarAnchor = TaskCalendar.Shift(_calendarAnchor, _prefs.CalendarMode, -1); BuildCalendar(); });
        CalendarNextCommand = new RelayCommand(() => { _calendarAnchor = TaskCalendar.Shift(_calendarAnchor, _prefs.CalendarMode, 1); BuildCalendar(); });
        CalendarTodayCommand = new RelayCommand(() => { _calendarAnchor = _calendarSelected = Today; BuildCalendar(); });
        CalendarModeCommand = new RelayCommand<string>(mode => { if (Enum.TryParse<CalendarMode>(mode, out var value)) { _prefs.CalendarMode = value; SavePrefs(); BuildCalendar(); } });
        SelectCalendarDateCommand = new RelayCommand<CalendarCellVm>(cell => { if (cell is not null) { _calendarSelected = cell.Date; if (_prefs.CalendarMode == CalendarMode.Month && !cell.InMonth) _calendarAnchor = cell.Date; BuildCalendar(); } });
        OpenDayCommand = new RelayCommand<CalendarCellVm>(cell => { if (cell is not null) { _calendarSelected = _calendarAnchor = cell.Date; _prefs.CalendarMode = CalendarMode.Day; SavePrefs(); BuildCalendar(); } });
        SelectCalendarTaskCommand = new RelayCommand<CalendarTaskVm>(item => { if (item is not null) ShowDetail(item.Id); });
        DismissReminderCommand = new AsyncRelayCommand<ActiveReminder>(async item => { if (item is not null) { ActiveReminders.Remove(item); await Run(() => _board!.DismissReminderAsync(item.TaskId, item.ReminderId)); RaiseReminders(); } });
        SnoozeReminderCommand = new AsyncRelayCommand<ActiveReminder>(async item => { if (item is not null) { ActiveReminders.Remove(item); await Run(() => _board!.SnoozeReminderAsync(item.TaskId, item.ReminderId, TimeSpan.FromMinutes(Math.Clamp(_prefs.SnoozeMinutes, 1, 1440))), $"Snoozed for {_prefs.SnoozeMinutes} minutes"); RaiseReminders(); } });
        OpenReminderCommand = new RelayCommand<ActiveReminder>(item => { if (item is not null) OpenTask(item.TaskId); });
        ToggleGroupCommand = new AsyncRelayCommand<SidebarItem>(async item => { if (item?.GroupId is { } group) await Run(() => _board!.UpdateGroupAsync(group, value => value.Collapsed = !value.Collapsed, "Collapse group", undoable: false)); });
        ClearSearchCommand = new RelayCommand(() => { SearchText = ""; OnPropertyChanged(nameof(SearchText)); RebuildItems(); });
    }

    public MainViewModel Host { get; }
    public TaskBoard? Board => _board;
    public string? StoreError { get; }
    public bool IsAvailable => _board is not null;

    public ObservableCollection<SidebarItem> Sidebar { get; } = [];
    public ObservableCollection<TaskItemVm> Items { get; } = [];
    public ObservableCollection<SubtaskVm> Steps { get; } = [];
    public ObservableCollection<ReminderVm> Reminders { get; } = [];
    public ObservableCollection<AttachmentVm> Attachments { get; } = [];
    public ObservableCollection<ListChoice> ListChoices { get; } = [];
    public ObservableCollection<CalendarCellVm> CalendarCells { get; } = [];
    public ObservableCollection<DaySlotVm> DaySlots { get; } = [];
    public ObservableCollection<CalendarTaskVm> Backlog { get; } = [];
    public ObservableCollection<StatTile> Tiles { get; } = [];
    public ObservableCollection<DayBar> DayBars { get; } = [];
    public ObservableCollection<HistoryRow> RecentHistory { get; } = [];
    public ObservableCollection<ActiveReminder> ActiveReminders { get; } = [];

    public IAsyncRelayCommand QuickAddCommand { get; }
    public IAsyncRelayCommand<TaskItemVm> ToggleCompleteCommand { get; }
    public IAsyncRelayCommand<TaskItemVm> ToggleImportantCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand<SubtaskVm> ToggleStepCommand { get; }
    public IAsyncRelayCommand<SubtaskVm> DeleteStepCommand { get; }
    public IAsyncRelayCommand<SubtaskVm> StepUpCommand { get; }
    public IAsyncRelayCommand<SubtaskVm> StepDownCommand { get; }
    public IAsyncRelayCommand<SubtaskVm> PromoteStepCommand { get; }
    public IAsyncRelayCommand AddStepCommand { get; }
    public IAsyncRelayCommand<ReminderVm> RemoveReminderCommand { get; }
    public IAsyncRelayCommand<string> AddReminderCommand { get; }
    public IAsyncRelayCommand<string> SetDueCommand { get; }
    public IAsyncRelayCommand ToggleMyDayCommand { get; }
    public IAsyncRelayCommand ToggleDetailImportantCommand { get; }
    public IAsyncRelayCommand ToggleDetailCompleteCommand { get; }
    public IRelayCommand CloseDetailCommand { get; }
    public IRelayCommand CalendarPrevCommand { get; }
    public IRelayCommand CalendarNextCommand { get; }
    public IRelayCommand CalendarTodayCommand { get; }
    public IRelayCommand<string> CalendarModeCommand { get; }
    public IRelayCommand<CalendarCellVm> SelectCalendarDateCommand { get; }
    public IRelayCommand<CalendarCellVm> OpenDayCommand { get; }
    public IRelayCommand<CalendarTaskVm> SelectCalendarTaskCommand { get; }
    public IAsyncRelayCommand<ActiveReminder> DismissReminderCommand { get; }
    public IAsyncRelayCommand<ActiveReminder> SnoozeReminderCommand { get; }
    public IRelayCommand<ActiveReminder> OpenReminderCommand { get; }
    public IAsyncRelayCommand<SidebarItem> ToggleGroupCommand { get; }
    public IRelayCommand ClearSearchCommand { get; }

    public event Action? FocusQuickAddRequested;
    public event Action? PrintRequested;
    public event Action? SearchFocusRequested;

    public void RequestPrint() => PrintRequested?.Invoke();
    public void RequestSearchFocus() => SearchFocusRequested?.Invoke();

    /// <summary>Alondra icon for the current view heading.</summary>
    public string ViewIcon =>
        IsSearching && IsTaskView ? "Search"
        : IsCalendarView ? "Calendar"
        : IsSummaryView ? "Chart"
        : IsAccountView ? "Contacts"
        : CurrentListId is { } id ? (_board?.FindList(id)?.IsDefault == true ? "Home" : "List")
        : CurrentSmart is { } view ? IconFor(view) : "Todo";

    public static string IconFor(SmartView view) => view switch
    {
        SmartView.MyDay => "Sun",
        SmartView.Important => "Priority",
        SmartView.Planned => "Calendar",
        SmartView.Upcoming => "Clock",
        SmartView.Overdue => "Alert",
        SmartView.All => "Todo",
        SmartView.Completed => "Check",
        _ => "Unscheduled",
    };
    public event Action? ItemsRebuilding;
    public event Action? ItemsRebuilt;

    /// <summary>True while <see cref="Items"/> is being refilled; selection changes during that time are not user actions.</summary>
    public bool IsRebuilding { get; private set; }
    public event Action<string>? ErrorRaised;

    // ---------- View state ----------

    public string CurrentKey { get; private set; } = "smart:MyDay";
    public SmartView? CurrentSmart => CurrentKey.StartsWith("smart:", StringComparison.Ordinal) && Enum.TryParse<SmartView>(CurrentKey[6..], out var view) ? view : null;
    public string? CurrentListId => CurrentKey.StartsWith("list:", StringComparison.Ordinal) ? CurrentKey[5..] : null;
    public bool IsCalendarView => CurrentKey == CalendarKey;
    public bool IsSummaryView => CurrentKey == SummaryKey;
    public bool IsAccountView => CurrentKey == AccountKey;
    public bool IsTaskView => !IsCalendarView && !IsSummaryView && !IsAccountView;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public DateOnly Today => _board?.Today ?? DateOnly.FromDateTime(DateTime.Today);

    public string ViewTitle { get; private set; } = "My Day";
    public string ViewGlyph { get; private set; } = "";
    public string ViewSubtitle { get; private set; } = "";
    public Brush? ViewAccent { get; private set; }
    public bool HasTint { get; private set; }
    public string EmptyTitle { get; private set; } = "";
    public string EmptyText { get; private set; } = "";
    public bool IsEmpty => IsTaskView && Items.Count == 0;
    public string CountText { get; private set; } = "";

    public string QuickAddText { get; set; } = "";
    public string QuickAddHint { get; private set; } = "";
    public bool HasQuickAddHint => QuickAddHint.Length > 0;
    public string SearchText { get; set; } = "";

    public IReadOnlyList<string> SortOptions { get; } = Enum.GetValues<TaskSortMode>().Select(TaskSorting.Label).ToList();
    public IReadOnlyList<string> StatusOptions { get; } = ["Open tasks", "All tasks", "Completed only"];
    public int SortIndex { get; private set; }
    public bool SortDescending { get; private set; }
    public int StatusIndex => (int)_prefs.StatusFilter;
    public bool ImportantOnly => _prefs.ImportantOnlyFilter;
    public bool CanReorder => IsTaskView && !IsSearching && CurrentSortMode == TaskSortMode.Manual && (CurrentListId is not null || CurrentSmart is SmartView.MyDay or SmartView.Important or SmartView.All or SmartView.Unscheduled);
    public bool ShowCounts => _prefs.ShowCounts;
    public bool StatusFilterApplies => CurrentSmart is not { } view || !SmartViews.FixesStatus(view);

    public int SelectedCount => _selectedIds.Count;
    public bool HasBulkSelection => _selectedIds.Count > 1;
    public string BulkText => $"{_selectedIds.Count} selected";
    public IReadOnlyList<string> SelectedIds => _selectedIds;

    public string ToastText { get; private set; } = "";
    public bool HasToast => ToastText.Length > 0;
    public bool ToastCanUndo { get; private set; }
    public bool HasActiveReminders => ActiveReminders.Count > 0;

    // ---------- Detail ----------

    public string? DetailId => _detailId;
    public bool HasDetail => _detailId is not null && _prefs.DetailsOpen && !IsSummaryView && !IsAccountView;
    public string DetailTitle { get; set; } = "";
    public string DetailNotes { get; set; } = "";
    public bool DetailCompleted { get; private set; }
    public bool DetailImportant { get; private set; }
    public bool DetailInMyDay { get; private set; }
    public string DetailMyDayText => DetailInMyDay ? "Added to My Day" : "Add to My Day";
    public string DetailDueText { get; set; } = "";
    public string DetailDueLabel { get; private set; } = "";
    public bool DetailOverdue { get; private set; }
    public string DetailTimeText { get; set; } = "";
    public string DetailStartText { get; set; } = "";
    public string DetailStartLabel { get; private set; } = "";
    public int DetailRecurrenceIndex { get; private set; }
    public string DetailRecurrenceText { get; private set; } = "";
    public IReadOnlyList<string> RecurrenceOptions { get; } = ["Does not repeat", "Daily", "Weekdays", "Weekly", "Monthly", "Yearly", "Custom…"];
    public ListChoice? DetailList { get; private set; }
    public string DetailCreatedText { get; private set; } = "";
    public string DetailProgressText { get; private set; } = "";
    public double DetailProgress { get; private set; }
    public bool DetailHasSteps => Steps.Count > 0;
    public string NewStepText { get; set; } = "";

    // ---------- Calendar ----------

    public CalendarMode CalendarMode => _prefs.CalendarMode;
    public bool IsMonthMode => _prefs.CalendarMode == CalendarMode.Month;
    public bool IsWeekMode => _prefs.CalendarMode == CalendarMode.Week;
    public bool IsDayMode => _prefs.CalendarMode == CalendarMode.Day;
    public string CalendarTitle { get; private set; } = "";
    public DateOnly CalendarSelectedDate => _calendarSelected;
    public string CalendarSelectedText => TodoFormat.Date(_calendarSelected, Today);
    public bool HasBacklog => Backlog.Count > 0;

    // ---------- Summary ----------

    public string SummaryText { get; private set; } = "";

    // ---------- Lifecycle ----------

    public async Task InitializeAsync()
    {
        if (_board is null)
            return;
        try
        {
            _prefs = await _services.TaskStore!.LoadPreferencesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            _prefs = new TaskPreferences();
        }

        _calendarAnchor = _calendarSelected = Today;
        var key = _prefs.SelectedView;
        if (key.StartsWith("list:", StringComparison.Ordinal) && _board.FindList(key[5..]) is null)
            key = "smart:MyDay";
        Select(key, save: false);
    }

    public void Select(string key, bool save = true)
    {
        if (_board is null)
            return;
        if (key.StartsWith("group:", StringComparison.Ordinal))
            return;
        CurrentKey = key;
        _prefs.SelectedView = key;
        if (save)
            SavePrefs();
        if (IsAccountView)
            _ = Host.RefreshAccountTasksAsync();
        _selectedIds = [];
        if (!IsCalendarView)
            _detailId = null;
        SearchText = IsTaskView ? SearchText : "";
        var sort = SortFor(key);
        SortIndex = (int)sort;
        SortDescending = _prefs.SortDescendingByView.GetValueOrDefault(key);
        Refresh();
        OnPropertyChanged(nameof(CurrentKey));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SortIndex));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(IsCalendarView));
        OnPropertyChanged(nameof(IsSummaryView));
        OnPropertyChanged(nameof(IsAccountView));
        OnPropertyChanged(nameof(IsTaskView));
        OnPropertyChanged(nameof(StatusFilterApplies));
    }

    /// <summary>Rebuilds everything from the board. Cheap enough to run after each change, even with thousands of tasks.</summary>
    public void Refresh()
    {
        if (_board is null)
            return;
        RebuildSidebar();
        RebuildListChoices();
        RebuildHeader();
        RebuildItems();
        RebuildDetail();
        if (IsCalendarView)
            BuildCalendar();
        if (IsSummaryView)
            BuildSummary();
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---------- Sidebar ----------

    private void RebuildSidebar()
    {
        var board = _board!;
        var items = new List<SidebarItem>();
        foreach (var view in SmartViews.All)
        {
            var count = view == SmartView.Completed ? 0 : board.CountOpen(view);
            items.Add(new SidebarItem
            {
                Key = "smart:" + view,
                Kind = SidebarKind.Smart,
                Title = SmartViews.Title(view),
                Glyph = SmartViews.Glyph(view),
                Icon = IconFor(view),
                Count = _prefs.ShowCounts ? count : 0,
                IsWarning = view == SmartView.Overdue && count > 0,
            });
        }

        items.Add(new SidebarItem { Key = CalendarKey, Kind = SidebarKind.Special, Title = "Calendar", Glyph = "", Icon = "Calendar" });
        items.Add(new SidebarItem { Key = SummaryKey, Kind = SidebarKind.Special, Title = "Summary", Glyph = "", Icon = "Chart" });
        items.Add(new SidebarItem { Key = "header:lists", Kind = SidebarKind.Header, Title = "Lists" });
        var lists = board.Lists;
        foreach (var list in lists.Where(item => item.GroupId is null))
            items.Add(ListItem(list, 0));
        foreach (var group in board.Groups)
        {
            var children = lists.Where(item => item.GroupId == group.Id).ToList();
            items.Add(new SidebarItem
            {
                Key = "group:" + group.Id,
                Kind = SidebarKind.Group,
                GroupId = group.Id,
                Title = group.Name,
                Glyph = "",
                Icon = "Group",
                Collapsed = group.Collapsed,
                Count = _prefs.ShowCounts && group.Collapsed ? children.Sum(item => board.CountOpen(item.Id)) : 0,
            });
            if (!group.Collapsed)
            {
                foreach (var list in children)
                    items.Add(ListItem(list, 16));
            }
        }

        if (Host.Config.Accounts.Any(account => account.Kind != Alondra.Core.Config.AccountKind.Manual) || Host.IsDemo)
        {
            items.Add(new SidebarItem { Key = "header:accounts", Kind = SidebarKind.Header, Title = "Accounts" });
            items.Add(new SidebarItem { Key = AccountKey, Kind = SidebarKind.Special, Title = "Account tasks", Glyph = "", Icon = "Contacts" });
        }

        Sync(Sidebar, items, (a, b) => a.Key == b.Key && a.Title == b.Title && a.Count == b.Count && a.Collapsed == b.Collapsed && Equals(a.Accent?.ToString(), b.Accent?.ToString()));

        SidebarItem ListItem(TaskList list, double indent) => new()
        {
            Key = "list:" + list.Id,
            Kind = SidebarKind.List,
            ListId = list.Id,
            GroupId = list.GroupId,
            Title = list.Name,
            Glyph = list.IsDefault ? "" : "",
            Icon = list.IsDefault ? "Home" : "List",
            Accent = TodoFormat.Brush(list.Color),
            Indent = indent,
            Count = _prefs.ShowCounts ? board.CountOpen(list.Id) : 0,
        };
    }

    private void RebuildListChoices()
    {
        var choices = _board!.Lists.Select(list => new ListChoice { Id = list.Id, Name = list.Name }).ToList();
        Sync(ListChoices, choices, (a, b) => a.Id == b.Id && a.Name == b.Name);
    }

    // ---------- Header and items ----------

    private void RebuildHeader()
    {
        var board = _board!;
        ViewAccent = null;
        HasTint = false;
        ViewSubtitle = "";
        if (IsCalendarView)
        {
            ViewTitle = "Calendar";
            ViewGlyph = "";
        }
        else if (IsSummaryView)
        {
            ViewTitle = "Summary";
            ViewGlyph = "";
        }
        else if (IsAccountView)
        {
            ViewTitle = "Account tasks";
            ViewGlyph = "";
            ViewSubtitle = "To Do or Google Tasks from a signed-in account. These are not stored in the local task lists.";
        }
        else if (CurrentListId is { } listId && board.FindList(listId) is { } list)
        {
            ViewTitle = list.Name;
            ViewGlyph = list.IsDefault ? "" : "";
            ViewAccent = TodoFormat.Brush(list.Color);
            HasTint = list.TintBackground && ViewAccent is not null;
            if (list.GroupId is { } group && board.FindGroup(group) is { } owner)
                ViewSubtitle = owner.Name;
        }
        else if (CurrentSmart is { } view)
        {
            ViewTitle = SmartViews.Title(view);
            ViewGlyph = SmartViews.Glyph(view);
            if (view == SmartView.MyDay)
                ViewSubtitle = Today.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        }

        if (IsSearching && IsTaskView)
        {
            ViewTitle = $"Search: {SearchText.Trim()}";
            ViewGlyph = "";
            ViewSubtitle = "All lists";
        }

        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(ViewGlyph));
        OnPropertyChanged(nameof(ViewIcon));
        OnPropertyChanged(nameof(ViewSubtitle));
        OnPropertyChanged(nameof(ViewAccent));
        OnPropertyChanged(nameof(HasTint));
    }

    public TaskSortMode CurrentSortMode => (TaskSortMode)SortIndex;

    private TaskSortMode SortFor(string key) =>
        _prefs.SortByView.TryGetValue(key, out var sort) ? sort
        : key.StartsWith("smart:", StringComparison.Ordinal) && Enum.TryParse<SmartView>(key[6..], out var view) ? SmartViews.DefaultSort(view)
        : TaskSortMode.Manual;

    public void RebuildItems()
    {
        var board = _board!;
        var today = Today;
        List<TodoTask> tasks;
        var filter = new TaskFilter { Status = _prefs.StatusFilter, ImportantOnly = _prefs.ImportantOnlyFilter, Query = SearchText };
        var showList = true;
        if (!IsTaskView)
            tasks = [];
        else if (IsSearching)
            tasks = board.Search(SearchText, includeCompleted: _prefs.StatusFilter != TaskStatusFilter.Open)
                .Where(task => !_prefs.ImportantOnlyFilter || task.Important)
                .Where(task => _prefs.StatusFilter != TaskStatusFilter.Completed || task.Completed).ToList();
        else if (CurrentListId is { } listId)
        {
            tasks = board.QueryList(listId, filter, CurrentSortMode, SortDescending);
            showList = false;
        }
        else
            tasks = board.Query(CurrentSmart ?? SmartView.MyDay, filter, CurrentSortMode, SortDescending);
        if (_prefs.StatusFilter == TaskStatusFilter.All)
            tasks = tasks.OrderBy(task => task.Completed).ToList();

        var accents = board.Lists.ToDictionary(list => list.Id, list => TodoFormat.Brush(list.Color));
        var rows = tasks.Select(task => new TaskItemVm(task, board.ListName(task.ListId), accents.GetValueOrDefault(task.ListId), today, showList)).ToList();
        // Rows are rebuilt after every change. The view keeps the selection, keyboard focus, and details pane across it.
        ItemsRebuilding?.Invoke();
        IsRebuilding = true;
        try
        {
            Items.Clear();
            foreach (var row in rows)
                Items.Add(row);
        }
        finally
        {
            IsRebuilding = false;
        }

        var present = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        _selectedIds = _selectedIds.Where(present.Contains).ToList();
        ItemsRebuilt?.Invoke();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasBulkSelection));
        OnPropertyChanged(nameof(BulkText));

        var open = tasks.Count(task => !task.Completed);
        CountText = tasks.Count == 0 ? "" : open == tasks.Count ? $"{open} open" : $"{open} open · {tasks.Count - open} completed";
        (EmptyTitle, EmptyText) = EmptyState();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(IsSearching));
    }

    private (string, string) EmptyState()
    {
        if (IsSearching)
            return ("No matching tasks", "Search looks at titles, notes, steps, and list names in every list.");
        if (_prefs.ImportantOnlyFilter || _prefs.StatusFilter == TaskStatusFilter.Completed)
            return ("Nothing matches the filter", "Change the filter above to see more tasks.");
        return CurrentSmart switch
        {
            SmartView.MyDay => ("Focus on your day", "Add tasks here, or press Ctrl+T on any task to add it to My Day. My Day starts empty each morning."),
            SmartView.Important => ("No important tasks", "Star a task to see it here."),
            SmartView.Planned => ("Nothing planned", "Tasks with a due date or start date show up here."),
            SmartView.Upcoming => ("Nothing coming up", "Open tasks due after today show up here in date order."),
            SmartView.Overdue => ("You're all caught up", "No open task is past its due date."),
            SmartView.Completed => ("No completed tasks yet", "Completed tasks stay here until you delete them."),
            SmartView.Unscheduled => ("Everything is scheduled", "Tasks without a date show up here."),
            SmartView.All => ("No tasks yet", "Type in the box below to add your first task."),
            _ => ("This list is empty", "Type in the box below to add a task. Try \"Call Sam tomorrow at 3pm\"."),
        };
    }

    // ---------- Detail pane ----------

    public void SetSelection(IReadOnlyList<string> ids)
    {
        _selectedIds = ids;
        if (ids.Count == 1)
            ShowDetail(ids[0]);
        else if (ids.Count == 0 && !IsCalendarView)
        {
            _detailId = null;
            RaiseDetail();
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasBulkSelection));
        OnPropertyChanged(nameof(BulkText));
    }

    public void ShowDetail(string id)
    {
        if (_detailId != id)
        {
            _titleDirty = false;
            _notesDirty = false;
        }

        _detailId = id;
        if (!_prefs.DetailsOpen)
        {
            _prefs.DetailsOpen = true;
            SavePrefs();
        }

        RebuildDetail();
    }

    private void RebuildDetail()
    {
        if (_detailId is null || _board!.FindTask(_detailId) is not { } task)
        {
            _detailId = null;
            Steps.Clear();
            Reminders.Clear();
            Attachments.Clear();
            RaiseDetail();
            return;
        }

        var today = Today;
        if (!_titleDirty)
            DetailTitle = task.Title;
        if (!_notesDirty)
            DetailNotes = task.Notes;
        DetailCompleted = task.Completed;
        DetailImportant = task.Important;
        DetailInMyDay = task.MyDayDate == today;
        DetailDueText = task.DueDate?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        DetailDueLabel = task.DueDate is { } due ? (task.IsOverdue(today) ? "Overdue · " : "Due ") + TodoFormat.Date(due, today) : "No due date";
        DetailOverdue = task.IsOverdue(today);
        DetailTimeText = task.DueTime is { } time ? TodoFormat.Time(time) : "";
        DetailStartText = task.StartDate?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        DetailStartLabel = task.StartDate is { } start ? "Starts " + TodoFormat.Date(start, today) : "No start date";
        DetailRecurrenceIndex = RecurrenceIndex(task.Recurrence);
        DetailRecurrenceText = task.Recurrence is null ? "" : Recurrence.Describe(task.Recurrence) + (task.DueDate is { } next ? " · next " + TodoFormat.Date(next, today) : "");
        DetailList = ListChoices.FirstOrDefault(choice => choice.Id == task.ListId);
        DetailCreatedText = "Created " + TodoFormat.Date(DateOnly.FromDateTime(task.CreatedUtc.ToLocalTime().DateTime), today)
            + (task.Completed && task.CompletedUtc is { } done ? " · Completed " + TodoFormat.Date(DateOnly.FromDateTime(done.ToLocalTime().DateTime), today) : "");
        var (doneSteps, total) = task.SubtaskProgress;
        DetailProgress = total == 0 ? 0 : (double)doneSteps / total;
        DetailProgressText = total == 0 ? "" : $"{doneSteps} of {total} steps";

        Sync(Steps, task.Subtasks.Select(step => new SubtaskVm { Id = step.Id, Title = step.Title, Completed = step.Completed }).ToList(),
            (a, b) => a.Id == b.Id && a.Title == b.Title && a.Completed == b.Completed);
        var now = _board.LocalNow;
        Sync(Reminders, task.Reminders.Where(item => !item.Dismissed).Select(item => new ReminderVm
        {
            Id = item.Id,
            Text = "Remind me " + TodoFormat.Date(DateOnly.FromDateTime(item.At), today) + " " + TodoFormat.Time(TimeOnly.FromDateTime(item.At))
                   + (item.SnoozedUntilUtc is { } snooze ? " · snoozed to " + TodoFormat.Time(TimeOnly.FromDateTime(snooze.ToLocalTime().DateTime)) : ""),
            Past = item.At < now && item.SnoozedUntilUtc is null,
        }).ToList(), (a, b) => a.Id == b.Id && a.Text == b.Text);
        Sync(Attachments, task.Attachments.Select(item => new AttachmentVm
        {
            Id = item.Id,
            FileName = item.FileName,
            SizeText = TodoFormat.Size(item.Size),
            Missing = _services.TaskFiles?.Resolve(item) is null,
        }).ToList(), (a, b) => a.Id == b.Id);
        RaiseDetail();
    }

    private static int RecurrenceIndex(RecurrenceRule? rule)
    {
        if (rule is null)
            return 0;
        if (rule.Interval != 1 || rule.Until is not null)
            return 6;
        return rule.Frequency switch
        {
            RecurrenceFrequency.Daily => 1,
            RecurrenceFrequency.Weekly when rule.DaysOfWeek.Count == 5 && !rule.DaysOfWeek.Contains(DayOfWeek.Saturday) && !rule.DaysOfWeek.Contains(DayOfWeek.Sunday) => 2,
            RecurrenceFrequency.Weekly when rule.DaysOfWeek.Count <= 1 => 3,
            RecurrenceFrequency.Monthly => 4,
            RecurrenceFrequency.Yearly => 5,
            _ => 6,
        };
    }

    private void RaiseDetail()
    {
        foreach (var name in new[]
        {
            nameof(DetailId), nameof(HasDetail), nameof(DetailTitle), nameof(DetailNotes), nameof(DetailCompleted), nameof(DetailImportant),
            nameof(DetailInMyDay), nameof(DetailMyDayText), nameof(DetailDueText), nameof(DetailDueLabel), nameof(DetailOverdue), nameof(DetailTimeText),
            nameof(DetailStartText), nameof(DetailStartLabel), nameof(DetailRecurrenceIndex), nameof(DetailRecurrenceText), nameof(DetailList),
            nameof(DetailCreatedText), nameof(DetailProgress), nameof(DetailProgressText), nameof(DetailHasSteps),
        })
            OnPropertyChanged(name);
    }

    public void MarkTitleEdited() => _titleDirty = true;
    public void MarkNotesEdited() => _notesDirty = true;

    public async Task CommitTitleAsync()
    {
        if (_detailId is not { } id || !_titleDirty)
            return;
        _titleDirty = false;
        var title = DetailTitle;
        await Run(() => _board!.UpdateTaskAsync(id, task => task.Title = title, "Rename task"));
    }

    public async Task CommitNotesAsync()
    {
        if (_detailId is not { } id || !_notesDirty)
            return;
        _notesDirty = false;
        var notes = DetailNotes;
        await Run(() => _board!.UpdateTaskAsync(id, task => task.Notes = notes, "Edit note"));
    }

    public async Task CommitDueAsync()
    {
        if (_detailId is not { } id)
            return;
        var text = DetailDueText;
        if (text.Trim().Length == 0)
        {
            await Run(() => _board!.UpdateTaskAsync(id, task => { task.DueDate = null; task.DueTime = null; task.Recurrence = null; }, "Remove due date"));
            return;
        }

        if (TodoFormat.ParseDate(text, _board!.LocalNow) is { } date)
            await Run(() => _board.RescheduleAsync(id, date));
        else
        {
            Error($"\"{text}\" is not a date Alondra understands. Try 12/24, 2026-12-24, tomorrow, or next fri.");
            RebuildDetail();
        }
    }

    public async Task CommitTimeAsync()
    {
        if (_detailId is not { } id)
            return;
        var text = DetailTimeText;
        if (text.Trim().Length == 0)
        {
            await Run(() => _board!.UpdateTaskAsync(id, task => task.DueTime = null, "Remove time"));
            return;
        }

        if (TodoFormat.ParseTime(text, _board!.LocalNow) is { } time)
            await Run(() => _board.UpdateTaskAsync(id, task => { task.DueDate ??= _board.Today; task.DueTime = time; }, "Set time"));
        else
        {
            Error($"\"{text}\" is not a time. Try 14:30 or 2:30 PM.");
            RebuildDetail();
        }
    }

    public async Task CommitStartAsync()
    {
        if (_detailId is not { } id)
            return;
        var text = DetailStartText;
        DateOnly? date = null;
        if (text.Trim().Length > 0 && (date = TodoFormat.ParseDate(text, _board!.LocalNow)) is null)
        {
            Error($"\"{text}\" is not a date Alondra understands.");
            RebuildDetail();
            return;
        }

        await Run(() => _board!.UpdateTaskAsync(id, task => task.StartDate = date, date is null ? "Remove start date" : "Set start date"));
    }

    public async Task SetRecurrenceAsync(int index, Func<RecurrenceRule?, RecurrenceRule?>? custom)
    {
        if (_detailId is not { } id || _board!.FindTask(id) is not { } current)
            return;
        RecurrenceRule? rule = index switch
        {
            1 => new RecurrenceRule { Frequency = RecurrenceFrequency.Daily },
            2 => new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] },
            3 => new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly },
            4 => new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly },
            5 => new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly },
            6 => custom?.Invoke(current.Recurrence?.Clone()),
            _ => null,
        };
        if (index == 6 && rule is null)
        {
            RebuildDetail();
            return;
        }

        await Run(() => _board.UpdateTaskAsync(id, task =>
        {
            if (rule is not null)
            {
                task.DueDate ??= rule.Frequency == RecurrenceFrequency.Weekly && rule.DaysOfWeek.Count > 0
                    ? Enumerable.Range(0, 7).Select(offset => _board.Today.AddDays(offset)).First(day => rule.DaysOfWeek.Contains(day.DayOfWeek))
                    : _board.Today;
                rule.Anchor = task.DueDate;
            }

            task.Recurrence = rule;
        }, rule is null ? "Stop repeating" : "Set repeat"));
    }

    public async Task MoveDetailToListAsync(ListChoice? choice)
    {
        if (_detailId is not { } id || choice is null || _board!.FindTask(id)?.ListId == choice.Id)
            return;
        await Run(() => _board.MoveTasksAsync([id], choice.Id), $"Moved to {choice.Name}");
    }

    private async Task AddStepAsync()
    {
        if (_detailId is not { } id || string.IsNullOrWhiteSpace(NewStepText))
            return;
        var text = NewStepText;
        NewStepText = "";
        OnPropertyChanged(nameof(NewStepText));
        await Run(() => _board!.UpdateTaskAsync(id, task => SubtaskEdits.Add(task, text), "Add step"));
    }

    public async Task RenameStepAsync(string stepId, string title)
    {
        if (_detailId is not { } id || string.IsNullOrWhiteSpace(title))
            return;
        await Run(() => _board!.UpdateTaskAsync(id, task =>
        {
            if (task.Subtasks.FirstOrDefault(step => step.Id == stepId) is { } step)
                step.Title = title.Trim();
        }, "Rename step"));
    }

    private async Task PromoteStepAsync(SubtaskVm? step)
    {
        if (step is null || _detailId is not { } id || _board!.FindTask(id) is not { } parent)
            return;
        await Run(async () =>
        {
            await _board.AddTaskAsync(parent.ListId, step.Title, task => task.Completed = step.Completed);
            await _board.UpdateTaskAsync(id, task => SubtaskEdits.Remove(task, step.Id), "Promote step");
        }, "Step turned into a task");
    }

    private async Task SetDuePresetAsync(string? preset)
    {
        if (_detailId is not { } id)
            return;
        var today = Today;
        DateOnly? date = preset switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "nextweek" => Recurrence.WeekStart(today).AddDays(7),
            _ => null,
        };
        if (date is { } value)
            await Run(() => _board!.RescheduleAsync(id, value));
        else
            await Run(() => _board!.UpdateTaskAsync(id, task => { task.DueDate = null; task.DueTime = null; task.Recurrence = null; }, "Remove due date"));
    }

    private async Task AddReminderPresetAsync(string? preset)
    {
        if (_detailId is not { } id || _board!.FindTask(id) is not { } task)
            return;
        var now = _board.LocalNow;
        DateTime? at = preset switch
        {
            "later" => new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(3),
            "tomorrow" => now.Date.AddDays(1).AddHours(9),
            "nextweek" => Recurrence.WeekStart(DateOnly.FromDateTime(now)).AddDays(7).ToDateTime(new TimeOnly(9, 0)),
            "due" when task.DueDate is { } due => due.ToDateTime(task.DueTime ?? new TimeOnly(9, 0)),
            _ => null,
        };
        if (at is { } value)
            await AddReminderAsync(value);
    }

    public async Task AddReminderAsync(DateTime at)
    {
        if (_detailId is not { } id)
            return;
        await Run(() => _board!.UpdateTaskAsync(id, task => task.Reminders.Add(new TaskReminder { At = at }), "Add reminder"),
            "Reminder set for " + TodoFormat.Date(DateOnly.FromDateTime(at), Today) + " " + TodoFormat.Time(TimeOnly.FromDateTime(at)));
    }

    public async Task AttachFilesAsync(IEnumerable<string> paths)
    {
        if (_detailId is not { } id || _services.TaskFiles is not { } files)
            return;
        var added = new List<TaskAttachment>();
        foreach (var path in paths)
        {
            try
            {
                added.Add(files.Import(id, path, _board!.Now));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error($"Could not attach {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (added.Count > 0)
            await Run(() => _board!.UpdateTaskAsync(id, task => task.Attachments.AddRange(added), "Attach file"), added.Count == 1 ? "File attached" : $"{added.Count} files attached");
    }

    public string? ResolveAttachment(string attachmentId)
    {
        if (_detailId is not { } id || _board!.FindTask(id)?.Attachments.FirstOrDefault(item => item.Id == attachmentId) is not { } attachment)
            return null;
        return _services.TaskFiles?.Resolve(attachment);
    }

    public async Task RemoveAttachmentAsync(string attachmentId)
    {
        if (_detailId is not { } id)
            return;
        await Run(() => _board!.UpdateTaskAsync(id, task => task.Attachments.RemoveAll(item => item.Id == attachmentId), "Remove file"), "File removed");
    }

    // ---------- Task actions (single and bulk) ----------

    private async Task QuickAddAsync()
    {
        var text = QuickAddText.Trim();
        if (text.Length == 0 || _board is null)
            return;
        var parsed = NaturalDateParser.Parse(text, _board.LocalNow);
        var listId = CurrentListId ?? _board.DefaultList.Id;
        var smart = CurrentSmart;
        DateOnly? calendarDate = IsCalendarView ? _calendarSelected : null;
        QuickAddText = "";
        QuickAddHint = "";
        OnPropertyChanged(nameof(QuickAddText));
        OnPropertyChanged(nameof(QuickAddHint));
        OnPropertyChanged(nameof(HasQuickAddHint));
        await Run(async () =>
        {
            var task = await _board.AddTaskAsync(listId, parsed.Title, item =>
            {
                item.DueDate = parsed.DueDate ?? calendarDate ?? (smart is SmartView.Planned or SmartView.Upcoming ? _board.Today.AddDays(smart == SmartView.Upcoming ? 1 : 0) : null);
                item.DueTime = parsed.DueTime;
                item.Recurrence = parsed.Recurrence;
                item.Important = parsed.Important || smart == SmartView.Important;
                if (smart == SmartView.MyDay)
                    item.MyDayDate = _board.Today;
                if (parsed.DueTime is { } time && item.DueDate is { } due && due.ToDateTime(time) > _board.LocalNow)
                    item.Reminders.Add(new TaskReminder { At = due.ToDateTime(time) });
            }, atTop: CurrentSortMode == TaskSortMode.Manual);
            if (IsCalendarView)
                ShowDetail(task.Id);
        }, parsed.HasSchedule ? "Added · " + Describe(parsed) : null);
    }

    public void UpdateQuickAddHint()
    {
        var text = QuickAddText.Trim();
        var parsed = text.Length == 0 || _board is null ? null : NaturalDateParser.Parse(text, _board.LocalNow);
        QuickAddHint = parsed is { HasSchedule: true } || parsed?.Important == true ? $"“{parsed!.Title}” · {Describe(parsed)}" : "";
        OnPropertyChanged(nameof(QuickAddHint));
        OnPropertyChanged(nameof(HasQuickAddHint));
    }

    private string Describe(ParsedTaskInput parsed)
    {
        var parts = new List<string>();
        if (parsed.DueDate is { } due)
            parts.Add("due " + TodoFormat.Date(due, Today));
        if (parsed.DueTime is { } time)
            parts.Add("at " + TodoFormat.Time(time) + " with reminder");
        if (parsed.Recurrence is { } rule)
            parts.Add(Recurrence.Describe(rule).ToLower(CultureInfo.CurrentCulture));
        if (parsed.Important)
            parts.Add("important");
        return string.Join(", ", parts);
    }

    public IReadOnlyList<string> TargetIds(string? fallback = null) =>
        _selectedIds.Count > 0 ? _selectedIds : fallback is not null ? [fallback] : _detailId is not null ? [_detailId] : [];

    public Task CompleteAsync(IReadOnlyList<string> ids, bool completed) =>
        ids.Count == 0 ? Task.CompletedTask : Run(() => _board!.SetCompletedAsync(ids, completed), Plural(ids.Count, completed ? "completed" : "reopened"));

    public Task ToggleCompleteAsync(IReadOnlyList<string> ids)
    {
        var allDone = ids.All(id => _board!.FindTask(id)?.Completed == true);
        return CompleteAsync(ids, !allDone);
    }

    public Task DeleteAsync(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? Task.CompletedTask : Run(() => _board!.DeleteTasksAsync(ids), Plural(ids.Count, "deleted"));

    public Task DuplicateAsync(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? Task.CompletedTask : Run(() => _board!.DuplicateTasksAsync(ids), Plural(ids.Count, "duplicated"));

    public Task MoveAsync(IReadOnlyList<string> ids, string listId) =>
        ids.Count == 0 ? Task.CompletedTask : Run(() => _board!.MoveTasksAsync(ids, listId), Plural(ids.Count, "moved to " + _board!.ListName(listId)));

    public Task SetImportantAsync(IReadOnlyList<string> ids, bool? value = null)
    {
        if (ids.Count == 0)
            return Task.CompletedTask;
        var target = value ?? !ids.All(id => _board!.FindTask(id)?.Important == true);
        return Run(async () =>
        {
            foreach (var id in ids)
                await _board!.UpdateTaskAsync(id, task => task.Important = target, "Change importance");
        });
    }

    public Task ToggleMyDayAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
            return Task.CompletedTask;
        var today = Today;
        var add = !ids.All(id => _board!.FindTask(id)?.MyDayDate == today);
        return Run(async () =>
        {
            foreach (var id in ids)
                await _board!.UpdateTaskAsync(id, task => task.MyDayDate = add ? today : null, add ? "Add to My Day" : "Remove from My Day");
        }, add ? Plural(ids.Count, "added to My Day") : Plural(ids.Count, "removed from My Day"));
    }

    public Task SetDueAsync(IReadOnlyList<string> ids, DateOnly? date) =>
        Run(async () =>
        {
            foreach (var id in ids)
            {
                if (date is { } value)
                    await _board!.RescheduleAsync(id, value);
                else
                    await _board!.UpdateTaskAsync(id, task => { task.DueDate = null; task.DueTime = null; task.Recurrence = null; }, "Remove due date");
            }
        }, date is { } d ? Plural(ids.Count, "due " + TodoFormat.Date(d, Today)) : Plural(ids.Count, "unscheduled"));

    public async Task ReorderAsync(string id, int newIndex)
    {
        if (!CanReorder)
        {
            Toast("Choose \"My order\" in Sort to arrange tasks by hand.");
            return;
        }

        var order = Items.Select(item => item.Id).ToList();
        await Run(() => _board!.MoveWithinAsync(order, id, newIndex));
    }

    public Task NudgeAsync(string id, int delta)
    {
        var index = Items.ToList().FindIndex(item => item.Id == id);
        return index < 0 ? Task.CompletedTask : ReorderAsync(id, index + delta);
    }

    public Task RescheduleAsync(string id, DateOnly date, int? hour = null, bool allDay = false) =>
        Run(() => _board!.RescheduleAsync(id, date, hour is { } h ? new TimeOnly(h, 0) : null, clearTime: allDay), "Moved to " + TodoFormat.Date(date, Today) + (hour is { } value ? " " + TodoFormat.Time(new TimeOnly(value, 0)) : ""));

    // ---------- Lists and groups ----------

    public Task<TaskList?> AddListAsync(string name, string? groupId) =>
        RunWithResult(() => _board!.AddListAsync(name, groupId));

    public Task RenameListAsync(string id, string name) => Run(() => _board!.UpdateListAsync(id, list => list.Name = name, "Rename list"));

    public Task UpdateListAsync(string id, string name, string? color, bool tint, string? groupId) =>
        Run(() => _board!.UpdateListAsync(id, list => { list.Name = name; list.Color = color; list.TintBackground = tint; list.GroupId = groupId; }, "Edit list"));

    public Task DeleteListAsync(string id)
    {
        var name = _board!.ListName(id);
        if (CurrentListId == id)
            Select("smart:MyDay");
        return Run(() => _board.DeleteListAsync(id), $"List \"{name}\" deleted");
    }

    public Task DuplicateListAsync(string id) => Run(() => _board!.DuplicateListAsync(id), "List duplicated");

    public Task MoveListAsync(string id, string? groupId, int index) => Run(() => _board!.MoveListAsync(id, groupId, index));

    public Task<ListGroup?> AddGroupAsync(string name) => RunWithResult(() => _board!.AddGroupAsync(name));

    public Task RenameGroupAsync(string id, string name) => Run(() => _board!.UpdateGroupAsync(id, group => group.Name = name, "Rename group"));

    public Task DeleteGroupAsync(string id) => Run(() => _board!.DeleteGroupAsync(id), "Group removed; its lists were kept");

    // ---------- Sorting, filtering, preferences ----------

    public void SetSort(int index)
    {
        if (index < 0 || index == SortIndex)
            return;
        SortIndex = index;
        _prefs.SortByView[CurrentKey] = (TaskSortMode)index;
        SavePrefs();
        OnPropertyChanged(nameof(SortIndex));
        RebuildItems();
    }

    public void ToggleSortDirection()
    {
        SortDescending = !SortDescending;
        _prefs.SortDescendingByView[CurrentKey] = SortDescending;
        SavePrefs();
        OnPropertyChanged(nameof(SortDescending));
        RebuildItems();
    }

    public void SetStatusFilter(int index)
    {
        if (index < 0 || (int)_prefs.StatusFilter == index)
            return;
        _prefs.StatusFilter = (TaskStatusFilter)index;
        SavePrefs();
        OnPropertyChanged(nameof(StatusIndex));
        RebuildItems();
    }

    public void SetImportantOnly(bool value)
    {
        _prefs.ImportantOnlyFilter = value;
        SavePrefs();
        OnPropertyChanged(nameof(ImportantOnly));
        RebuildItems();
    }

    public void SetShowCounts(bool value)
    {
        _prefs.ShowCounts = value;
        SavePrefs();
        OnPropertyChanged(nameof(ShowCounts));
        RebuildSidebar();
    }

    public TaskPreferences Preferences => _prefs;

    public void SetNotifications(bool enabled)
    {
        _prefs.NotificationsEnabled = enabled;
        SavePrefs();
    }

    public void ApplySearch(string text)
    {
        SearchText = text;
        if (!IsTaskView && text.Length > 0)
        {
            Select("smart:All");
            SearchText = text;
        }

        RebuildHeader();
        RebuildItems();
    }

    private void SavePrefs()
    {
        if (_services.TaskStore is not { } store)
            return;
        try
        {
            store.SavePreferencesAsync(_prefs, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Error("Preferences could not be saved: " + ex.Message);
        }
    }

    // ---------- Calendar ----------

    public void BuildCalendar()
    {
        if (_board is null)
            return;
        var today = Today;
        var mode = _prefs.CalendarMode;
        var culture = CultureInfo.CurrentCulture;
        var accents = _board.Lists.ToDictionary(list => list.Id, list => TodoFormat.Brush(list.Color));
        var tasks = _board.Tasks;
        CalendarTaskVm Chip(TodoTask task, DateOnly day) => new()
        {
            Id = task.Id,
            Title = task.Title,
            TimeText = task.DueDate == day && task.DueTime is { } time ? TodoFormat.Time(time) : "",
            Completed = task.Completed,
            Overdue = task.IsOverdue(today),
            Important = task.Important,
            IsStartMarker = task.DueDate != day,
            Accent = accents.GetValueOrDefault(task.ListId),
        };

        IReadOnlyList<DateOnly> days = mode switch
        {
            CalendarMode.Month => TaskCalendar.MonthGrid(_calendarAnchor),
            CalendarMode.Week => TaskCalendar.Week(_calendarAnchor),
            _ => [_calendarAnchor],
        };
        var byDay = days.ToDictionary(day => day, day => TaskCalendar.On(tasks, day).ToList());
        var limit = mode == CalendarMode.Month ? 3 : 50;
        var cells = days.Select(day =>
        {
            var list = byDay[day];
            return new CalendarCellVm
            {
                Date = day,
                DayText = day.Day.ToString(culture),
                Header = mode == CalendarMode.Month ? "" : day.ToString(mode == CalendarMode.Week ? "ddd d" : "dddd, MMMM d", culture),
                InMonth = mode != CalendarMode.Month || day.Month == _calendarAnchor.Month,
                IsToday = day == today,
                IsSelected = day == _calendarSelected,
                IsPast = day < today,
                Tasks = list.Take(limit).Select(task => Chip(task, day)).ToList(),
                MoreText = list.Count > limit ? $"+{list.Count - limit} more" : "",
                AccessName = $"{day.ToString("D", culture)}, {list.Count} tasks" + (list.Any(task => task.IsOverdue(today)) ? ", overdue tasks" : ""),
            };
        }).ToList();
        CalendarCells.Clear();
        foreach (var cell in cells)
            CalendarCells.Add(cell);

        DaySlots.Clear();
        if (mode == CalendarMode.Day)
        {
            var day = _calendarAnchor;
            var dayTasks = byDay[day];
            DaySlots.Add(new DaySlotVm { Label = "All day", Hour = null, Tasks = dayTasks.Where(task => task.DueTime is null || task.DueDate != day).Select(task => Chip(task, day)).ToList() });
            var nowHour = _board.LocalNow.Hour;
            for (var hour = 0; hour < 24; hour++)
            {
                DaySlots.Add(new DaySlotVm
                {
                    Label = TodoFormat.Time(new TimeOnly(hour, 0)),
                    Hour = hour,
                    IsNow = day == today && hour == nowHour,
                    Tasks = dayTasks.Where(task => task.DueDate == day && task.DueTime?.Hour == hour).Select(task => Chip(task, day)).ToList(),
                });
            }
        }

        var backlog = tasks.Where(task => !task.Completed && (task.IsOverdue(today) || (task.DueDate is null && task.StartDate is null)))
            .OrderBy(task => task.DueDate is null).ThenBy(task => task.DueDate).ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(200).Select(task => Chip(task, task.DueDate ?? today)).ToList();
        Sync(Backlog, backlog, (a, b) => a.Id == b.Id && a.Title == b.Title && a.Overdue == b.Overdue);

        CalendarTitle = mode switch
        {
            CalendarMode.Month => _calendarAnchor.ToString("MMMM yyyy", culture),
            CalendarMode.Week => $"{days[0].ToString("MMM d", culture)} – {days[^1].ToString("MMM d, yyyy", culture)}",
            _ => _calendarAnchor.ToString("dddd, MMMM d, yyyy", culture),
        };
        foreach (var name in new[] { nameof(CalendarTitle), nameof(CalendarMode), nameof(IsMonthMode), nameof(IsWeekMode), nameof(IsDayMode), nameof(HasBacklog), nameof(CalendarSelectedDate), nameof(CalendarSelectedText) })
            OnPropertyChanged(name);
    }

    // ---------- Summary ----------

    private void BuildSummary()
    {
        var stats = _board!.Statistics();
        var tiles = new List<StatTile>
        {
            new() { Value = stats.Open.ToString(CultureInfo.CurrentCulture), Label = "Open tasks" },
            new() { Value = stats.Overdue.ToString(CultureInfo.CurrentCulture), Label = "Overdue", Warning = stats.Overdue > 0 },
            new() { Value = stats.DueToday.ToString(CultureInfo.CurrentCulture), Label = "Due today" },
            new() { Value = stats.DueNextSevenDays.ToString(CultureInfo.CurrentCulture), Label = "Due in the next 7 days" },
            new() { Value = stats.CompletedToday.ToString(CultureInfo.CurrentCulture), Label = "Completed today" },
            new() { Value = stats.CompletedLastSevenDays.ToString(CultureInfo.CurrentCulture), Label = "Completed this week" },
            new() { Value = stats.CompletedLastThirtyDays.ToString(CultureInfo.CurrentCulture), Label = "Completed in 30 days" },
            new() { Value = stats.StreakDays.ToString(CultureInfo.CurrentCulture), Label = stats.StreakDays == 1 ? "Day streak" : "Day streak" },
        };
        Tiles.Clear();
        foreach (var tile in tiles)
            Tiles.Add(tile);
        var max = Math.Max(1, stats.LastSevenDays.Max(item => item.Count));
        DayBars.Clear();
        foreach (var (day, count) in stats.LastSevenDays)
            DayBars.Add(new DayBar { Label = day == Today ? "Today" : day.ToString("ddd", CultureInfo.CurrentCulture), Count = count, Height = Math.Max(2, 90.0 * count / max) });
        RecentHistory.Clear();
        foreach (var record in _board.History.OrderByDescending(item => item.CompletedUtc).Take(40))
        {
            var local = record.CompletedUtc.ToLocalTime();
            RecentHistory.Add(new HistoryRow
            {
                Title = record.Title,
                When = TodoFormat.Date(DateOnly.FromDateTime(local.DateTime), Today) + " " + TodoFormat.Time(TimeOnly.FromDateTime(local.DateTime)),
                ListName = _board.ListName(record.ListId),
            });
        }

        SummaryText = stats.CompletedLastSevenDays == 0
            ? "No tasks completed in the last 7 days."
            : $"You completed {stats.CompletedLastSevenDays} task{(stats.CompletedLastSevenDays == 1 ? "" : "s")} in the last 7 days.";
        OnPropertyChanged(nameof(SummaryText));
    }

    // ---------- Reminders ----------

    public void ShowReminder(DueReminder due)
    {
        if (ActiveReminders.Any(item => item.ReminderId == due.Reminder.Id))
            return;
        ActiveReminders.Add(new ActiveReminder
        {
            TaskId = due.Task.Id,
            ReminderId = due.Reminder.Id,
            Title = due.Task.Title,
            When = TodoFormat.Time(TimeOnly.FromDateTime(due.Instant.ToLocalTime().DateTime)),
        });
        RaiseReminders();
    }

    private void RaiseReminders() => OnPropertyChanged(nameof(HasActiveReminders));

    public void OpenTask(string taskId)
    {
        if (_board?.FindTask(taskId) is not { } task)
            return;
        Host.SelectWorkspace("To-do");
        if (!IsCalendarView)
            Select("list:" + task.ListId);
        ShowDetail(taskId);
        TaskOpened?.Invoke(taskId);
    }

    public event Action<string>? TaskOpened;

    // ---------- Undo, toasts, errors ----------

    private async Task UndoAsync()
    {
        if (_board is null || !_board.CanUndo)
        {
            Toast("Nothing to undo");
            return;
        }

        try
        {
            var description = await _board.UndoAsync();
            Toast("Undone: " + description);
        }
        catch (Exception ex)
        {
            Error(ex.Message);
        }
    }

    public void RequestQuickAdd() => FocusQuickAddRequested?.Invoke();

    public void Toast(string text, bool canUndo = false)
    {
        ToastText = text;
        ToastCanUndo = canUndo && _board?.CanUndo == true;
        _toastTimer.Stop();
        _toastTimer.Start();
        OnPropertyChanged(nameof(ToastText));
        OnPropertyChanged(nameof(HasToast));
        OnPropertyChanged(nameof(ToastCanUndo));
    }

    public void Error(string text)
    {
        Toast(text);
        ErrorRaised?.Invoke(text);
    }

    private async Task Run(Func<Task> action, string? done = null)
    {
        if (_board is null)
            return;
        try
        {
            await action();
            if (done is not null)
                Toast(done, canUndo: true);
        }
        catch (Exception ex) when (ex is TodoStorageException or InvalidOperationException or ArgumentException or IOException)
        {
            Error(ex.Message);
            Refresh();
        }
    }

    private async Task<T?> RunWithResult<T>(Func<Task<T>> action) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is TodoStorageException or InvalidOperationException or ArgumentException)
        {
            Error(ex.Message);
            return null;
        }
    }

    private static string Plural(int count, string verb) => count == 1 ? "Task " + verb : $"{count} tasks {verb}";

    /// <summary>Updates a collection in place so list selection and scroll position survive a refresh.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, T, bool> same)
    {
        if (target.Count == source.Count && target.Zip(source).All(pair => same(pair.First, pair.Second)))
            return;
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
