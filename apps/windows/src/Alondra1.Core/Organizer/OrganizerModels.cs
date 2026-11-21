namespace Alondra1.Core.Organizer;

public enum OrganizerFeature
{
    Calendar,
    Tasks,
    Contacts,
    Notes,
}

public sealed record CapabilityStatus(bool Available, string AccountId, string AccountName, string? Reason);

public sealed class CalendarEntry
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string? Revision { get; set; }
    public bool Pending { get; set; }
}

public sealed class TaskEntry
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string ListId { get; set; } = "";
    public string ListName { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public string? Revision { get; set; }
    public bool Pending { get; set; }
}

public sealed class ContactEntry
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Revision { get; set; }
    public bool Pending { get; set; }
}

public sealed class NoteEntry
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public bool Pending { get; set; }
}

public static class OrganizerCopy
{
    public const string ManualUnavailable =
        "This account is for mail only. Choose a Microsoft 365 or Google account for calendar, to-do, notes, and contacts.";

    public const string NotesAreAppNotes =
        "Notes are stored by Alondra1 in the account's app folder. They are not Google Keep or OneNote notes.";

    public const string ChatPlaceholder = "Chat is not available yet.";

    public const string ConsentDenied = "This feature is off because the account did not grant permission.";

    public const string LicenseOrPlan = "This feature is not available for the signed-in account.";
}
