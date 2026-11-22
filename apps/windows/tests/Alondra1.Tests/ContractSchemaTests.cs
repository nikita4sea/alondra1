using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alondra1.Core.Tasks;

namespace Alondra1.Tests;

/// <summary>
/// Keeps the shared task contract (packages/api-contract/schemas/todo-v1.schema.json) and its fixtures
/// (packages/shared-models/fixtures/todo-v1) identical to what the Windows model actually serializes.
/// Set ALONDRA1_UPDATE_FIXTURES=1 to rewrite the fixtures after an intended model change.
/// </summary>
public class ContractSchemaTests
{
    private static readonly string Repo = FindMonorepoRoot();
    private static string SchemaPath => Path.Combine(Repo, "packages", "api-contract", "schemas", "todo-v1.schema.json");
    private static string FixtureDirectory => Path.Combine(Repo, "packages", "shared-models", "fixtures", "todo-v1");

    public static TheoryData<Type, string> ModelTypes => new()
    {
        { typeof(TodoTask), "TodoTask" },
        { typeof(TaskList), "TaskList" },
        { typeof(ListGroup), "ListGroup" },
        { typeof(Subtask), "Subtask" },
        { typeof(TaskReminder), "TaskReminder" },
        { typeof(TaskAttachment), "TaskAttachment" },
        { typeof(RecurrenceRule), "RecurrenceRule" },
        { typeof(CompletionRecord), "CompletionRecord" },
        { typeof(TaskPreferences), "TaskPreferences" },
    };

    [Theory]
    [MemberData(nameof(ModelTypes))]
    public void Schema_properties_match_the_serialized_model(Type type, string definition)
    {
        var schema = Schema();
        var declared = schema["$defs"]![definition]!["properties"]!.AsObject().Select(pair => pair.Key).Order().ToList();
        Assert.Equal(SerializedNames(type), declared);
    }

    [Fact]
    public void Document_root_matches_the_export_document()
    {
        var declared = Schema()["properties"]!.AsObject().Select(pair => pair.Key).Order().ToList();
        Assert.Equal(SerializedNames(typeof(TodoDocument)), declared);
        Assert.Equal(TodoTransfer.FormatName, (string?)Schema()["properties"]!["format"]!["const"]);
        Assert.Equal(TodoTransfer.CurrentVersion, (int)Schema()["properties"]!["version"]!["maximum"]!);
    }

    [Theory]
    [InlineData(typeof(RecurrenceFrequency), "RecurrenceRule", "frequency")]
    [InlineData(typeof(TaskStatusFilter), "TaskPreferences", "statusFilter")]
    [InlineData(typeof(CalendarMode), "TaskPreferences", "calendarMode")]
    public void Schema_enums_match_the_model(Type enumType, string definition, string property)
    {
        var values = Schema()["$defs"]![definition]!["properties"]![property]!["enum"]!.AsArray().Select(node => (string)node!).ToList();
        Assert.Equal(Enum.GetNames(enumType), values);
    }

    [Fact]
    public void Schema_day_names_and_sort_modes_match_the_model()
    {
        var days = Schema()["$defs"]!["dayOfWeek"]!["enum"]!.AsArray().Select(node => (string)node!).ToList();
        Assert.Equal(Enum.GetNames<DayOfWeek>(), days);
        var sorts = Schema()["$defs"]!["TaskPreferences"]!["properties"]!["sortByView"]!["additionalProperties"]!["enum"]!.AsArray().Select(node => (string)node!).ToList();
        Assert.Equal(Enum.GetNames<TaskSortMode>(), sorts);
    }

    [Fact]
    public void Full_document_fixture_is_what_the_windows_model_writes()
    {
        var json = TodoTransfer.ExportJson(Sample(), new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(FixtureDirectory, "full-document.json");
        if (Environment.GetEnvironmentVariable("ALONDRA1_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(FixtureDirectory);
            File.WriteAllText(path, json.ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(File.Exists(path), $"Missing fixture {path}. Run the tests once with ALONDRA1_UPDATE_FIXTURES=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd(), json.ReplaceLineEndings("\n").TrimEnd());

        // The fixture round-trips through the model without losing or changing anything.
        var document = JsonSerializer.Deserialize<TodoDocument>(File.ReadAllText(path), TodoTransfer.Json)!;
        var again = TodoTransfer.ExportJson(new TodoData { Groups = document.Groups, Lists = document.Lists, Tasks = document.Tasks, History = document.History }, document.ExportedUtc);
        Assert.Equal(json.ReplaceLineEndings("\n").TrimEnd(), again.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Fixture_uses_only_properties_declared_in_the_schema()
    {
        var schema = Schema();
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "full-document.json")))!.AsObject();
        AssertDeclared(document, schema["properties"]!.AsObject());
        foreach (var (collection, definition) in new[] { ("groups", "ListGroup"), ("lists", "TaskList"), ("tasks", "TodoTask"), ("history", "CompletionRecord") })
        {
            foreach (var item in document[collection]!.AsArray())
                AssertDeclared(item!.AsObject(), schema["$defs"]![definition]!["properties"]!.AsObject());
        }

        foreach (var task in document["tasks"]!.AsArray().Select(node => node!.AsObject()))
        {
            foreach (var (collection, definition) in new[] { ("reminders", "TaskReminder"), ("subtasks", "Subtask"), ("attachments", "TaskAttachment") })
            {
                foreach (var item in task[collection]?.AsArray() ?? [])
                    AssertDeclared(item!.AsObject(), schema["$defs"]![definition]!["properties"]!.AsObject());
            }

            if (task["recurrence"] is JsonObject recurrence)
                AssertDeclared(recurrence, schema["$defs"]!["RecurrenceRule"]!["properties"]!.AsObject());
        }
    }

    /// <summary>
    /// The cloud never stores device-local state (reminder delivery, group collapse). A document in that shape, as the
    /// backend's export returns it, must still import into the Windows app with nothing lost but those fields.
    /// </summary>
    [Fact]
    public void Cloud_shaped_documents_import_into_the_windows_app()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "full-document.json")))!.AsObject();
        foreach (var group in document["groups"]!.AsArray())
            group!.AsObject().Remove("collapsed");
        foreach (var task in document["tasks"]!.AsArray())
        {
            foreach (var reminder in task!["reminders"]?.AsArray() ?? [])
            {
                foreach (var deviceLocal in new[] { "deliveredFor", "dismissed", "snoozedUntilUtc" })
                    reminder!.AsObject().Remove(deviceLocal);
            }
        }

        var plan = TodoTransfer.PrepareImport(document.ToJsonString(), new TodoData());
        Assert.True(plan.Ok, string.Join("; ", plan.Errors));
        Assert.Equal(3, plan.Data!.Tasks.Count);
        Assert.Equal(2, plan.Data.Lists.Count);
        Assert.Equal(3, plan.Data.History.Count);
        var monthEnd = plan.Data.Tasks.Single(task => task.Title == "Close the books");
        Assert.Equal(new DateOnly(2026, 3, 31), monthEnd.DueDate);
        Assert.Equal(new DateTime(2026, 3, 29, 2, 30, 0), monthEnd.Reminders[0].At);
        Assert.Null(monthEnd.Reminders[0].DeliveredFor);
        Assert.Equal(["Export statements", "Reconcile", "Send summary"], monthEnd.Subtasks.Select(step => step.Title));
    }

    private static void AssertDeclared(JsonObject value, JsonObject properties)
    {
        foreach (var (name, _) in value)
            Assert.True(properties.ContainsKey(name), $"'{name}' is not declared in the schema.");
    }

    /// <summary>
    /// Covers the cases the backend must preserve: month-end recurrence with an anchor, a weekly weekday set, a reminder
    /// at a local time that does not exist on the DST change day, a due time, My Day, ordered steps, attachments,
    /// grouped and tinted lists with emoji, and completion history for two occurrences of one recurring task.
    /// </summary>
    private static TodoData Sample()
    {
        var created = new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero);
        var work = new ListGroup { Id = "group-work", Name = "Work", Position = 1 };
        var tasks = new TaskList { Id = "list-tasks", Name = "Tasks", Position = 0, IsDefault = true, CreatedUtc = created };
        var launch = new TaskList { Id = "list-launch", Name = "🚀 Launch", GroupId = work.Id, Color = "#6C6DB0", TintBackground = true, Position = 1, CreatedUtc = created };
        var monthEnd = new TodoTask
        {
            Id = "task-month-end",
            ListId = launch.Id,
            Title = "Close the books",
            Notes = "Runs on the last day of each month.",
            Important = true,
            Position = 1,
            CreatedUtc = created,
            ModifiedUtc = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
            DueDate = new DateOnly(2026, 3, 31),
            DueTime = new TimeOnly(16, 0),
            Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = 1, Anchor = new DateOnly(2026, 1, 31), Until = new DateOnly(2026, 12, 31) },
            Reminders =
            [
                // 02:30 does not exist in Central Europe on 2026-03-29 (clocks go 02:00 -> 03:00); stored as wall clock, not UTC.
                new TaskReminder { Id = "reminder-dst", At = new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Unspecified), DeliveredFor = new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Unspecified) },
                new TaskReminder { Id = "reminder-due", At = new DateTime(2026, 3, 31, 15, 0, 0, DateTimeKind.Unspecified), SnoozedUntilUtc = new DateTimeOffset(2026, 3, 31, 13, 10, 0, TimeSpan.Zero) },
            ],
            Subtasks =
            [
                new Subtask { Id = "step-1", Title = "Export statements", Completed = true, CompletedUtc = new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero) },
                new Subtask { Id = "step-2", Title = "Reconcile" },
                new Subtask { Id = "step-3", Title = "Send summary" },
            ],
            Attachments = [new TaskAttachment { Id = "file-1", FileName = "checklist.pdf", RelativePath = "task-month-end/checklist.pdf", Size = 48213, AddedUtc = created }],
        };
        var weekly = new TodoTask
        {
            Id = "task-weekly",
            ListId = tasks.Id,
            Title = "Water the plants",
            Position = 2,
            CreatedUtc = created,
            ModifiedUtc = created,
            StartDate = new DateOnly(2026, 11, 2),
            DueDate = new DateOnly(2026, 11, 23),
            MyDayDate = new DateOnly(2026, 11, 21),
            Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, Interval = 2, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Thursday], Anchor = new DateOnly(2026, 11, 2) },
        };
        var done = new TodoTask
        {
            Id = "task-done",
            ListId = tasks.Id,
            Title = "Renew passport",
            Completed = true,
            Position = 3,
            CreatedUtc = created,
            ModifiedUtc = new DateTimeOffset(2026, 2, 10, 17, 45, 0, new TimeSpan(1, 0, 0)),
            CompletedUtc = new DateTimeOffset(2026, 2, 10, 17, 45, 0, new TimeSpan(1, 0, 0)),
        };
        return new TodoData
        {
            Groups = [work],
            Lists = [tasks, launch],
            Tasks = [monthEnd, weekly, done],
            History =
            [
                new CompletionRecord { Id = "done-jan", TaskId = monthEnd.Id, Title = monthEnd.Title, ListId = launch.Id, CompletedUtc = new DateTimeOffset(2026, 1, 31, 16, 5, 0, TimeSpan.Zero), OccurrenceDate = new DateOnly(2026, 1, 31) },
                new CompletionRecord { Id = "done-feb", TaskId = monthEnd.Id, Title = monthEnd.Title, ListId = launch.Id, CompletedUtc = new DateTimeOffset(2026, 2, 28, 15, 0, 0, TimeSpan.Zero), OccurrenceDate = new DateOnly(2026, 2, 28) },
                new CompletionRecord { Id = "done-passport", TaskId = done.Id, Title = done.Title, ListId = tasks.Id, CompletedUtc = done.CompletedUtc!.Value },
            ],
        };
    }

    private static List<string> SerializedNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order()
            .ToList();

    private static JsonObject Schema() => JsonNode.Parse(File.ReadAllText(SchemaPath))!.AsObject();

    private static string FindMonorepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "packages", "api-contract")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Monorepo root (with packages/api-contract) not found.");
    }
}
