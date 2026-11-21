using System.Globalization;

namespace Alondra.Core.Tasks;

/// <summary>Smart views are filters over the stored tasks. They never copy a task.</summary>
public static class SmartViews
{
    public static readonly IReadOnlyList<SmartView> All =
    [
        SmartView.MyDay, SmartView.Important, SmartView.Planned, SmartView.Upcoming,
        SmartView.Overdue, SmartView.All, SmartView.Completed, SmartView.Unscheduled,
    ];

    public static string Title(SmartView view) => view switch
    {
        SmartView.MyDay => "My Day",
        SmartView.Important => "Important",
        SmartView.Planned => "Planned",
        SmartView.Upcoming => "Upcoming",
        SmartView.Overdue => "Overdue",
        SmartView.All => "All Tasks",
        SmartView.Completed => "Completed",
        _ => "Unscheduled",
    };

    /// <summary>Segoe MDL2 Assets glyph used in the sidebar.</summary>
    public static string Glyph(SmartView view) => view switch
    {
        SmartView.MyDay => "",
        SmartView.Important => "",
        SmartView.Planned => "",
        SmartView.Upcoming => "",
        SmartView.Overdue => "",
        SmartView.All => "",
        SmartView.Completed => "",
        _ => "",
    };

    public static bool Includes(TodoTask task, SmartView view, DateOnly today) => view switch
    {
        SmartView.MyDay => task.MyDayDate == today,
        SmartView.Important => task.Important,
        SmartView.Planned => task.DueDate is not null || task.StartDate is not null,
        SmartView.Upcoming => !task.Completed && task.DueDate is { } due && due > today,
        SmartView.Overdue => task.IsOverdue(today),
        SmartView.All => true,
        SmartView.Completed => task.Completed,
        SmartView.Unscheduled => task.DueDate is null && task.StartDate is null,
        _ => false,
    };

    /// <summary>Views whose membership already decides completion state, so the status filter does not apply.</summary>
    public static bool FixesStatus(SmartView view) => view is SmartView.Completed or SmartView.Upcoming or SmartView.Overdue;

    public static TaskSortMode DefaultSort(SmartView view) => view switch
    {
        SmartView.Planned or SmartView.Upcoming or SmartView.Overdue => TaskSortMode.DueDate,
        SmartView.Completed => TaskSortMode.Created,
        _ => TaskSortMode.Manual,
    };

    public static IEnumerable<TodoTask> Filter(IEnumerable<TodoTask> tasks, SmartView view, DateOnly today) =>
        tasks.Where(task => Includes(task, view, today));
}

public sealed class TaskFilter
{
    public TaskStatusFilter Status { get; init; } = TaskStatusFilter.Open;
    public bool ImportantOnly { get; init; }
    public string Query { get; init; } = "";
}

public static class TaskSearch
{
    /// <summary>Every whitespace-separated term must appear in the title, notes, a step, or the list name.</summary>
    public static bool Matches(TodoTask task, string? query, string? listName = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        foreach (var term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Contains(task.Title, term) && !Contains(task.Notes, term) && !Contains(listName, term)
                && !task.Subtasks.Any(step => Contains(step.Title, term)))
                return false;
        }

        return true;
    }

    public static IEnumerable<TodoTask> Apply(IEnumerable<TodoTask> tasks, TaskFilter filter, bool statusApplies, Func<string, string?>? listName = null)
    {
        foreach (var task in tasks)
        {
            if (statusApplies)
            {
                if (filter.Status == TaskStatusFilter.Open && task.Completed)
                    continue;
                if (filter.Status == TaskStatusFilter.Completed && !task.Completed)
                    continue;
            }

            if (filter.ImportantOnly && !task.Important)
                continue;
            if (!Matches(task, filter.Query, listName?.Invoke(task.ListId)))
                continue;
            yield return task;
        }
    }

    private static bool Contains(string? text, string term) =>
        !string.IsNullOrEmpty(text) && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

public static class TaskSorting
{
    /// <param name="listOrder">Position of each list, used to keep manual order stable across lists.</param>
    public static List<TodoTask> Sort(IEnumerable<TodoTask> tasks, TaskSortMode mode, bool descending, IReadOnlyDictionary<string, double>? listOrder = null)
    {
        double ListPos(TodoTask task) => listOrder is not null && listOrder.TryGetValue(task.ListId, out var value) ? value : 0;
        IOrderedEnumerable<TodoTask> ordered = mode switch
        {
            TaskSortMode.DueDate => tasks.OrderBy(task => task.DueDate is null)
                .ThenBy(task => task.DueDate ?? DateOnly.MaxValue)
                .ThenBy(task => task.DueTime ?? TimeOnly.MaxValue),
            TaskSortMode.Importance => tasks.OrderByDescending(task => task.Important)
                .ThenBy(task => task.DueDate ?? DateOnly.MaxValue),
            TaskSortMode.Created => tasks.OrderByDescending(task => task.Completed ? task.CompletedUtc ?? task.CreatedUtc : task.CreatedUtc),
            TaskSortMode.Alphabetical => tasks.OrderBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => tasks.OrderBy(ListPos),
        };
        var result = ordered.ThenBy(ListPos).ThenBy(task => task.Position).ThenBy(task => task.Id, StringComparer.Ordinal).ToList();
        if (descending && mode != TaskSortMode.Manual)
            result.Reverse();
        return result;
    }

    public static string Label(TaskSortMode mode) => mode switch
    {
        TaskSortMode.DueDate => "Due date",
        TaskSortMode.Importance => "Importance",
        TaskSortMode.Created => "Creation date",
        TaskSortMode.Alphabetical => "Alphabetical",
        _ => "My order",
    };
}

/// <summary>Date ranges for the task calendar. Weeks start on Monday, matching the existing calendar workspace.</summary>
public static class TaskCalendar
{
    /// <summary>The 42 days (6 weeks) shown for a month, starting on the Monday on or before the 1st.</summary>
    public static IReadOnlyList<DateOnly> MonthGrid(DateOnly anyDayInMonth)
    {
        var first = new DateOnly(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var start = Recurrence.WeekStart(first);
        return Enumerable.Range(0, 42).Select(start.AddDays).ToList();
    }

    public static IReadOnlyList<DateOnly> Week(DateOnly anyDay)
    {
        var start = Recurrence.WeekStart(anyDay);
        return Enumerable.Range(0, 7).Select(start.AddDays).ToList();
    }

    public static DateOnly Shift(DateOnly anchor, CalendarMode mode, int delta) => mode switch
    {
        CalendarMode.Day => anchor.AddDays(delta),
        CalendarMode.Week => anchor.AddDays(7 * delta),
        _ => anchor.AddMonths(delta),
    };

    /// <summary>Tasks to show on a day: due that day; open tasks also appear on their start date when it differs.</summary>
    public static IEnumerable<TodoTask> On(IEnumerable<TodoTask> tasks, DateOnly day) =>
        tasks.Where(task => task.DueDate == day || (!task.Completed && task.StartDate == day && task.DueDate != day))
            .OrderBy(task => task.DueTime ?? TimeOnly.MaxValue)
            .ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase);
}

public sealed class TodoStatistics
{
    public int Open { get; init; }
    public int Overdue { get; init; }
    public int DueToday { get; init; }
    public int DueNextSevenDays { get; init; }
    public int CompletedToday { get; init; }
    public int CompletedLastSevenDays { get; init; }
    public int CompletedLastThirtyDays { get; init; }
    public int StreakDays { get; init; }

    /// <summary>Completions per day for the last seven days, oldest first.</summary>
    public IReadOnlyList<(DateOnly Day, int Count)> LastSevenDays { get; init; } = [];

    public static TodoStatistics Compute(TodoData data, DateOnly today, TimeZoneInfo zone)
    {
        DateOnly Local(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        var perDay = data.History.GroupBy(item => Local(item.CompletedUtc)).ToDictionary(group => group.Key, group => group.Count());
        int Count(DateOnly day) => perDay.TryGetValue(day, out var value) ? value : 0;
        var streak = 0;
        var cursor = Count(today) > 0 ? today : today.AddDays(-1);
        while (Count(cursor) > 0)
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        var open = data.Tasks.Where(task => !task.Completed).ToList();
        return new TodoStatistics
        {
            Open = open.Count,
            Overdue = open.Count(task => task.IsOverdue(today)),
            DueToday = open.Count(task => task.DueDate == today),
            DueNextSevenDays = open.Count(task => task.DueDate is { } due && due > today && due <= today.AddDays(7)),
            CompletedToday = Count(today),
            CompletedLastSevenDays = Enumerable.Range(0, 7).Sum(offset => Count(today.AddDays(-offset))),
            CompletedLastThirtyDays = Enumerable.Range(0, 30).Sum(offset => Count(today.AddDays(-offset))),
            StreakDays = streak,
            LastSevenDays = Enumerable.Range(0, 7).Select(offset => today.AddDays(offset - 6)).Select(day => (day, Count(day))).ToList(),
        };
    }
}
