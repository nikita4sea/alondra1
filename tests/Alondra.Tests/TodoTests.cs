using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Alondra.Core.Ports;
using Alondra.Core.Tasks;
using Alondra.Persistence;
using Microsoft.Data.Sqlite;

namespace Alondra.Tests;

public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>In-memory store that can be told to fail, to check that a failed write leaves the board unchanged.</summary>
public sealed class MemoryTaskStore : ITaskStore
{
    public TodoData Data { get; } = new();
    public TaskPreferences Preferences { get; set; } = new();
    public bool Fail { get; set; }
    public int Writes { get; private set; }

    public Task<TodoData> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new TodoData
    {
        Groups = Data.Groups.Select(item => item.Clone()).ToList(),
        Lists = Data.Lists.Select(item => item.Clone()).ToList(),
        Tasks = Data.Tasks.Select(item => item.Clone()).ToList(),
        History = Data.History.Select(item => item.Clone()).ToList(),
    });

    public Task ApplyAsync(TodoChangeSet changes, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new IOException("disk full");
        Writes++;
        Data.Tasks.RemoveAll(item => changes.DeleteTasks.Contains(item.Id) || changes.UpsertTasks.Any(up => up.Id == item.Id));
        Data.Tasks.AddRange(changes.UpsertTasks.Select(item => item.Clone()));
        Data.Lists.RemoveAll(item => changes.DeleteLists.Contains(item.Id) || changes.UpsertLists.Any(up => up.Id == item.Id));
        Data.Lists.AddRange(changes.UpsertLists.Select(item => item.Clone()));
        Data.Groups.RemoveAll(item => changes.DeleteGroups.Contains(item.Id) || changes.UpsertGroups.Any(up => up.Id == item.Id));
        Data.Groups.AddRange(changes.UpsertGroups.Select(item => item.Clone()));
        Data.History.RemoveAll(item => changes.DeleteHistory.Contains(item.Id) || changes.UpsertHistory.Any(up => up.Id == item.Id));
        Data.History.AddRange(changes.UpsertHistory.Select(item => item.Clone()));
        return Task.CompletedTask;
    }

    public Task ReplaceAllAsync(TodoData data, CancellationToken cancellationToken)
    {
        Data.Tasks.Clear();
        Data.Lists.Clear();
        Data.Groups.Clear();
        Data.History.Clear();
        var changes = new TodoChangeSet();
        changes.UpsertTasks.AddRange(data.Tasks);
        changes.UpsertLists.AddRange(data.Lists);
        changes.UpsertGroups.AddRange(data.Groups);
        changes.UpsertHistory.AddRange(data.History);
        return ApplyAsync(changes, cancellationToken);
    }

    public Task<TaskPreferences> LoadPreferencesAsync(CancellationToken cancellationToken) => Task.FromResult(Preferences.Clone());

    public Task SavePreferencesAsync(TaskPreferences preferences, CancellationToken cancellationToken)
    {
        Preferences = preferences.Clone();
        return Task.CompletedTask;
    }
}

public class TodoTests
{
    // Saturday, 21 November 2026, 10:00 UTC.
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private static async Task<(TaskBoard Board, MemoryTaskStore Store, FixedClock Clock)> NewBoard()
    {
        var store = new MemoryTaskStore();
        var clock = new FixedClock(Start);
        var board = new TaskBoard(store, clock, TimeZoneInfo.Utc);
        await board.LoadAsync();
        return (board, store, clock);
    }

    private static string Temp() => Directory.CreateTempSubdirectory("alondra-todo-").FullName;

    // ---------- Tasks ----------

    [Fact]
    public async Task Board_creates_the_default_list_once()
    {
        var (board, store, clock) = await NewBoard();
        Assert.Single(board.Lists);
        Assert.True(board.DefaultList.IsDefault);
        var again = new TaskBoard(store, clock, TimeZoneInfo.Utc);
        await again.LoadAsync();
        Assert.Single(again.Lists);
    }

    [Fact]
    public async Task Task_create_update_complete_reopen_and_delete()
    {
        var (board, _, clock) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "  Write report  ");
        Assert.Equal("Write report", task.Title);
        Assert.Equal(Start, task.CreatedUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        await board.UpdateTaskAsync(task.Id, item => { item.Notes = "Section 2"; item.Important = true; item.DueDate = Today.AddDays(2); });
        var updated = board.FindTask(task.Id)!;
        Assert.Equal("Section 2", updated.Notes);
        Assert.True(updated.Important);
        Assert.Equal(Start.AddMinutes(5), updated.ModifiedUtc);

        await board.SetCompletedAsync([task.Id], true);
        Assert.True(board.FindTask(task.Id)!.Completed);
        Assert.NotNull(board.FindTask(task.Id)!.CompletedUtc);
        Assert.Single(board.History);

        await board.SetCompletedAsync([task.Id], false);
        Assert.False(board.FindTask(task.Id)!.Completed);
        Assert.Null(board.FindTask(task.Id)!.CompletedUtc);
        Assert.Single(board.History);

        await board.DeleteTasksAsync([task.Id]);
        Assert.Null(board.FindTask(task.Id));
    }

    [Fact]
    public async Task Undo_restores_completion_movement_and_deletion()
    {
        var (board, _, _) = await NewBoard();
        var other = await board.AddListAsync("Errands");
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Buy stamps");

        await board.SetCompletedAsync([task.Id], true);
        Assert.Equal("Complete task", board.UndoDescription);
        await board.UndoAsync();
        Assert.False(board.FindTask(task.Id)!.Completed);
        Assert.Empty(board.History);

        await board.MoveTasksAsync([task.Id], other.Id);
        Assert.Equal(other.Id, board.FindTask(task.Id)!.ListId);
        await board.UndoAsync();
        Assert.Equal(board.DefaultList.Id, board.FindTask(task.Id)!.ListId);

        await board.UpdateTaskAsync(task.Id, item => SubtaskEdits.Add(item, "Find envelope"));
        await board.DeleteTasksAsync([task.Id]);
        Assert.Null(board.FindTask(task.Id));
        await board.UndoAsync();
        var restored = board.FindTask(task.Id)!;
        Assert.Equal("Find envelope", Assert.Single(restored.Subtasks).Title);
    }

    [Fact]
    public async Task A_failed_write_changes_nothing()
    {
        var (board, store, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Keep me");
        store.Fail = true;
        await Assert.ThrowsAsync<TodoStorageException>(() => board.DeleteTasksAsync([task.Id]));
        Assert.NotNull(board.FindTask(task.Id));
        await Assert.ThrowsAsync<TodoStorageException>(() => board.UpdateTaskAsync(task.Id, item => item.Title = "Changed"));
        Assert.Equal("Keep me", board.FindTask(task.Id)!.Title);
        Assert.Equal("Add task", board.UndoDescription);
    }

    [Fact]
    public async Task Subtasks_complete_independently_and_reorder()
    {
        var (board, _, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Trip");
        await board.UpdateTaskAsync(task.Id, item =>
        {
            SubtaskEdits.Add(item, "Book hotel");
            SubtaskEdits.Add(item, "Pack");
            SubtaskEdits.Add(item, "  ");
        });
        var steps = board.FindTask(task.Id)!.Subtasks;
        Assert.Equal(2, steps.Count);
        await board.UpdateTaskAsync(task.Id, item => SubtaskEdits.Toggle(item, steps[1].Id, Start));
        var current = board.FindTask(task.Id)!;
        Assert.False(current.Subtasks[0].Completed);
        Assert.True(current.Subtasks[1].Completed);
        Assert.False(current.Completed);
        Assert.Equal((1, 2), current.SubtaskProgress);

        await board.UpdateTaskAsync(task.Id, item => SubtaskEdits.Move(item, steps[1].Id, -1));
        Assert.Equal("Pack", board.FindTask(task.Id)!.Subtasks[0].Title);
        await board.UpdateTaskAsync(task.Id, item => SubtaskEdits.Remove(item, steps[0].Id));
        Assert.Single(board.FindTask(task.Id)!.Subtasks);
    }

    [Fact]
    public async Task Lists_groups_duplicate_and_delete_behave_consistently()
    {
        var (board, _, _) = await NewBoard();
        var group = await board.AddGroupAsync("Home");
        var list = await board.AddListAsync("🛒 Groceries", group.Id, "#2E7D32");
        Assert.Equal(group.Id, list.GroupId);
        Assert.Equal("🛒 Groceries", list.Name);
        await board.AddTaskAsync(list.Id, "Milk");
        await board.AddTaskAsync(list.Id, "Bread");

        await board.UpdateListAsync(list.Id, item => { item.Name = "Shopping"; item.Color = "not a color"; });
        Assert.Equal("Shopping", board.FindList(list.Id)!.Name);
        Assert.Equal("#2E7D32", board.FindList(list.Id)!.Color);

        var copy = await board.DuplicateListAsync(list.Id);
        Assert.NotNull(copy);
        Assert.Equal(2, board.Tasks.Count(task => task.ListId == copy!.Id));
        Assert.Equal(4, board.Tasks.Select(task => task.Id).Distinct().Count());

        await board.DeleteListAsync(list.Id);
        Assert.Null(board.FindList(list.Id));
        Assert.DoesNotContain(board.Tasks, task => task.ListId == list.Id);
        await board.UndoAsync();
        Assert.NotNull(board.FindList(list.Id));
        Assert.Equal(2, board.Tasks.Count(task => task.ListId == list.Id));

        await board.DeleteGroupAsync(group.Id);
        Assert.Null(board.FindGroup(group.Id));
        Assert.Null(board.FindList(list.Id)!.GroupId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => board.DeleteListAsync(board.DefaultList.Id));
    }

    [Fact]
    public async Task Lists_move_between_groups_and_reorder()
    {
        var (board, _, _) = await NewBoard();
        var group = await board.AddGroupAsync("Work");
        var a = await board.AddListAsync("A", group.Id);
        var b = await board.AddListAsync("B", group.Id);
        var c = await board.AddListAsync("C");
        await board.MoveListAsync(c.Id, group.Id, 0);
        var order = board.Lists.Where(item => item.GroupId == group.Id).Select(item => item.Name).ToList();
        Assert.Equal(["C", "A", "B"], order);
        await board.MoveListAsync(a.Id, null, 0);
        Assert.Null(board.FindList(a.Id)!.GroupId);
        _ = b;
    }

    [Fact]
    public async Task Bulk_complete_move_and_delete()
    {
        var (board, _, _) = await NewBoard();
        var target = await board.AddListAsync("Later");
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add((await board.AddTaskAsync(board.DefaultList.Id, "Task " + i)).Id);

        await board.SetCompletedAsync(ids.Take(3).ToList(), true);
        Assert.Equal(3, board.Tasks.Count(task => task.Completed));
        Assert.Equal("Complete 3 tasks", board.UndoDescription);

        await board.MoveTasksAsync(ids.Skip(3).ToList(), target.Id);
        Assert.Equal(2, board.CountOpen(target.Id));

        await board.DeleteTasksAsync(ids);
        Assert.Empty(board.Tasks);
        await board.UndoAsync();
        Assert.Equal(5, board.Tasks.Count);
    }

    [Fact]
    public async Task Duplicate_task_copies_details_with_new_ids()
    {
        var (board, _, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Plan", item => SubtaskEdits.Add(item, "Step"));
        var copy = Assert.Single(await board.DuplicateTasksAsync([task.Id]));
        Assert.NotEqual(task.Id, copy.Id);
        Assert.Equal("Plan (copy)", copy.Title);
        Assert.NotEqual(task.Subtasks[0].Id, copy.Subtasks[0].Id);
    }

    [Fact]
    public async Task Drag_and_drop_reorders_in_manual_order()
    {
        var (board, _, _) = await NewBoard();
        var list = board.DefaultList.Id;
        foreach (var name in new[] { "C", "B", "A" })
            await board.AddTaskAsync(list, name);
        List<string> Order() => board.QueryList(list, new TaskFilter { Status = TaskStatusFilter.All }, TaskSortMode.Manual, false).Select(task => task.Title).ToList();
        Assert.Equal(["A", "B", "C"], Order());

        var visible = board.QueryList(list, new TaskFilter(), TaskSortMode.Manual, false).Select(task => task.Id).ToList();
        await board.MoveWithinAsync(visible, visible[0], 2);
        Assert.Equal(["B", "C", "A"], Order());

        visible = board.QueryList(list, new TaskFilter(), TaskSortMode.Manual, false).Select(task => task.Id).ToList();
        await board.MoveWithinAsync(visible, visible[2], 0);
        Assert.Equal(["A", "B", "C"], Order());
        await board.UndoAsync();
        Assert.Equal(["B", "C", "A"], Order());
    }

    // ---------- Recurrence ----------

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 1, "2026-11-21", "2026-11-22")]
    [InlineData(RecurrenceFrequency.Daily, 3, "2026-11-21", "2026-11-24")]
    [InlineData(RecurrenceFrequency.Weekly, 1, "2026-11-21", "2026-11-28")]
    [InlineData(RecurrenceFrequency.Weekly, 2, "2026-11-21", "2026-12-05")]
    [InlineData(RecurrenceFrequency.Monthly, 1, "2026-11-21", "2026-12-21")]
    [InlineData(RecurrenceFrequency.Yearly, 1, "2026-11-21", "2027-11-21")]
    public void Recurrence_next_dates(RecurrenceFrequency frequency, int interval, string from, string expected)
    {
        var rule = new RecurrenceRule { Frequency = frequency, Interval = interval };
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), Recurrence.Next(rule, DateOnly.Parse(from, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Monthly_recurrence_clamps_without_drifting()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Anchor = new DateOnly(2027, 1, 31) };
        var dates = Recurrence.Occurrences(rule, new DateOnly(2027, 1, 31), new DateOnly(2027, 1, 1), new DateOnly(2027, 5, 31)).ToList();
        Assert.Equal([new(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31), new(2027, 4, 30), new(2027, 5, 31)], dates);
    }

    [Fact]
    public void Yearly_recurrence_from_leap_day()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, Anchor = new DateOnly(2028, 2, 29) };
        Assert.Equal(new DateOnly(2029, 2, 28), Recurrence.Next(rule, new DateOnly(2028, 2, 29)));
        Assert.Equal(new DateOnly(2032, 2, 29), Recurrence.Next(rule, new DateOnly(2031, 2, 28)));
    }

    [Fact]
    public void Weekly_recurrence_on_chosen_days_and_custom_interval()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 2,
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Thursday],
            Anchor = new DateOnly(2026, 11, 23),
        };
        var dates = Recurrence.Occurrences(rule, new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)).ToList();
        Assert.Equal([new(2026, 11, 23), new(2026, 11, 26), new(2026, 12, 7), new(2026, 12, 10), new(2026, 12, 21), new(2026, 12, 24)], dates);
        Assert.Equal("Every 2 weeks on Mon, Thu", Recurrence.Describe(rule));
    }

    [Fact]
    public void Recurrence_until_ends_the_series_and_is_deterministic()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Until = new DateOnly(2026, 11, 22) };
        Assert.Equal(new DateOnly(2026, 11, 22), Recurrence.Next(rule, Today));
        Assert.Null(Recurrence.Next(rule, new DateOnly(2026, 11, 22)));
        var a = Recurrence.Occurrences(new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = 3 }, Today, Today, Today.AddYears(2)).ToList();
        var b = Recurrence.Occurrences(new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = 3 }, Today, Today, Today.AddYears(2)).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Completing_a_recurring_task_advances_it_without_duplicating()
    {
        var (board, _, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Water plants", item =>
        {
            item.DueDate = Today;
            item.DueTime = new TimeOnly(9, 0);
            item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly };
            item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 8, 30, 0) });
            SubtaskEdits.Add(item, "Balcony");
        });
        await board.UpdateTaskAsync(task.Id, item => item.Subtasks[0].Completed = true);
        await board.SetCompletedAsync([task.Id], true);

        Assert.Single(board.Tasks);
        var next = board.FindTask(task.Id)!;
        Assert.False(next.Completed);
        Assert.Equal(new DateOnly(2026, 11, 28), next.DueDate);
        Assert.Equal(new DateTime(2026, 11, 28, 8, 30, 0), next.Reminders[0].At);
        Assert.False(next.Subtasks[0].Completed);
        var record = Assert.Single(board.History);
        Assert.Equal(Today, record.OccurrenceDate);

        // Completing twice in a row does not skip or duplicate: each completion is one occurrence.
        await board.SetCompletedAsync([task.Id], true);
        Assert.Equal(new DateOnly(2026, 12, 5), board.FindTask(task.Id)!.DueDate);
        Assert.Equal(2, board.History.Count);
        Assert.Single(board.Tasks);

        await board.UndoAsync();
        Assert.Equal(new DateOnly(2026, 11, 28), board.FindTask(task.Id)!.DueDate);
        Assert.Single(board.History);
    }

    [Fact]
    public async Task Recurring_task_with_ended_series_completes_normally()
    {
        var (board, _, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Last one", item =>
        {
            item.DueDate = Today;
            item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Until = Today };
        });
        await board.SetCompletedAsync([task.Id], true);
        Assert.True(board.FindTask(task.Id)!.Completed);
    }

    // ---------- Smart views, search, filter, sort ----------

    [Fact]
    public async Task Smart_views_reference_tasks_without_copying_them()
    {
        var (board, _, _) = await NewBoard();
        var list = board.DefaultList.Id;
        var myDay = await board.AddTaskAsync(list, "Focus", item => item.MyDayDate = Today);
        var important = await board.AddTaskAsync(list, "Star", item => item.Important = true);
        var overdue = await board.AddTaskAsync(list, "Late", item => item.DueDate = Today.AddDays(-1));
        var upcoming = await board.AddTaskAsync(list, "Soon", item => item.DueDate = Today.AddDays(3));
        var started = await board.AddTaskAsync(list, "Started", item => item.StartDate = Today);
        var done = await board.AddTaskAsync(list, "Done", item => item.DueDate = Today.AddDays(-2));
        await board.SetCompletedAsync([done.Id], true);
        var yesterday = await board.AddTaskAsync(list, "Old day", item => item.MyDayDate = Today.AddDays(-1));

        HashSet<string> View(SmartView view) => board.Query(view, new TaskFilter { Status = TaskStatusFilter.All }, TaskSortMode.Manual, false).Select(task => task.Id).ToHashSet();
        Assert.Equal([myDay.Id], View(SmartView.MyDay));
        Assert.Equal([important.Id], View(SmartView.Important));
        Assert.Equal([overdue.Id, upcoming.Id, started.Id, done.Id], View(SmartView.Planned));
        Assert.Equal([upcoming.Id], View(SmartView.Upcoming));
        Assert.Equal([overdue.Id], View(SmartView.Overdue));
        Assert.Equal([done.Id], View(SmartView.Completed));
        Assert.Equal([myDay.Id, important.Id, yesterday.Id], View(SmartView.Unscheduled));
        Assert.Equal(7, View(SmartView.All).Count);
        Assert.Equal(7, board.Tasks.Count);

        // Editing through a view edits the single stored task.
        await board.UpdateTaskAsync(overdue.Id, item => item.Important = true);
        Assert.Contains(overdue.Id, View(SmartView.Important));
        Assert.Contains(overdue.Id, View(SmartView.Overdue));
        Assert.Equal(7, board.Tasks.Count);
        Assert.Equal(1, board.CountOpen(SmartView.Overdue));
    }

    [Fact]
    public async Task Search_filter_and_sort()
    {
        var (board, _, clock) = await NewBoard();
        var errands = await board.AddListAsync("Errands");
        await board.AddTaskAsync(board.DefaultList.Id, "Call the bank", item => { item.DueDate = Today.AddDays(5); item.Notes = "Ask about the café card"; });
        clock.Advance(TimeSpan.FromMinutes(1));
        await board.AddTaskAsync(errands.Id, "Buy apples", item => { item.Important = true; item.DueDate = Today.AddDays(1); });
        clock.Advance(TimeSpan.FromMinutes(1));
        var zebra = await board.AddTaskAsync(errands.Id, "Zebra crossing paint", item => SubtaskEdits.Add(item, "Get white paint"));
        await board.SetCompletedAsync([zebra.Id], true);

        Assert.Single(board.Search("cafe"));
        Assert.Single(board.Search("CALL bank"));
        Assert.Equal(2, board.Search("errands").Count);
        Assert.Single(board.Search("white"));
        Assert.Empty(board.Search("white", includeCompleted: false));
        Assert.Empty(board.Search("bank apples"));

        var all = new TaskFilter { Status = TaskStatusFilter.All };
        Assert.Equal(["Buy apples", "Call the bank", "Zebra crossing paint"], board.Query(SmartView.All, all, TaskSortMode.Alphabetical, false).Select(task => task.Title));
        Assert.Equal(["Zebra crossing paint", "Call the bank", "Buy apples"], board.Query(SmartView.All, all, TaskSortMode.Alphabetical, true).Select(task => task.Title));
        Assert.Equal(["Buy apples", "Call the bank", "Zebra crossing paint"], board.Query(SmartView.All, all, TaskSortMode.DueDate, false).Select(task => task.Title));
        Assert.Equal("Buy apples", board.Query(SmartView.All, all, TaskSortMode.Importance, false)[0].Title);
        Assert.Equal("Zebra crossing paint", board.Query(SmartView.All, all, TaskSortMode.Created, false)[0].Title);
        Assert.Equal(2, board.Query(SmartView.All, new TaskFilter { Status = TaskStatusFilter.Open }, TaskSortMode.Manual, false).Count);
        Assert.Single(board.Query(SmartView.All, new TaskFilter { Status = TaskStatusFilter.Completed }, TaskSortMode.Manual, false));
        Assert.Single(board.Query(SmartView.All, new TaskFilter { ImportantOnly = true, Status = TaskStatusFilter.All }, TaskSortMode.Manual, false));
    }

    [Fact]
    public async Task Statistics_count_completions_streaks_and_deadlines()
    {
        var (board, _, clock) = await NewBoard();
        clock.UtcNow = Start.AddDays(-2);
        var a = await board.AddTaskAsync(board.DefaultList.Id, "A");
        await board.SetCompletedAsync([a.Id], true);
        clock.UtcNow = Start.AddDays(-1);
        var b = await board.AddTaskAsync(board.DefaultList.Id, "B");
        await board.SetCompletedAsync([b.Id], true);
        clock.UtcNow = Start;
        var c = await board.AddTaskAsync(board.DefaultList.Id, "C");
        await board.SetCompletedAsync([c.Id], true);
        await board.AddTaskAsync(board.DefaultList.Id, "Late", item => item.DueDate = Today.AddDays(-1));
        await board.AddTaskAsync(board.DefaultList.Id, "Today", item => item.DueDate = Today);
        await board.AddTaskAsync(board.DefaultList.Id, "Week", item => item.DueDate = Today.AddDays(4));

        var stats = board.Statistics();
        Assert.Equal(3, stats.Open);
        Assert.Equal(1, stats.Overdue);
        Assert.Equal(1, stats.DueToday);
        Assert.Equal(1, stats.DueNextSevenDays);
        Assert.Equal(1, stats.CompletedToday);
        Assert.Equal(3, stats.CompletedLastSevenDays);
        Assert.Equal(3, stats.StreakDays);
        Assert.Equal(7, stats.LastSevenDays.Count);
        Assert.Equal(Today, stats.LastSevenDays[^1].Day);
    }

    // ---------- Natural language ----------

    [Theory]
    [InlineData("Call Sam tomorrow at 3pm", "Call Sam", "2026-11-22", "15:00")]
    [InlineData("Report due friday", "Report", "2026-11-27", null)]
    [InlineData("Report next friday", "Report", "2026-11-27", null)]
    [InlineData("Report next tue", "Report", "2026-11-24", null)]
    [InlineData("Dentist on dec 3", "Dentist", "2026-12-03", null)]
    [InlineData("Taxes 15 April", "Taxes", "2027-04-15", null)]
    [InlineData("Launch 2027-01-05 at 09:30", "Launch", "2027-01-05", "09:30")]
    [InlineData("Renew passport in 2 weeks", "Renew passport", "2026-12-05", null)]
    [InlineData("Plan sprint next week", "Plan sprint", "2026-11-23", null)]
    [InlineData("Read today", "Read", "2026-11-21", null)]
    [InlineData("Movie tonight", "Movie", "2026-11-21", "20:00")]
    [InlineData("Standup 9:15", "Standup", "2026-11-22", "09:15")]
    [InlineData("Buy sun cream", "Buy sun cream", null, null)]
    [InlineData("Sat exam prep", "Sat exam prep", null, null)]
    [InlineData("tomorrow", "tomorrow", null, null)]
    public void Natural_language_dates(string input, string title, string? date, string? time)
    {
        var parsed = NaturalDateParser.Parse(input, new DateTime(2026, 11, 21, 10, 0, 0), CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(title, parsed.Title);
        Assert.Equal(date is null ? null : DateOnly.Parse(date, CultureInfo.InvariantCulture), parsed.DueDate);
        Assert.Equal(time is null ? null : TimeOnly.Parse(time, CultureInfo.InvariantCulture), parsed.DueTime);
    }

    [Fact]
    public void Natural_language_recurrence_importance_and_numeric_dates()
    {
        var now = new DateTime(2026, 11, 21, 10, 0, 0);
        var rent = NaturalDateParser.Parse("Pay rent every month on the 1st", now, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("Pay rent", rent.Title);
        Assert.Equal(RecurrenceFrequency.Monthly, rent.Recurrence!.Frequency);
        Assert.Equal(new DateOnly(2026, 12, 1), rent.DueDate);

        var gym = NaturalDateParser.Parse("Gym every monday !", now, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("Gym", gym.Title);
        Assert.True(gym.Important);
        Assert.Equal([DayOfWeek.Monday], gym.Recurrence!.DaysOfWeek);
        Assert.Equal(new DateOnly(2026, 11, 23), gym.DueDate);

        var biweekly = NaturalDateParser.Parse("Payroll every 2 weeks", now, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(2, biweekly.Recurrence!.Interval);
        Assert.Equal(Today, biweekly.DueDate);

        Assert.Equal(new DateOnly(2026, 12, 3), NaturalDateParser.Parse("Party on 3/12", now, CultureInfo.GetCultureInfo("en-GB")).DueDate);
        Assert.Equal(new DateOnly(2027, 3, 12), NaturalDateParser.Parse("Party on 3/12", now, CultureInfo.GetCultureInfo("en-US")).DueDate);
        Assert.Equal(RecurrenceFrequency.Weekly, NaturalDateParser.Parse("Review every weekday", now).Recurrence!.Frequency);
    }

    // ---------- Reminders, time zones, DST ----------

    [Fact]
    public async Task Reminders_fire_once_and_can_be_snoozed_or_dismissed()
    {
        var (board, _, clock) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Call", item =>
        {
            item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 10, 30, 0) });
            item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 10, 30, 0) });
        });
        Assert.Single(board.FindTask(task.Id)!.Reminders);
        Assert.Empty(board.DueReminders());

        clock.Advance(TimeSpan.FromMinutes(31));
        var due = Assert.Single(board.DueReminders());
        await board.MarkReminderDeliveredAsync(task.Id, due.Reminder.Id);
        Assert.Empty(board.DueReminders());

        await board.SnoozeReminderAsync(task.Id, due.Reminder.Id, TimeSpan.FromMinutes(10));
        Assert.Empty(board.DueReminders());
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(board.DueReminders());
        await board.DismissReminderAsync(task.Id, due.Reminder.Id);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Empty(board.DueReminders());
        Assert.False(board.CanUndo && board.UndoDescription == "Reminder");
    }

    [Fact]
    public async Task Completed_tasks_do_not_remind()
    {
        var (board, _, clock) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Done", item => item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 10, 5, 0) }));
        await board.SetCompletedAsync([task.Id], true);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(board.DueReminders());
    }

    [Fact]
    public void Reminder_times_follow_the_local_zone_across_daylight_saving()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        // 09:00 local before and after the 2027-03-14 change: the UTC instant moves by one hour.
        Assert.Equal(new DateTimeOffset(2027, 3, 13, 14, 0, 0, TimeSpan.Zero), ReminderSchedule.ToInstant(new DateTime(2027, 3, 13, 9, 0, 0), eastern).ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2027, 3, 15, 13, 0, 0, TimeSpan.Zero), ReminderSchedule.ToInstant(new DateTime(2027, 3, 15, 9, 0, 0), eastern).ToUniversalTime());
        // 02:30 does not exist on 2027-03-14; the reminder fires at 03:00 EDT.
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 7, 0, 0, TimeSpan.Zero), ReminderSchedule.ToInstant(new DateTime(2027, 3, 14, 2, 30, 0), eastern).ToUniversalTime());
        // 01:30 happens twice on 2026-11-01; the first (EDT) occurrence is used, once.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), ReminderSchedule.ToInstant(new DateTime(2026, 11, 1, 1, 30, 0), eastern).ToUniversalTime());
    }

    [Fact]
    public async Task Recurring_reminder_keeps_wall_clock_time_over_dst_and_does_not_repeat()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var store = new MemoryTaskStore();
        var clock = new FixedClock(new DateTimeOffset(2027, 3, 13, 13, 0, 0, TimeSpan.Zero));
        var board = new TaskBoard(store, clock, eastern);
        await board.LoadAsync();
        Assert.Equal(new DateOnly(2027, 3, 13), board.Today);
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Pills", item =>
        {
            item.DueDate = new DateOnly(2027, 3, 13);
            item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily };
            item.Reminders.Add(new TaskReminder { At = new DateTime(2027, 3, 13, 9, 0, 0) });
        });
        clock.UtcNow = new DateTimeOffset(2027, 3, 13, 14, 0, 1, TimeSpan.Zero);
        var first = Assert.Single(board.DueReminders());
        await board.MarkReminderDeliveredAsync(task.Id, first.Reminder.Id);
        await board.SetCompletedAsync([task.Id], true);
        await board.SetCompletedAsync([task.Id], true);
        var reminder = board.FindTask(task.Id)!.Reminders.Single();
        Assert.Equal(new DateTime(2027, 3, 15, 9, 0, 0), reminder.At);
        clock.UtcNow = new DateTimeOffset(2027, 3, 15, 12, 59, 0, TimeSpan.Zero);
        Assert.Empty(board.DueReminders());
        clock.UtcNow = new DateTimeOffset(2027, 3, 15, 13, 0, 0, TimeSpan.Zero);
        Assert.Single(board.DueReminders());
    }

    // ---------- Calendar ----------

    [Fact]
    public async Task Calendar_ranges_display_and_rescheduling()
    {
        var grid = TaskCalendar.MonthGrid(new DateOnly(2026, 11, 15));
        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), grid[0]);
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Equal(new DateOnly(2026, 11, 16), TaskCalendar.Week(Today)[0]);
        Assert.Equal(new DateOnly(2026, 12, 21), TaskCalendar.Shift(Today, CalendarMode.Month, 1));

        var (board, _, _) = await NewBoard();
        var task = await board.AddTaskAsync(board.DefaultList.Id, "Review", item =>
        {
            item.DueDate = Today;
            item.DueTime = new TimeOnly(14, 0);
            item.StartDate = Today.AddDays(-2);
            item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 13, 0, 0) });
        });
        var late = await board.AddTaskAsync(board.DefaultList.Id, "Late", item => item.DueDate = Today.AddDays(-3));
        Assert.Equal([task.Id], TaskCalendar.On(board.Tasks, Today).Select(item => item.Id));
        Assert.Equal([task.Id], TaskCalendar.On(board.Tasks, Today.AddDays(-2)).Select(item => item.Id));
        Assert.True(board.FindTask(late.Id)!.IsOverdue(board.Today));

        await board.RescheduleAsync(task.Id, Today.AddDays(3));
        var moved = board.FindTask(task.Id)!;
        Assert.Equal(Today.AddDays(3), moved.DueDate);
        Assert.Equal(new TimeOnly(14, 0), moved.DueTime);
        Assert.Equal(Today.AddDays(1), moved.StartDate);
        Assert.Equal(new DateTime(2026, 11, 24, 13, 0, 0), moved.Reminders[0].At);

        await board.RescheduleAsync(task.Id, Today.AddDays(3), new TimeOnly(8, 0));
        Assert.Equal(new TimeOnly(8, 0), board.FindTask(task.Id)!.DueTime);
        await board.RescheduleAsync(late.Id, Today);
        Assert.False(board.FindTask(late.Id)!.IsOverdue(board.Today));
        await board.UndoAsync();
        Assert.True(board.FindTask(late.Id)!.IsOverdue(board.Today));
    }

    // ---------- Persistence ----------

    [Fact]
    public async Task Sqlite_store_keeps_everything_across_restarts()
    {
        var path = Path.Combine(Temp(), "todo.db");
        string taskId;
        await using (var store = new SqliteTaskStore(path))
        {
            await store.InitializeAsync();
            var board = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
            await board.LoadAsync();
            var group = await board.AddGroupAsync("Home");
            var list = await board.AddListAsync("Garden", group.Id, "#3366CC");
            var task = await board.AddTaskAsync(list.Id, "Mow", item =>
            {
                item.DueDate = Today;
                item.DueTime = new TimeOnly(7, 45);
                item.Important = true;
                item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Saturday] };
                item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 7, 0, 0) });
                item.Attachments.Add(new TaskAttachment { FileName = "plan.pdf", RelativePath = "abc/def.pdf", Size = 10 });
                SubtaskEdits.Add(item, "Edges");
            });
            taskId = task.Id;
            var done = await board.AddTaskAsync(list.Id, "Rake");
            await board.SetCompletedAsync([done.Id], true);
            await store.SavePreferencesAsync(new TaskPreferences { SelectedView = "list:" + list.Id, SortByView = { ["list:" + list.Id] = TaskSortMode.Alphabetical }, CalendarMode = CalendarMode.Week }, CancellationToken.None);
        }

        await using (var store = new SqliteTaskStore(path))
        {
            await store.InitializeAsync();
            var board = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
            await board.LoadAsync();
            Assert.Equal(2, board.Lists.Count);
            Assert.Single(board.Groups);
            var task = board.FindTask(taskId)!;
            Assert.Equal("Mow", task.Title);
            Assert.Equal(new TimeOnly(7, 45), task.DueTime);
            Assert.Equal([DayOfWeek.Saturday], task.Recurrence!.DaysOfWeek);
            Assert.Single(task.Reminders);
            Assert.Equal("Edges", task.Subtasks[0].Title);
            Assert.Equal("plan.pdf", task.Attachments[0].FileName);
            Assert.Single(board.History);
            var preferences = await store.LoadPreferencesAsync(CancellationToken.None);
            Assert.Equal(CalendarMode.Week, preferences.CalendarMode);
            Assert.Equal(TaskSortMode.Alphabetical, preferences.SortByView.Values.Single());
        }
    }

    [Fact]
    public async Task Migration_upgrades_old_files_and_keeps_a_backup()
    {
        var path = Path.Combine(Temp(), "todo.db");
        var task = new TodoTask { Id = "legacy", ListId = "list1", Title = "From v1", DueDate = Today, ModifiedUtc = Start };
        await using (var old = new SqliteTaskStore(path))
        {
            await old.InitializeAsync(targetVersion: 1);
        }

        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using var insert = raw.CreateCommand();
            insert.CommandText = "INSERT INTO task_lists(id, json) VALUES ('list1', '{\"id\":\"list1\",\"name\":\"Old list\"}'); INSERT INTO tasks(id, list_id, json) VALUES ('legacy', 'list1', $json);";
            insert.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(task, TodoTransfer.CreateOptions()));
            await insert.ExecuteNonQueryAsync();
        }

        await using (var store = new SqliteTaskStore(path))
        {
            await store.InitializeAsync();
            Assert.NotNull(store.LastMigrationBackup);
            Assert.True(File.Exists(store.LastMigrationBackup));
            var board = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
            await board.LoadAsync();
            Assert.Equal("From v1", board.FindTask("legacy")!.Title);
            Assert.Equal("Old list", board.FindList("list1")!.Name);
            Assert.Single(board.Lists, list => list.IsDefault);
        }

        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using var query = raw.CreateCommand();
            query.CommandText = "SELECT due_date FROM tasks WHERE id = 'legacy'";
            Assert.Equal("2026-11-21", await query.ExecuteScalarAsync());
            query.CommandText = "PRAGMA user_version";
            Assert.Equal((long)SqliteTaskStore.SchemaVersion, await query.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task A_newer_database_is_refused_and_not_changed()
    {
        var path = Path.Combine(Temp(), "todo.db");
        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using var command = raw.CreateCommand();
            command.CommandText = "CREATE TABLE future(x); PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        var before = await File.ReadAllBytesAsync(path);
        await using var store = new SqliteTaskStore(path);
        var error = await Assert.ThrowsAsync<TodoStorageException>(() => store.InitializeAsync());
        Assert.Contains("not changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Backup_and_restore_round_trip()
    {
        var directory = Temp();
        var path = Path.Combine(directory, "todo.db");
        var backup = Path.Combine(directory, "backup.db");
        await using var store = new SqliteTaskStore(path);
        await store.InitializeAsync();
        var board = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
        await board.LoadAsync();
        var kept = await board.AddTaskAsync(board.DefaultList.Id, "Before backup");
        await store.BackupToAsync(backup);

        await board.DeleteTasksAsync([kept.Id]);
        await board.AddTaskAsync(board.DefaultList.Id, "After backup");

        var restored = await SqliteTaskStore.ReadBackupAsync(backup);
        Assert.Equal(["Before backup"], restored.Tasks.Select(task => task.Title));
        await board.ReplaceAllAsync(restored);
        Assert.Equal(["Before backup"], board.Tasks.Select(task => task.Title));
        Assert.False(board.CanUndo);

        var garbage = Path.Combine(directory, "garbage.db");
        await File.WriteAllTextAsync(garbage, "not a database");
        await Assert.ThrowsAsync<TodoStorageException>(() => SqliteTaskStore.ReadBackupAsync(garbage));
        Assert.Equal(["Before backup"], board.Tasks.Select(task => task.Title));
    }

    [Fact]
    public async Task Attachments_are_copied_and_resolved_safely()
    {
        var directory = Temp();
        var files = new TaskFileStore(Path.Combine(directory, "task-files"));
        var source = Path.Combine(directory, "notes.txt");
        await File.WriteAllTextAsync(source, "hello");
        var attachment = files.Import("task1", source, Start);
        Assert.Equal("notes.txt", attachment.FileName);
        Assert.Equal(5, attachment.Size);
        var resolved = files.Resolve(attachment);
        Assert.NotNull(resolved);
        Assert.Equal("hello", await File.ReadAllTextAsync(resolved!));
        File.Delete(source);
        Assert.NotNull(files.Resolve(attachment));

        Assert.Null(files.Resolve(new TaskAttachment { RelativePath = "..\\..\\Windows\\win.ini" }));
        Assert.Null(files.Resolve(new TaskAttachment { RelativePath = "C:\\Windows\\win.ini" }));

        var task = new TodoTask { Attachments = [attachment] };
        Assert.Equal(0, files.RemoveOrphans([task]));
        Assert.Equal(1, files.RemoveOrphans([]));
        Assert.Null(files.Resolve(attachment));
    }

    // ---------- Import / export ----------

    [Fact]
    public async Task Json_export_import_round_trip_preserves_relationships_with_new_ids()
    {
        var (board, _, _) = await NewBoard();
        var group = await board.AddGroupAsync("Projects");
        var list = await board.AddListAsync("Website", group.Id, "#AA3366");
        var task = await board.AddTaskAsync(list.Id, "Launch", item =>
        {
            item.DueDate = Today;
            item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly };
            SubtaskEdits.Add(item, "DNS");
        });
        await board.SetCompletedAsync([task.Id], true);
        var json = TodoTransfer.ExportJson(board.Snapshot(), Start);

        var plan = TodoTransfer.PrepareImport(json, board.Snapshot());
        Assert.True(plan.Ok, string.Join(" ", plan.Errors));
        await board.ImportAsync(plan.Data!);
        await board.ImportAsync(TodoTransfer.PrepareImport(json, board.Snapshot()).Data!);

        Assert.Equal(board.Tasks.Count, board.Tasks.Select(item => item.Id).Distinct().Count());
        Assert.Equal(3, board.Tasks.Count(item => item.Title == "Launch"));
        Assert.Single(board.Lists, item => item.IsDefault);
        var importedList = board.Lists.First(item => item.Name == "Website (imported)");
        Assert.NotEqual(list.Id, importedList.Id);
        var importedGroup = board.FindGroup(importedList.GroupId!)!;
        Assert.Equal("Projects", importedGroup.Name);
        Assert.NotEqual(group.Id, importedGroup.Id);
        var importedTask = board.Tasks.First(item => item.ListId == importedList.Id && item.Title == "Launch");
        Assert.Equal("DNS", importedTask.Subtasks.Single().Title);
        Assert.Equal(RecurrenceFrequency.Monthly, importedTask.Recurrence!.Frequency);
        Assert.Contains(board.History, record => record.TaskId == importedTask.Id);
        Assert.Equal("Website", board.FindList(list.Id)!.Name);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"format":"something-else","version":1}""")]
    [InlineData("""{"format":"alondra-todo","version":7}""")]
    [InlineData("""{"format":"alondra-todo","version":1,"lists":[{"id":"a","name":"A"},{"id":"a","name":"B"}]}""")]
    [InlineData("""{"format":"alondra-todo","version":1,"tasks":[{"id":"","title":"x","listId":"a"}]}""")]
    public void Import_rejects_invalid_documents(string json)
    {
        var plan = TodoTransfer.PrepareImport(json, new TodoData());
        Assert.False(plan.Ok);
        Assert.NotEmpty(plan.Errors);
        Assert.Null(plan.Data);
    }

    [Fact]
    public void Import_repairs_references_and_drops_unsafe_paths()
    {
        const string json = """
            {"format":"alondra-todo","version":1,
             "lists":[{"id":"l1","name":"Home","groupId":"missing","color":"red"}],
             "tasks":[
               {"id":"t1","listId":"l1","title":"Safe","attachments":[{"fileName":"a","relativePath":"x/a.txt"},{"fileName":"b","relativePath":"..\\..\\evil.txt"}]},
               {"id":"t2","listId":"nowhere","title":"Orphan","recurrence":{"frequency":"Daily","interval":0}}
             ]}
            """;
        var plan = TodoTransfer.PrepareImport(json, new TodoData());
        Assert.True(plan.Ok);
        Assert.Equal(2, plan.Data!.Lists.Count);
        Assert.Null(plan.Data.Lists[0].GroupId);
        Assert.Null(plan.Data.Lists[0].Color);
        Assert.Single(plan.Data.Tasks[0].Attachments);
        Assert.Equal("Imported tasks", plan.Data.Lists.Single(list => list.Id == plan.Data.Tasks[1].ListId).Name);
        Assert.Null(plan.Data.Tasks[1].Recurrence);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public async Task Csv_and_ics_exports_are_well_formed()
    {
        var (board, _, _) = await NewBoard();
        await board.AddTaskAsync(board.DefaultList.Id, "=SUM(A1)", item => item.Notes = "line one\nhas \"quotes\", commas");
        await board.AddTaskAsync(board.DefaultList.Id, "Weekly sync", item =>
        {
            item.DueDate = Today;
            item.DueTime = new TimeOnly(9, 0);
            item.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday] };
            item.Reminders.Add(new TaskReminder { At = new DateTime(2026, 11, 21, 8, 45, 0) });
        });
        var csv = TodoTransfer.ExportCsv(board.Snapshot());
        Assert.StartsWith("List,Group,Title", csv, StringComparison.Ordinal);
        Assert.Contains("'=SUM(A1)", csv, StringComparison.Ordinal);
        Assert.Contains("\"line one\nhas \"\"quotes\"\", commas\"", csv, StringComparison.Ordinal);

        var ics = TodoTransfer.ExportIcs(board.Snapshot(), Start);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(ics, "BEGIN:VTODO").Count);
        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=MO", ics, StringComparison.Ordinal);
        Assert.Contains("DUE:20261121T090000", ics, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VALARM", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:line one\\nhas \"quotes\"\\, commas", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75));
    }

    // ---------- Offline, themes, scale ----------

    [Fact]
    public void Task_code_has_no_network_dependencies()
    {
        // Core is shared with mail code, so check the task code itself rather than whole assemblies.
        var root = FindRepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src", "Alondra.Core", "Tasks"), "*.cs")
            .Concat([Path.Combine(root, "src", "Alondra.Persistence", "SqliteTaskStore.cs"), Path.Combine(root, "src", "Alondra.Persistence", "TaskFileStore.cs")])
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "Alondra.App", "Tasks"), "*.cs"));
        var network = new Regex(@"System\.Net\b|HttpClient|WebRequest|WebClient|Socket|Alondra\.Providers|Alondra\.Mail|OrganizerGateway");
        foreach (var file in sources)
            Assert.False(network.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} refers to a network API");

        var types = typeof(TaskBoard).Assembly.GetTypes().Where(type => type.Namespace == "Alondra.Core.Tasks")
            .Concat([typeof(SqliteTaskStore), typeof(TaskFileStore)]);
        foreach (var type in types)
        {
            var members = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                var signature = member switch
                {
                    System.Reflection.FieldInfo field => [field.FieldType],
                    System.Reflection.PropertyInfo property => [property.PropertyType],
                    System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
                    _ => Array.Empty<Type>(),
                };
                Assert.DoesNotContain(signature, used => used.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true);
            }
        }
    }

    [Fact]
    public void Task_views_use_only_brushes_defined_in_both_themes()
    {
        var root = FindRepoRoot();
        var app = Path.Combine(root, "src", "Alondra.App");
        var used = Directory.EnumerateFiles(Path.Combine(app, "Tasks"), "*.xaml")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"DynamicResource\s+(\w+)").Select(match => match.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var theme in new[] { "Light.xaml", "Dark.xaml" })
        {
            var keys = XDocument.Load(Path.Combine(app, "Themes", theme)).Root!.Elements().Select(element => (string?)element.Attribute(x + "Key")).ToHashSet();
            foreach (var key in used)
                Assert.True(keys.Contains(key), $"{theme} is missing {key}");
        }
    }

    [Fact]
    public async Task Large_collections_stay_fast()
    {
        var path = Path.Combine(Temp(), "todo.db");
        await using var store = new SqliteTaskStore(path);
        await store.InitializeAsync();
        var board = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
        await board.LoadAsync();
        var lists = new List<TaskList>();
        for (var i = 0; i < 20; i++)
            lists.Add(new TaskList { Name = "List " + i, Position = i });
        var tasks = Enumerable.Range(0, 10_000).Select(i => new TodoTask
        {
            ListId = lists[i % lists.Count].Id,
            Title = "Task number " + i,
            Notes = i % 7 == 0 ? "needle" : "",
            Important = i % 11 == 0,
            DueDate = i % 3 == 0 ? Today.AddDays(i % 60 - 30) : null,
            Position = i,
            CreatedUtc = Start,
            ModifiedUtc = Start,
        }).ToList();

        var watch = Stopwatch.StartNew();
        await board.ImportAsync(new TodoData { Lists = lists, Tasks = tasks });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"import took {watch.Elapsed}");

        watch.Restart();
        foreach (var view in SmartViews.All)
            _ = board.Query(view, new TaskFilter(), SmartViews.DefaultSort(view), false);
        var found = board.Search("needle");
        Assert.Equal(1429, found.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"views and search took {watch.Elapsed}");

        watch.Restart();
        var reloaded = new TaskBoard(store, new FixedClock(Start), TimeZoneInfo.Utc);
        await reloaded.LoadAsync();
        Assert.Equal(10_000, reloaded.Tasks.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"load took {watch.Elapsed}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Alondra.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
