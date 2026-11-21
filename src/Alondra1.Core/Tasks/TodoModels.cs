namespace Alondra1.Core.Tasks;

public enum RecurrenceFrequency { Daily, Weekly, Monthly, Yearly }

public enum TaskSortMode { Manual, DueDate, Importance, Created, Alphabetical }

public enum TaskStatusFilter { Open, All, Completed }

public enum SmartView { MyDay, Important, Planned, Upcoming, Overdue, All, Completed, Unscheduled }

public enum CalendarMode { Day, Week, Month }

/// <summary>A recurrence rule. Dates are calendar dates, so repeating is independent of time zones and DST.</summary>
public sealed class RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Daily;

    /// <summary>Repeat every N units (custom interval). Always at least 1.</summary>
    public int Interval { get; set; } = 1;

    /// <summary>Weekly only: days to repeat on. Empty means the weekday of the due date.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];

    /// <summary>Optional last date (inclusive) on which an occurrence may fall.</summary>
    public DateOnly? Until { get; set; }

    /// <summary>First date of the series. Monthly and yearly repeats count from it so a clamped date (Jan 31 → Feb 28) does not drift.</summary>
    public DateOnly? Anchor { get; set; }

    public RecurrenceRule Clone() => new()
    {
        Frequency = Frequency,
        Interval = Interval,
        DaysOfWeek = [.. DaysOfWeek],
        Until = Until,
        Anchor = Anchor,
    };
}

/// <summary>A reminder at a local wall-clock time. Stored without an offset so it follows the system time zone and DST.</summary>
public sealed class TaskReminder
{
    public string Id { get; set; } = NewId.Create();
    public DateTime At { get; set; }

    /// <summary>The <see cref="At"/> value a notification was already shown for. Prevents duplicate notifications.</summary>
    public DateTime? DeliveredFor { get; set; }

    public bool Dismissed { get; set; }
    public DateTimeOffset? SnoozedUntilUtc { get; set; }

    public TaskReminder Clone() => (TaskReminder)MemberwiseClone();
}

public sealed class Subtask
{
    public string Id { get; set; } = NewId.Create();
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public Subtask Clone() => (Subtask)MemberwiseClone();
}

/// <summary>A file copied into Alondra1's local task-file folder. <see cref="RelativePath"/> is relative to that folder.</summary>
public sealed class TaskAttachment
{
    public string Id { get; set; } = NewId.Create();
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset AddedUtc { get; set; }

    public TaskAttachment Clone() => (TaskAttachment)MemberwiseClone();
}

public sealed class TodoTask
{
    public string Id { get; set; } = NewId.Create();
    public string ListId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Completed { get; set; }
    public bool Important { get; set; }
    public double Position { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>Optional time slot on the due date (local wall clock).</summary>
    public TimeOnly? DueTime { get; set; }

    /// <summary>The day the task was added to My Day. My Day shows tasks whose value is today.</summary>
    public DateOnly? MyDayDate { get; set; }

    public RecurrenceRule? Recurrence { get; set; }
    public List<TaskReminder> Reminders { get; set; } = [];
    public List<Subtask> Subtasks { get; set; } = [];
    public List<TaskAttachment> Attachments { get; set; } = [];

    public bool IsOverdue(DateOnly today) => !Completed && DueDate is { } due && due < today;

    public (int Done, int Total) SubtaskProgress => (Subtasks.Count(item => item.Completed), Subtasks.Count);

    public TodoTask Clone()
    {
        var copy = (TodoTask)MemberwiseClone();
        copy.Recurrence = Recurrence?.Clone();
        copy.Reminders = Reminders.Select(item => item.Clone()).ToList();
        copy.Subtasks = Subtasks.Select(item => item.Clone()).ToList();
        copy.Attachments = Attachments.Select(item => item.Clone()).ToList();
        return copy;
    }
}

public sealed class TaskList
{
    public string Id { get; set; } = NewId.Create();
    public string Name { get; set; } = "";
    public string? GroupId { get; set; }

    /// <summary>Accent color as #RRGGBB, or null for the theme accent.</summary>
    public string? Color { get; set; }

    /// <summary>When true the list's accent color tints the list background.</summary>
    public bool TintBackground { get; set; }

    public double Position { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    public TaskList Clone() => (TaskList)MemberwiseClone();
}

public sealed class ListGroup
{
    public string Id { get; set; } = NewId.Create();
    public string Name { get; set; } = "";
    public double Position { get; set; }
    public bool Collapsed { get; set; }

    public ListGroup Clone() => (ListGroup)MemberwiseClone();
}

/// <summary>One completion event, kept even when the task is reopened, deleted, or repeats.</summary>
public sealed class CompletionRecord
{
    public string Id { get; set; } = NewId.Create();
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ListId { get; set; } = "";
    public DateTimeOffset CompletedUtc { get; set; }

    /// <summary>The due date of the occurrence that was completed (recurring tasks).</summary>
    public DateOnly? OccurrenceDate { get; set; }

    public CompletionRecord Clone() => (CompletionRecord)MemberwiseClone();
}

/// <summary>Per-user preferences for the To-do workspace. Stored locally next to the tasks.</summary>
public sealed class TaskPreferences
{
    public string SelectedView { get; set; } = "smart:MyDay";
    public Dictionary<string, TaskSortMode> SortByView { get; set; } = [];
    public Dictionary<string, bool> SortDescendingByView { get; set; } = [];
    public TaskStatusFilter StatusFilter { get; set; } = TaskStatusFilter.Open;
    public bool ImportantOnlyFilter { get; set; }
    public bool ShowCounts { get; set; } = true;
    public bool DetailsOpen { get; set; } = true;
    public CalendarMode CalendarMode { get; set; } = CalendarMode.Month;
    public bool NotificationsEnabled { get; set; } = true;
    public int SnoozeMinutes { get; set; } = 10;

    public TaskPreferences Clone()
    {
        var copy = (TaskPreferences)MemberwiseClone();
        copy.SortByView = new Dictionary<string, TaskSortMode>(SortByView);
        copy.SortDescendingByView = new Dictionary<string, bool>(SortDescendingByView);
        return copy;
    }
}

/// <summary>Everything the To-do workspace stores.</summary>
public sealed class TodoData
{
    public List<ListGroup> Groups { get; set; } = [];
    public List<TaskList> Lists { get; set; } = [];
    public List<TodoTask> Tasks { get; set; } = [];
    public List<CompletionRecord> History { get; set; } = [];
}

public static class NewId
{
    public static string Create() => Guid.NewGuid().ToString("n");
}
