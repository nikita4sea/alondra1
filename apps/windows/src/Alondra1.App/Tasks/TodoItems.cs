using System.Globalization;
using System.Windows.Media;
using Alondra1.Core.Tasks;

namespace Alondra1.App.Tasks;

public enum SidebarKind { Smart, Special, Header, Group, List }

/// <summary>One row in the To-do sidebar: a smart view, the calendar, a group header, or a list.</summary>
public sealed class SidebarItem
{
    public required string Key { get; init; }
    public SidebarKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Icon { get; init; } = "List";
    public int Count { get; init; }
    public string CountText => Count > 0 ? Count.ToString(CultureInfo.CurrentCulture) : "";
    public double Indent { get; init; }
    public Brush? Accent { get; init; }
    public bool Collapsed { get; init; }
    public string? ListId { get; init; }
    public string? GroupId { get; init; }
    public bool IsHeader => Kind == SidebarKind.Header;
    public bool IsGroup => Kind == SidebarKind.Group;
    public bool IsList => Kind == SidebarKind.List;
    public bool HasCount => Count > 0;
    public bool IsWarning { get; init; }
    public string GroupGlyph => Collapsed ? "" : "";
    public string GroupIcon => Collapsed ? "ChevronRight" : "ChevronDown";
    public string AccessName => Count > 0 ? $"{Title}, {Count}" : Title;
    public override string ToString() => AccessName;
}

public sealed class TaskItemVm
{
    public TaskItemVm(TodoTask task, string listName, Brush? listAccent, DateOnly today, bool showList)
    {
        Id = task.Id;
        Title = task.Title;
        Completed = task.Completed;
        Important = task.Important;
        ListName = showList ? listName : "";
        ListAccent = listAccent;
        IsOverdue = task.IsOverdue(today);
        IsDueSoon = !task.Completed && task.DueDate is { } due && due >= today && due <= today.AddDays(1);
        DueText = task.DueDate is null ? "" : (IsOverdue ? "Overdue · " : "") + TodoFormat.Date(task.DueDate.Value, today) + (task.DueTime is { } time ? " " + TodoFormat.Time(time) : "");
        InMyDay = task.MyDayDate == today;
        IsRecurring = task.Recurrence is not null;
        HasReminder = task.Reminders.Any(item => !item.Dismissed);
        HasNotes = !string.IsNullOrWhiteSpace(task.Notes);
        HasAttachments = task.Attachments.Count > 0;
        var (done, total) = task.SubtaskProgress;
        StepsText = total == 0 ? "" : $"{done} of {total}";
        Progress = total == 0 ? 0 : (double)done / total;
        HasSteps = total > 0;
        var parts = new List<string>();
        if (InMyDay)
            parts.Add("My Day");
        if (ListName.Length > 0)
            parts.Add(ListName);
        HasMeta = parts.Count > 0 || DueText.Length > 0 || HasSteps || IsRecurring || HasReminder || HasNotes || HasAttachments;
        ContextText = string.Join(" · ", parts);
        AccessName = string.Join(", ", new[]
        {
            Title,
            Completed ? "completed" : "",
            Important ? "important" : "",
            DueText.Length > 0 ? "due " + DueText : "",
            HasSteps ? StepsText + " steps" : "",
            IsRecurring ? "repeats" : "",
            HasReminder ? "has reminder" : "",
            HasNotes ? "has note" : "",
            HasAttachments ? "has files" : "",
            ListName,
        }.Where(part => part.Length > 0));
    }

    public string Id { get; }
    public string Title { get; }
    public bool Completed { get; }
    public bool Important { get; }
    public string ListName { get; }
    public Brush? ListAccent { get; }
    public bool IsOverdue { get; }
    public bool IsDueSoon { get; }
    public string DueText { get; }
    public bool HasDue => DueText.Length > 0;
    public bool InMyDay { get; }
    public bool IsRecurring { get; }
    public bool HasReminder { get; }
    public bool HasNotes { get; }
    public bool HasAttachments { get; }
    public string StepsText { get; }
    public double Progress { get; }
    public bool HasSteps { get; }
    public bool HasMeta { get; }
    public string ContextText { get; }
    public bool HasContext => ContextText.Length > 0;
    public string AccessName { get; }
    public override string ToString() => AccessName;
}

public sealed class SubtaskVm
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public bool Completed { get; init; }
    public override string ToString() => Completed ? Title + ", completed" : Title;
}

public sealed class ReminderVm
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public bool Past { get; init; }
    public override string ToString() => Text;
}

public sealed class AttachmentVm
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string SizeText { get; init; }
    public bool Missing { get; init; }
    public override string ToString() => FileName + ", " + SizeText + (Missing ? ", missing" : "");
}

public sealed class ListChoice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

public sealed class CalendarTaskVm
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string TimeText { get; init; } = "";
    public bool Completed { get; init; }
    public bool Overdue { get; init; }
    public bool Important { get; init; }
    public bool IsStartMarker { get; init; }
    public Brush? Accent { get; init; }
    public string AccessName => string.Join(", ", new[] { Title, TimeText, Completed ? "completed" : "", Overdue ? "overdue" : "", IsStartMarker ? "starts" : "" }.Where(part => part.Length > 0));
    public override string ToString() => AccessName;
}

public sealed class CalendarCellVm
{
    public required DateOnly Date { get; init; }
    public string DayText { get; init; } = "";
    public string Header { get; init; } = "";
    public bool InMonth { get; init; } = true;
    public bool IsToday { get; init; }
    public bool IsSelected { get; init; }
    public bool IsPast { get; init; }
    public IReadOnlyList<CalendarTaskVm> Tasks { get; init; } = [];
    public string MoreText { get; init; } = "";
    public bool HasMore => MoreText.Length > 0;
    public string AccessName { get; init; } = "";
    public override string ToString() => AccessName;
}

public sealed class DaySlotVm
{
    public required string Label { get; init; }
    public int? Hour { get; init; }
    public IReadOnlyList<CalendarTaskVm> Tasks { get; init; } = [];
    public bool IsNow { get; init; }
    public override string ToString() => Label + (Tasks.Count == 0 ? "" : ": " + string.Join(", ", Tasks.Select(task => task.Title)));
}

public sealed class StatTile
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    public bool Warning { get; init; }
    public override string ToString() => Label + ": " + Value;
}

public sealed class DayBar
{
    public required string Label { get; init; }
    public required int Count { get; init; }
    public required double Height { get; init; }
    public string AccessName => $"{Label}: {Count} completed";
    public override string ToString() => AccessName;
}

public sealed class HistoryRow
{
    public required string Title { get; init; }
    public required string When { get; init; }
    public required string ListName { get; init; }
    public override string ToString() => $"{Title}, completed {When}" + (ListName.Length > 0 ? ", " + ListName : "");
}

public sealed class ActiveReminder
{
    public required string TaskId { get; init; }
    public required string ReminderId { get; init; }
    public required string Title { get; init; }
    public required string When { get; init; }
    public override string ToString() => $"Reminder: {Title} at {When}";
}

public static class TodoFormat
{
    public static string Date(DateOnly date, DateOnly today)
    {
        var culture = CultureInfo.CurrentCulture;
        if (date == today)
            return "Today";
        if (date == today.AddDays(1))
            return "Tomorrow";
        if (date == today.AddDays(-1))
            return "Yesterday";
        return date.Year == today.Year ? date.ToString("ddd, MMM d", culture) : date.ToString("ddd, MMM d, yyyy", culture);
    }

    public static string Time(TimeOnly time) => time.ToString("t", CultureInfo.CurrentCulture);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static Brush? Brush(string? hex)
    {
        if (!TodoTransfer.IsHexColor(hex))
            return null;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex!));
        brush.Freeze();
        return brush;
    }

    /// <summary>Reads a date typed by the user: a culture date, ISO date, or words such as "tomorrow" or "fri".</summary>
    public static DateOnly? ParseDate(string? text, DateTime now)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return null;
        if (DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return date;
        var parsed = NaturalDateParser.Parse("x " + value, now);
        if (parsed.DueDate is { } natural)
            return natural;
        parsed = NaturalDateParser.Parse("x on " + value, now);
        return parsed.DueDate;
    }

    public static TimeOnly? ParseTime(string? text, DateTime now)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return null;
        if (TimeOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var time))
            return time;
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return time;
        return NaturalDateParser.Parse("x at " + value, now).DueTime;
    }

    public static readonly IReadOnlyList<(string Name, string? Hex)> Palette =
    [
        ("Default", null), ("Blue", "#2F6FDE"), ("Teal", "#0F8A8A"), ("Green", "#2E8B3E"), ("Amber", "#C77700"),
        ("Red", "#D13438"), ("Pink", "#C239B3"), ("Purple", "#7A4FD6"), ("Slate", "#5D6B7E"),
    ];
}
