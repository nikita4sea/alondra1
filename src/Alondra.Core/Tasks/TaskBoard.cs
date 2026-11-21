using Alondra.Core.Ports;

namespace Alondra.Core.Tasks;

/// <summary>A set of writes the store applies in one transaction.</summary>
public sealed class TodoChangeSet
{
    public List<TodoTask> UpsertTasks { get; } = [];
    public List<string> DeleteTasks { get; } = [];
    public List<TaskList> UpsertLists { get; } = [];
    public List<string> DeleteLists { get; } = [];
    public List<ListGroup> UpsertGroups { get; } = [];
    public List<string> DeleteGroups { get; } = [];
    public List<CompletionRecord> UpsertHistory { get; } = [];
    public List<string> DeleteHistory { get; } = [];

    public bool IsEmpty =>
        UpsertTasks.Count + DeleteTasks.Count + UpsertLists.Count + DeleteLists.Count +
        UpsertGroups.Count + DeleteGroups.Count + UpsertHistory.Count + DeleteHistory.Count == 0;
}

public interface ITaskStore
{
    Task<TodoData> LoadAsync(CancellationToken cancellationToken);
    Task ApplyAsync(TodoChangeSet changes, CancellationToken cancellationToken);
    Task ReplaceAllAsync(TodoData data, CancellationToken cancellationToken);
    Task<TaskPreferences> LoadPreferencesAsync(CancellationToken cancellationToken);
    Task SavePreferencesAsync(TaskPreferences preferences, CancellationToken cancellationToken);
}

public sealed class TodoStorageException : Exception
{
    public TodoStorageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Business logic for local tasks. Each change is written to the store in one transaction before memory changes,
/// so a storage failure leaves the board as it was. Undoable changes keep their inverse.
/// The board is single-threaded: awaits resume on the caller's context, so in the app every change and every
/// <see cref="Changed"/> notification happens on the UI thread.
/// </summary>
public sealed class TaskBoard
{
    public const string DefaultListName = "Tasks";
    private const int UndoDepth = 50;

    private readonly ITaskStore _store;
    private readonly IClock _clock;
    private readonly Dictionary<string, TodoTask> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskList> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletionRecord> _history = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Description, TodoChangeSet Inverse)> _undo = new();

    public TaskBoard(ITaskStore store, IClock clock, TimeZoneInfo? zone = null)
    {
        _store = store;
        _clock = clock;
        Zone = zone ?? TimeZoneInfo.Local;
    }

    public event Action? Changed;

    public TimeZoneInfo Zone { get; }
    public DateTimeOffset Now => _clock.UtcNow;
    public DateTime LocalNow => TimeZoneInfo.ConvertTime(_clock.UtcNow, Zone).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public IReadOnlyCollection<TodoTask> Tasks => _tasks.Values;
    public IReadOnlyList<TaskList> Lists => _lists.Values.OrderBy(item => item.Position).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    public IReadOnlyList<ListGroup> Groups => _groups.Values.OrderBy(item => item.Position).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    public IReadOnlyCollection<CompletionRecord> History => _history.Values;
    public bool CanUndo => _undo.Count > 0;
    public string? UndoDescription => _undo.First?.Value.Description;
    public TaskList DefaultList => _lists.Values.First(item => item.IsDefault);

    public TodoTask? FindTask(string id) => _tasks.GetValueOrDefault(id);
    public TaskList? FindList(string id) => _lists.GetValueOrDefault(id);
    public ListGroup? FindGroup(string id) => _groups.GetValueOrDefault(id);

    public TodoData Snapshot() => new()
    {
        Groups = _groups.Values.Select(item => item.Clone()).ToList(),
        Lists = _lists.Values.Select(item => item.Clone()).ToList(),
        Tasks = _tasks.Values.Select(item => item.Clone()).ToList(),
        History = _history.Values.Select(item => item.Clone()).ToList(),
    };

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var data = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        Fill(data);
        if (!_lists.Values.Any(item => item.IsDefault))
        {
            var changes = new TodoChangeSet();
            var existing = _lists.Values.FirstOrDefault(item => item.Name == DefaultListName && item.GroupId is null);
            var list = existing?.Clone() ?? new TaskList { Name = DefaultListName, CreatedUtc = Now, Position = -1 };
            list.IsDefault = true;
            changes.UpsertLists.Add(list);
            await _store.ApplyAsync(changes, cancellationToken).ConfigureAwait(true);
            _lists[list.Id] = list;
        }

        _undo.Clear();
        Changed?.Invoke();
    }

    // ---------- Queries ----------

    public IReadOnlyDictionary<string, double> ListOrder() => _lists.Values.ToDictionary(item => item.Id, item => item.Position);

    public string ListName(string listId) => _lists.TryGetValue(listId, out var list) ? list.Name : "";

    public List<TodoTask> Query(SmartView view, TaskFilter filter, TaskSortMode sort, bool descending)
    {
        var today = Today;
        var tasks = TaskSearch.Apply(SmartViews.Filter(_tasks.Values, view, today), filter, !SmartViews.FixesStatus(view), ListName);
        return TaskSorting.Sort(tasks, sort, descending, ListOrder());
    }

    public List<TodoTask> QueryList(string listId, TaskFilter filter, TaskSortMode sort, bool descending)
    {
        var tasks = TaskSearch.Apply(_tasks.Values.Where(task => task.ListId == listId), filter, true, ListName);
        return TaskSorting.Sort(tasks, sort, descending, ListOrder());
    }

    /// <summary>Instant local search across every list.</summary>
    public List<TodoTask> Search(string query, bool includeCompleted = true) =>
        TaskSorting.Sort(
            TaskSearch.Apply(_tasks.Values, new TaskFilter { Query = query, Status = includeCompleted ? TaskStatusFilter.All : TaskStatusFilter.Open }, true, ListName),
            TaskSortMode.Manual, false, ListOrder())
        .OrderBy(task => task.Completed).ToList();

    public int CountOpen(SmartView view)
    {
        var today = Today;
        return _tasks.Values.Count(task => SmartViews.Includes(task, view, today) && (view == SmartView.Completed || !task.Completed));
    }

    public int CountOpen(string listId) => _tasks.Values.Count(task => task.ListId == listId && !task.Completed);

    /// <summary>Tasks shown on a calendar day: due on that day, or (when enabled) starting on it.</summary>
    public List<TodoTask> OnDate(DateOnly date, bool includeCompleted = true) =>
        _tasks.Values.Where(task => task.DueDate == date && (includeCompleted || !task.Completed))
            .OrderBy(task => task.DueTime ?? TimeOnly.MaxValue).ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    public TodoStatistics Statistics() => TodoStatistics.Compute(new TodoData { Tasks = _tasks.Values.ToList(), History = _history.Values.ToList() }, Today, Zone);

    // ---------- Tasks ----------

    public async Task<TodoTask> AddTaskAsync(string listId, string title, Action<TodoTask>? configure = null, bool atTop = true)
    {
        if (!_lists.ContainsKey(listId))
            listId = DefaultList.Id;
        var siblings = _tasks.Values.Where(task => task.ListId == listId).Select(task => task.Position).ToList();
        var task = new TodoTask
        {
            ListId = listId,
            Title = Clean(title, "Untitled task"),
            CreatedUtc = Now,
            ModifiedUtc = Now,
            Position = siblings.Count == 0 ? 0 : atTop ? siblings.Min() - 1 : siblings.Max() + 1,
        };
        configure?.Invoke(task);
        Normalize(task);
        var changes = new TodoChangeSet();
        changes.UpsertTasks.Add(task);
        await CommitAsync("Add task", changes).ConfigureAwait(true);
        return task;
    }

    /// <summary>Edits a copy of a task and saves it. Title, notes, dates, steps, reminders, recurrence, and files go through here.</summary>
    public async Task<TodoTask?> UpdateTaskAsync(string id, Action<TodoTask> edit, string description = "Edit task", bool undoable = true)
    {
        if (!_tasks.TryGetValue(id, out var current))
            return null;
        var copy = current.Clone();
        edit(copy);
        copy.Title = Clean(copy.Title, current.Title);
        copy.ModifiedUtc = Now;
        Normalize(copy);
        var changes = new TodoChangeSet();
        changes.UpsertTasks.Add(copy);
        await CommitAsync(description, changes, undoable).ConfigureAwait(true);
        return copy;
    }

    public async Task SetCompletedAsync(IReadOnlyCollection<string> ids, bool completed)
    {
        var changes = new TodoChangeSet();
        foreach (var id in ids)
        {
            if (!_tasks.TryGetValue(id, out var current) || current.Completed == completed)
                continue;
            var copy = current.Clone();
            copy.ModifiedUtc = Now;
            if (completed)
            {
                changes.UpsertHistory.Add(new CompletionRecord
                {
                    TaskId = copy.Id,
                    Title = copy.Title,
                    ListId = copy.ListId,
                    CompletedUtc = Now,
                    OccurrenceDate = copy.DueDate,
                });
                if (copy.Recurrence is { } rule && copy.DueDate is { } due && Recurrence.Next(rule, due) is { } next)
                {
                    // A repeating task stays a single record: its due date moves to the next occurrence.
                    var startShift = next.DayNumber - due.DayNumber;
                    copy.DueDate = next;
                    if (copy.StartDate is { } start)
                        copy.StartDate = start.AddDays(startShift);
                    ReminderSchedule.ShiftForRecurrence(copy, due, next);
                    foreach (var step in copy.Subtasks)
                    {
                        step.Completed = false;
                        step.CompletedUtc = null;
                    }

                    if (copy.MyDayDate == Today)
                        copy.MyDayDate = null;
                }
                else
                {
                    copy.Completed = true;
                    copy.CompletedUtc = Now;
                }
            }
            else
            {
                copy.Completed = false;
                copy.CompletedUtc = null;
            }

            changes.UpsertTasks.Add(copy);
        }

        var label = completed ? ids.Count == 1 ? "Complete task" : $"Complete {ids.Count} tasks" : ids.Count == 1 ? "Reopen task" : $"Reopen {ids.Count} tasks";
        await CommitAsync(label, changes).ConfigureAwait(true);
    }

    public async Task DeleteTasksAsync(IReadOnlyCollection<string> ids)
    {
        var changes = new TodoChangeSet();
        changes.DeleteTasks.AddRange(ids.Where(_tasks.ContainsKey));
        await CommitAsync(changes.DeleteTasks.Count == 1 ? "Delete task" : $"Delete {changes.DeleteTasks.Count} tasks", changes).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<TodoTask>> DuplicateTasksAsync(IReadOnlyCollection<string> ids)
    {
        var changes = new TodoChangeSet();
        foreach (var id in ids)
        {
            if (!_tasks.TryGetValue(id, out var source))
                continue;
            var copy = source.Clone();
            copy.Id = NewId.Create();
            copy.Title = source.Title + " (copy)";
            copy.CreatedUtc = Now;
            copy.ModifiedUtc = Now;
            copy.Position = source.Position + 0.5;
            copy.Subtasks.ForEach(step => step.Id = NewId.Create());
            copy.Reminders.ForEach(reminder => { reminder.Id = NewId.Create(); reminder.DeliveredFor = null; reminder.Dismissed = false; reminder.SnoozedUntilUtc = null; });
            copy.Attachments.ForEach(file => file.Id = NewId.Create());
            changes.UpsertTasks.Add(copy);
        }

        await CommitAsync(changes.UpsertTasks.Count == 1 ? "Duplicate task" : $"Duplicate {changes.UpsertTasks.Count} tasks", changes).ConfigureAwait(true);
        return changes.UpsertTasks;
    }

    public async Task MoveTasksAsync(IReadOnlyCollection<string> ids, string listId)
    {
        if (!_lists.ContainsKey(listId))
            throw new ArgumentException("The target list does not exist.", nameof(listId));
        var top = _tasks.Values.Where(task => task.ListId == listId).Select(task => task.Position).DefaultIfEmpty(0).Min();
        var changes = new TodoChangeSet();
        foreach (var id in ids)
        {
            if (!_tasks.TryGetValue(id, out var current) || current.ListId == listId)
                continue;
            var copy = current.Clone();
            copy.ListId = listId;
            copy.Position = --top;
            copy.ModifiedUtc = Now;
            changes.UpsertTasks.Add(copy);
        }

        await CommitAsync(changes.UpsertTasks.Count == 1 ? $"Move task to {ListName(listId)}" : $"Move {changes.UpsertTasks.Count} tasks to {ListName(listId)}", changes).ConfigureAwait(true);
    }

    /// <summary>
    /// Manual ordering (drag and drop, Alt+Up/Down). <paramref name="orderedIds"/> is the visible order after the move;
    /// tasks are renumbered so their relative order matches it.
    /// </summary>
    public async Task ReorderAsync(IReadOnlyList<string> orderedIds)
    {
        var tasks = orderedIds.Select(id => _tasks.GetValueOrDefault(id)).Where(task => task is not null).Cast<TodoTask>().ToList();
        if (tasks.Count == 0)
            return;
        var slots = tasks.Select(task => task.Position).OrderBy(value => value).ToList();
        if (slots.Distinct().Count() != slots.Count)
            slots = Enumerable.Range(0, tasks.Count).Select(index => slots[0] + index).Select(value => (double)value).ToList();
        var changes = new TodoChangeSet();
        for (var index = 0; index < tasks.Count; index++)
        {
            if (tasks[index].Position.Equals(slots[index]))
                continue;
            var copy = tasks[index].Clone();
            copy.Position = slots[index];
            changes.UpsertTasks.Add(copy);
        }

        await CommitAsync("Reorder tasks", changes).ConfigureAwait(true);
    }

    /// <summary>Moves one task to a new index within a visible order. Used by drag and drop.</summary>
    public Task MoveWithinAsync(IReadOnlyList<string> visibleOrder, string id, int newIndex)
    {
        var order = visibleOrder.ToList();
        if (!order.Remove(id))
            return Task.CompletedTask;
        order.Insert(Math.Clamp(newIndex, 0, order.Count), id);
        return ReorderAsync(order);
    }

    /// <summary>Calendar rescheduling: moves the due date (keeping the time slot unless one is given) and shifts reminders with it.</summary>
    public Task<TodoTask?> RescheduleAsync(string id, DateOnly date, TimeOnly? time = null, bool clearTime = false) =>
        UpdateTaskAsync(id, task =>
        {
            var previous = task.DueDate;
            task.DueDate = date;
            if (time is not null)
                task.DueTime = time;
            else if (clearTime)
                task.DueTime = null;
            if (previous is { } old && old != date)
            {
                var days = date.DayNumber - old.DayNumber;
                if (task.StartDate is { } start)
                    task.StartDate = start.AddDays(days);
                foreach (var reminder in task.Reminders.Where(item => !item.Dismissed))
                {
                    reminder.At = reminder.At.AddDays(days);
                    reminder.DeliveredFor = null;
                    reminder.SnoozedUntilUtc = null;
                }
            }

            if (task.Recurrence is not null)
                task.Recurrence.Anchor = date;
        }, "Reschedule task");

    // ---------- Reminders (not undoable; they track notification state) ----------

    public Task MarkReminderDeliveredAsync(string taskId, string reminderId) =>
        UpdateReminder(taskId, reminderId, reminder => { reminder.DeliveredFor = reminder.At; reminder.SnoozedUntilUtc = null; });

    public Task DismissReminderAsync(string taskId, string reminderId) =>
        UpdateReminder(taskId, reminderId, reminder => { reminder.Dismissed = true; reminder.DeliveredFor = reminder.At; reminder.SnoozedUntilUtc = null; });

    public Task SnoozeReminderAsync(string taskId, string reminderId, TimeSpan delay) =>
        UpdateReminder(taskId, reminderId, reminder => { reminder.DeliveredFor = reminder.At; reminder.SnoozedUntilUtc = Now + delay; });

    public IReadOnlyList<DueReminder> DueReminders() => ReminderSchedule.Due(_tasks.Values, Now, Zone);

    private async Task UpdateReminder(string taskId, string reminderId, Action<TaskReminder> edit)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || task.Reminders.All(item => item.Id != reminderId))
            return;
        await UpdateTaskAsync(taskId, copy => edit(copy.Reminders.First(item => item.Id == reminderId)), "Reminder", undoable: false).ConfigureAwait(true);
    }

    // ---------- Lists and groups ----------

    public async Task<TaskList> AddListAsync(string name, string? groupId = null, string? color = null)
    {
        var list = new TaskList
        {
            Name = Clean(name, "Untitled list"),
            GroupId = groupId is not null && _groups.ContainsKey(groupId) ? groupId : null,
            Color = TodoTransfer.IsHexColor(color) ? color : null,
            CreatedUtc = Now,
            Position = _lists.Count == 0 ? 0 : _lists.Values.Max(item => item.Position) + 1,
        };
        var changes = new TodoChangeSet();
        changes.UpsertLists.Add(list);
        await CommitAsync("Add list", changes).ConfigureAwait(true);
        return list;
    }

    public async Task UpdateListAsync(string id, Action<TaskList> edit, string description = "Edit list")
    {
        if (!_lists.TryGetValue(id, out var current))
            return;
        var copy = current.Clone();
        edit(copy);
        copy.Name = Clean(copy.Name, current.Name);
        copy.IsDefault = current.IsDefault;
        if (copy.GroupId is not null && !_groups.ContainsKey(copy.GroupId))
            copy.GroupId = null;
        if (copy.Color is not null && !TodoTransfer.IsHexColor(copy.Color))
            copy.Color = current.Color;
        var changes = new TodoChangeSet();
        changes.UpsertLists.Add(copy);
        await CommitAsync(description, changes).ConfigureAwait(true);
    }

    /// <summary>Deleting a list deletes the tasks in it (undo restores both). The default list cannot be deleted.</summary>
    public async Task DeleteListAsync(string id)
    {
        if (!_lists.TryGetValue(id, out var list))
            return;
        if (list.IsDefault)
            throw new InvalidOperationException("The default Tasks list cannot be deleted.");
        var changes = new TodoChangeSet();
        changes.DeleteLists.Add(id);
        changes.DeleteTasks.AddRange(_tasks.Values.Where(task => task.ListId == id).Select(task => task.Id));
        await CommitAsync($"Delete list {list.Name}", changes).ConfigureAwait(true);
    }

    public async Task<TaskList?> DuplicateListAsync(string id)
    {
        if (!_lists.TryGetValue(id, out var source))
            return null;
        var copy = source.Clone();
        copy.Id = NewId.Create();
        copy.IsDefault = false;
        copy.Name = source.Name + " (copy)";
        copy.CreatedUtc = Now;
        copy.Position = source.Position + 0.5;
        var changes = new TodoChangeSet();
        changes.UpsertLists.Add(copy);
        foreach (var task in _tasks.Values.Where(item => item.ListId == id))
        {
            var clone = task.Clone();
            clone.Id = NewId.Create();
            clone.ListId = copy.Id;
            clone.CreatedUtc = Now;
            clone.ModifiedUtc = Now;
            clone.Subtasks.ForEach(step => step.Id = NewId.Create());
            clone.Reminders.ForEach(reminder => reminder.Id = NewId.Create());
            clone.Attachments.ForEach(file => file.Id = NewId.Create());
            changes.UpsertTasks.Add(clone);
        }

        await CommitAsync($"Duplicate list {source.Name}", changes).ConfigureAwait(true);
        return copy;
    }

    /// <summary>Places a list in a group (or none) at a position among that group's lists.</summary>
    public async Task MoveListAsync(string id, string? groupId, int index)
    {
        if (!_lists.ContainsKey(id))
            return;
        if (groupId is not null && !_groups.ContainsKey(groupId))
            groupId = null;
        var siblings = Lists.Where(item => item.GroupId == groupId && item.Id != id).ToList();
        var moving = _lists[id].Clone();
        moving.GroupId = groupId;
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), moving);
        var changes = new TodoChangeSet();
        for (var i = 0; i < siblings.Count; i++)
        {
            // Positions are relative within a group; the sidebar renders each group separately.
            var list = siblings[i].Id == id ? moving : siblings[i].Clone();
            if (list.Id != id && list.Position.Equals(i))
                continue;
            list.Position = i;
            changes.UpsertLists.Add(list);
        }

        await CommitAsync("Move list", changes).ConfigureAwait(true);
    }

    public async Task<ListGroup> AddGroupAsync(string name)
    {
        var group = new ListGroup
        {
            Name = Clean(name, "Untitled group"),
            Position = _groups.Count == 0 ? 0 : _groups.Values.Max(item => item.Position) + 1,
        };
        var changes = new TodoChangeSet();
        changes.UpsertGroups.Add(group);
        await CommitAsync("Add group", changes).ConfigureAwait(true);
        return group;
    }

    public async Task UpdateGroupAsync(string id, Action<ListGroup> edit, string description = "Edit group", bool undoable = true)
    {
        if (!_groups.TryGetValue(id, out var current))
            return;
        var copy = current.Clone();
        edit(copy);
        copy.Name = Clean(copy.Name, current.Name);
        var changes = new TodoChangeSet();
        changes.UpsertGroups.Add(copy);
        await CommitAsync(description, changes, undoable).ConfigureAwait(true);
    }

    /// <summary>Deleting a group keeps its lists; they become ungrouped.</summary>
    public async Task DeleteGroupAsync(string id)
    {
        if (!_groups.TryGetValue(id, out var group))
            return;
        var changes = new TodoChangeSet();
        changes.DeleteGroups.Add(id);
        foreach (var list in _lists.Values.Where(item => item.GroupId == id))
        {
            var copy = list.Clone();
            copy.GroupId = null;
            changes.UpsertLists.Add(copy);
        }

        await CommitAsync($"Ungroup {group.Name}", changes).ConfigureAwait(true);
    }

    // ---------- Import, restore, undo ----------

    /// <summary>Adds prepared import data. Identifiers were already replaced, so nothing existing is overwritten.</summary>
    public async Task ImportAsync(TodoData data)
    {
        var collisions = data.Tasks.Any(item => _tasks.ContainsKey(item.Id)) || data.Lists.Any(item => _lists.ContainsKey(item.Id))
            || data.Groups.Any(item => _groups.ContainsKey(item.Id));
        if (collisions)
            throw new InvalidOperationException("The import contains identifiers that already exist. Prepare it with TodoTransfer.PrepareImport first.");
        var changes = new TodoChangeSet();
        changes.UpsertGroups.AddRange(data.Groups);
        changes.UpsertLists.AddRange(data.Lists.Select(item => { item.IsDefault = false; return item; }));
        changes.UpsertTasks.AddRange(data.Tasks);
        changes.UpsertHistory.AddRange(data.History);
        await CommitAsync($"Import {data.Tasks.Count} tasks", changes).ConfigureAwait(true);
    }

    /// <summary>Replaces everything with restored data in one transaction. Clears undo.</summary>
    public async Task ReplaceAllAsync(TodoData data)
    {
        await _store.ReplaceAllAsync(data, CancellationToken.None).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task<string?> UndoAsync()
    {
        if (_undo.First is not { } entry)
            return null;
        await ApplyAsync(entry.Value.Inverse).ConfigureAwait(true);
        _undo.RemoveFirst();
        Changed?.Invoke();
        return entry.Value.Description;
    }

    // ---------- Internals ----------

    private async Task CommitAsync(string description, TodoChangeSet changes, bool undoable = true)
    {
        if (changes.IsEmpty)
            return;
        var inverse = Invert(changes);
        await ApplyAsync(changes).ConfigureAwait(true);
        if (undoable)
        {
            _undo.AddFirst((description, inverse));
            while (_undo.Count > UndoDepth)
                _undo.RemoveLast();
        }

        Changed?.Invoke();
    }

    private async Task ApplyAsync(TodoChangeSet changes)
    {
        try
        {
            await _store.ApplyAsync(changes, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not TodoStorageException)
        {
            throw new TodoStorageException("The change could not be saved, so nothing was changed. " + ex.Message, ex);
        }

        foreach (var id in changes.DeleteTasks)
            _tasks.Remove(id);
        foreach (var id in changes.DeleteLists)
            _lists.Remove(id);
        foreach (var id in changes.DeleteGroups)
            _groups.Remove(id);
        foreach (var id in changes.DeleteHistory)
            _history.Remove(id);
        foreach (var item in changes.UpsertGroups)
            _groups[item.Id] = item.Clone();
        foreach (var item in changes.UpsertLists)
            _lists[item.Id] = item.Clone();
        foreach (var item in changes.UpsertTasks)
            _tasks[item.Id] = item.Clone();
        foreach (var item in changes.UpsertHistory)
            _history[item.Id] = item.Clone();
    }

    private TodoChangeSet Invert(TodoChangeSet changes)
    {
        var inverse = new TodoChangeSet();
        Invert(changes.UpsertTasks.Select(item => item.Id), changes.DeleteTasks, _tasks, item => item.Clone(), inverse.UpsertTasks, inverse.DeleteTasks);
        Invert(changes.UpsertLists.Select(item => item.Id), changes.DeleteLists, _lists, item => item.Clone(), inverse.UpsertLists, inverse.DeleteLists);
        Invert(changes.UpsertGroups.Select(item => item.Id), changes.DeleteGroups, _groups, item => item.Clone(), inverse.UpsertGroups, inverse.DeleteGroups);
        Invert(changes.UpsertHistory.Select(item => item.Id), changes.DeleteHistory, _history, item => item.Clone(), inverse.UpsertHistory, inverse.DeleteHistory);
        return inverse;
    }

    private static void Invert<T>(IEnumerable<string> upserted, IEnumerable<string> deleted, Dictionary<string, T> current, Func<T, T> clone, List<T> restore, List<string> remove)
    {
        foreach (var id in upserted)
        {
            if (current.TryGetValue(id, out var before))
                restore.Add(clone(before));
            else
                remove.Add(id);
        }

        foreach (var id in deleted)
        {
            if (current.TryGetValue(id, out var before))
                restore.Add(clone(before));
        }
    }

    private void Fill(TodoData data)
    {
        _tasks.Clear();
        _lists.Clear();
        _groups.Clear();
        _history.Clear();
        foreach (var item in data.Groups)
            _groups[item.Id] = item;
        foreach (var item in data.Lists)
        {
            if (item.GroupId is not null && !_groups.ContainsKey(item.GroupId))
                item.GroupId = null;
            _lists[item.Id] = item;
        }

        foreach (var item in data.Tasks)
            _tasks[item.Id] = item;
        foreach (var item in data.History)
            _history[item.Id] = item;
        var defaults = _lists.Values.Where(item => item.IsDefault).OrderBy(item => item.CreatedUtc).Skip(1);
        foreach (var extra in defaults)
            extra.IsDefault = false;
        var fallback = _lists.Values.FirstOrDefault(item => item.IsDefault);
        foreach (var task in _tasks.Values.Where(task => !_lists.ContainsKey(task.ListId)))
            task.ListId = fallback?.Id ?? task.ListId;
    }

    private void Normalize(TodoTask task)
    {
        if (task.Recurrence is { } rule)
        {
            rule.Interval = Math.Clamp(rule.Interval, 1, 999);
            task.DueDate ??= Today;
            rule.Anchor ??= task.DueDate;
        }

        if (task.DueDate is null)
            task.DueTime = null;
        task.Reminders = task.Reminders.GroupBy(item => item.At).Select(group => group.First()).OrderBy(item => item.At).ToList();
        if (task.Completed)
            task.CompletedUtc ??= Now;
        else
            task.CompletedUtc = null;
        task.Notes ??= "";
    }

    private static string Clean(string? value, string fallback)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > TodoTransfer.MaxTitle)
            text = text[..TodoTransfer.MaxTitle];
        return text.Length == 0 ? fallback : text;
    }
}

/// <summary>Helpers for editing a task's steps inside <see cref="TaskBoard.UpdateTaskAsync"/>.</summary>
public static class SubtaskEdits
{
    public static void Add(TodoTask task, string title)
    {
        var text = (title ?? "").Trim();
        if (text.Length > 0)
            task.Subtasks.Add(new Subtask { Title = text });
    }

    public static void Toggle(TodoTask task, string id, DateTimeOffset now)
    {
        var step = task.Subtasks.FirstOrDefault(item => item.Id == id);
        if (step is null)
            return;
        step.Completed = !step.Completed;
        step.CompletedUtc = step.Completed ? now : null;
    }

    public static void Move(TodoTask task, string id, int delta)
    {
        var index = task.Subtasks.FindIndex(item => item.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= task.Subtasks.Count)
            return;
        var step = task.Subtasks[index];
        task.Subtasks.RemoveAt(index);
        task.Subtasks.Insert(target, step);
    }

    public static void Remove(TodoTask task, string id) => task.Subtasks.RemoveAll(item => item.Id == id);

    /// <summary>Turns a step into its own task in the same list.</summary>
    public static Subtask? Detach(TodoTask task, string id)
    {
        var step = task.Subtasks.FirstOrDefault(item => item.Id == id);
        if (step is not null)
            task.Subtasks.Remove(step);
        return step;
    }
}
