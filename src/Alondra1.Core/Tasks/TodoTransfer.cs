using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alondra1.Core.Tasks;

/// <summary>The JSON document written by export and accepted by import.</summary>
public sealed class TodoDocument
{
    public string Format { get; set; } = TodoTransfer.FormatName;
    public int Version { get; set; } = TodoTransfer.CurrentVersion;
    public DateTimeOffset ExportedUtc { get; set; }
    public List<ListGroup> Groups { get; set; } = [];
    public List<TaskList> Lists { get; set; } = [];
    public List<TodoTask> Tasks { get; set; } = [];
    public List<CompletionRecord> History { get; set; } = [];
}

public sealed class TodoImportPlan
{
    public bool Ok => Errors.Count == 0 && Data is not null;
    public TodoData? Data { get; init; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public string Summary => Data is null ? "" : $"{Data.Lists.Count} lists, {Data.Groups.Count} groups, {Data.Tasks.Count} tasks";
}

public static class TodoTransfer
{
    public const string FormatName = "alondra1-todo";
    public const int CurrentVersion = 1;
    public const int MaxTasks = 200_000;
    public const int MaxTitle = 2_000;
    public const int MaxNotes = 100_000;

    public static readonly JsonSerializerOptions Json = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string ExportJson(TodoData data, DateTimeOffset nowUtc) =>
        JsonSerializer.Serialize(new TodoDocument
        {
            ExportedUtc = nowUtc,
            Groups = data.Groups,
            Lists = data.Lists,
            Tasks = data.Tasks,
            History = data.History,
        }, Json);

    /// <summary>
    /// Validates a JSON export and prepares it for import. Every imported group, list, task, step, reminder, and history
    /// entry gets a new identifier, so an import adds data and never overwrites existing records.
    /// </summary>
    public static TodoImportPlan PrepareImport(string json, TodoData existing)
    {
        TodoDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<TodoDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            var failed = new TodoImportPlan();
            failed.Errors.Add("The file is not a valid Alondra1 to-do export: " + ex.Message);
            return failed;
        }

        return PrepareImport(document, existing);
    }

    public static TodoImportPlan PrepareImport(TodoDocument? document, TodoData existing)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (document is null)
            errors.Add("The file is empty.");
        else if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
            errors.Add($"The file format is '{document.Format}', not '{FormatName}'.");
        else if (document.Version > CurrentVersion)
            errors.Add($"The file was written by a newer Alondra1 (version {document.Version}). This version supports {CurrentVersion}.");
        else if (document.Version < 1)
            errors.Add("The file has no valid version number.");
        if (document is not null && errors.Count == 0)
        {
            document.Groups ??= [];
            document.Lists ??= [];
            document.Tasks ??= [];
            document.History ??= [];
            if (document.Tasks.Count > MaxTasks)
                errors.Add($"The file has {document.Tasks.Count} tasks; the limit is {MaxTasks}.");
            Duplicates(document.Groups.Select(item => item.Id), "group", errors);
            Duplicates(document.Lists.Select(item => item.Id), "list", errors);
            Duplicates(document.Tasks.Select(item => item.Id), "task", errors);
            if (document.Lists.Any(list => string.IsNullOrWhiteSpace(list.Id)))
                errors.Add("A list has no id.");
            if (document.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Id)))
                errors.Add("A task has no id.");
        }

        if (errors.Count > 0 || document is null)
        {
            var failed = new TodoImportPlan();
            failed.Errors.AddRange(errors);
            return failed;
        }

        var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var listMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var taskMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var data = new TodoData();
        var groupBase = existing.Groups.Count == 0 ? 0 : existing.Groups.Max(item => item.Position) + 1;
        foreach (var group in document.Groups.OrderBy(item => item.Position))
        {
            var copy = group.Clone();
            copy.Id = NewId.Create();
            copy.Name = Clip(string.IsNullOrWhiteSpace(copy.Name) ? "Imported group" : copy.Name.Trim(), 200);
            copy.Position = groupBase++;
            groupMap[group.Id] = copy.Id;
            data.Groups.Add(copy);
        }

        var existingNames = existing.Lists.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var listBase = existing.Lists.Count == 0 ? 0 : existing.Lists.Max(item => item.Position) + 1;
        foreach (var list in document.Lists.OrderBy(item => item.Position))
        {
            var copy = list.Clone();
            copy.Id = NewId.Create();
            copy.IsDefault = false;
            copy.Name = Clip(string.IsNullOrWhiteSpace(copy.Name) ? "Imported list" : copy.Name.Trim(), 200);
            if (existingNames.Contains(copy.Name))
                copy.Name += " (imported)";
            copy.GroupId = copy.GroupId is not null && groupMap.TryGetValue(copy.GroupId, out var group) ? group : null;
            if (list.GroupId is not null && copy.GroupId is null)
                warnings.Add($"List '{copy.Name}' referred to a missing group and was imported without one.");
            if (copy.Color is not null && !IsHexColor(copy.Color))
                copy.Color = null;
            copy.Position = listBase++;
            listMap[list.Id] = copy.Id;
            data.Lists.Add(copy);
        }

        TaskList? orphanage = null;
        foreach (var task in document.Tasks)
        {
            var copy = task.Clone();
            copy.Id = NewId.Create();
            taskMap[task.Id] = copy.Id;
            if (!listMap.TryGetValue(task.ListId ?? "", out var listId))
            {
                if (orphanage is null)
                {
                    orphanage = new TaskList { Name = "Imported tasks", Position = listBase++, CreatedUtc = document.ExportedUtc };
                    data.Lists.Add(orphanage);
                    warnings.Add("Some tasks referred to a missing list and were placed in 'Imported tasks'.");
                }

                listId = orphanage.Id;
            }

            copy.ListId = listId;
            copy.Title = Clip(string.IsNullOrWhiteSpace(copy.Title) ? "Untitled task" : copy.Title.Trim(), MaxTitle);
            copy.Notes = Clip(copy.Notes ?? "", MaxNotes);
            copy.Subtasks = (copy.Subtasks ?? []).Select(step => { step.Id = NewId.Create(); step.Title = Clip(step.Title ?? "", MaxTitle); return step; }).ToList();
            copy.Reminders = (copy.Reminders ?? []).GroupBy(item => item.At).Select(item => { var first = item.First(); first.Id = NewId.Create(); return first; }).ToList();
            copy.Attachments = (copy.Attachments ?? []).Where(item => IsSafeRelativePath(item.RelativePath)).Select(item => { item.Id = NewId.Create(); return item; }).ToList();
            if ((task.Attachments?.Count ?? 0) != copy.Attachments.Count)
                warnings.Add($"Unsafe attachment paths were dropped from '{copy.Title}'.");
            if (copy.Recurrence is { } rule)
            {
                rule.Interval = Math.Clamp(rule.Interval, 1, 999);
                rule.DaysOfWeek ??= [];
                if (copy.DueDate is null)
                    copy.Recurrence = null;
            }

            if (copy.Completed && copy.CompletedUtc is null)
                copy.CompletedUtc = copy.ModifiedUtc;
            data.Tasks.Add(copy);
        }

        foreach (var record in document.History)
        {
            var copy = record.Clone();
            copy.Id = NewId.Create();
            copy.TaskId = taskMap.TryGetValue(record.TaskId ?? "", out var mapped) ? mapped : "";
            copy.ListId = listMap.TryGetValue(record.ListId ?? "", out var list) ? list : "";
            data.History.Add(copy);
        }

        var plan = new TodoImportPlan { Data = data };
        plan.Warnings.AddRange(warnings.Distinct());
        return plan;
    }

    public static string ExportCsv(TodoData data, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        var lists = data.Lists.ToDictionary(item => item.Id);
        var groups = data.Groups.ToDictionary(item => item.Id);
        var builder = new StringBuilder();
        builder.AppendLine("List,Group,Title,Notes,Completed,Important,Due date,Due time,Start date,My Day,Repeats,Steps done,Steps,Created,Completed at");
        var listOrder = data.Lists.ToDictionary(item => item.Id, item => item.Position);
        foreach (var task in TaskSorting.Sort(data.Tasks, TaskSortMode.Manual, false, listOrder))
        {
            var list = lists.GetValueOrDefault(task.ListId);
            var group = list?.GroupId is { } groupId ? groups.GetValueOrDefault(groupId) : null;
            var (done, total) = task.SubtaskProgress;
            var cells = new[]
            {
                list?.Name ?? "",
                group?.Name ?? "",
                task.Title,
                task.Notes,
                task.Completed ? "yes" : "no",
                task.Important ? "yes" : "no",
                task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                task.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                task.MyDayDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                Recurrence.Describe(task.Recurrence),
                total == 0 ? "" : $"{done}/{total}",
                string.Join("; ", task.Subtasks.Select(step => (step.Completed ? "[x] " : "[ ] ") + step.Title)),
                task.CreatedUtc.ToString("yyyy-MM-dd HH:mm", culture),
                task.CompletedUtc?.ToString("yyyy-MM-dd HH:mm", culture) ?? "",
            };
            builder.AppendLine(string.Join(",", cells.Select(CsvCell)));
        }

        return builder.ToString();
    }

    /// <summary>Quotes a CSV cell (RFC 4180) and neutralizes spreadsheet formulas.</summary>
    public static string CsvCell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Writes tasks as iCalendar VTODO items (RFC 5545), including recurrence and reminders.</summary>
    public static string ExportIcs(TodoData data, DateTimeOffset nowUtc)
    {
        var lists = data.Lists.ToDictionary(item => item.Id);
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//alondra1//To-do//EN", "CALSCALE:GREGORIAN" };
        var stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var task in data.Tasks)
        {
            lines.Add("BEGIN:VTODO");
            lines.Add("UID:" + task.Id + "@alondra1.local");
            lines.Add("DTSTAMP:" + stamp);
            lines.Add("SUMMARY:" + IcsText(task.Title));
            if (!string.IsNullOrWhiteSpace(task.Notes))
                lines.Add("DESCRIPTION:" + IcsText(task.Notes));
            if (lists.TryGetValue(task.ListId, out var list))
                lines.Add("CATEGORIES:" + IcsText(list.Name));
            if (task.StartDate is { } start)
                lines.Add("DTSTART;VALUE=DATE:" + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (task.DueDate is { } due)
            {
                lines.Add(task.DueTime is { } time
                    ? "DUE:" + due.ToDateTime(time).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
                    : "DUE;VALUE=DATE:" + due.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }

            if (task.Important)
                lines.Add("PRIORITY:1");
            lines.Add("STATUS:" + (task.Completed ? "COMPLETED" : "NEEDS-ACTION"));
            if (task.Completed && task.CompletedUtc is { } completed)
                lines.Add("COMPLETED:" + completed.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            if (task.Recurrence is { } rule)
                lines.Add("RRULE:" + RRule(rule));
            foreach (var reminder in task.Reminders.Where(item => !item.Dismissed))
            {
                lines.Add("BEGIN:VALARM");
                lines.Add("ACTION:DISPLAY");
                lines.Add("DESCRIPTION:" + IcsText(task.Title));
                lines.Add("TRIGGER;VALUE=DATE-TIME:" + reminder.At.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                lines.Add("END:VALARM");
            }

            lines.Add("END:VTODO");
        }

        lines.Add("END:VCALENDAR");
        var builder = new StringBuilder();
        foreach (var line in lines)
            builder.Append(Fold(line)).Append("\r\n");
        return builder.ToString();
    }

    public static string RRule(RecurrenceRule rule)
    {
        var frequency = rule.Frequency.ToString().ToUpperInvariant();
        var text = $"FREQ={frequency};INTERVAL={Math.Max(1, rule.Interval)}";
        if (rule.Frequency == RecurrenceFrequency.Weekly && rule.DaysOfWeek.Count > 0)
            text += ";BYDAY=" + string.Join(",", rule.DaysOfWeek.Distinct().Select(day => day.ToString()[..2].ToUpperInvariant()));
        if (rule.Until is { } until)
            text += ";UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return text;
    }

    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>A stored attachment path must stay inside the task-file folder.</summary>
    public static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains(':')
        && path.Split('/', '\\').All(part => part is not ".." and not "." && part.Length > 0);

    private static void Duplicates(IEnumerable<string> ids, string kind, List<string> errors)
    {
        var duplicate = ids.Where(id => !string.IsNullOrEmpty(id)).GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            errors.Add($"The file contains the {kind} id '{duplicate.Key}' more than once.");
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static string IcsText(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");

    private static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes <= 75)
            return line;
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (count + size > 75)
            {
                builder.Append("\r\n ");
                count = 1;
            }

            builder.Append(rune.ToString());
            count += size;
        }

        return builder.ToString();
    }
}
