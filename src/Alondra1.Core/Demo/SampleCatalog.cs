using Alondra1.Core.Mail;
using Alondra1.Core.Organizer;

namespace Alondra1.Core.Demo;

public static class SampleCatalog
{
    public const string Badge = "Demo";
    public const string Status = "Sample data";

    private static readonly (string From, string Subject, string? Body)[] Sources =
    [
        ("Maya Chen", "Re: Friday handoff", "The final review is complete. I’ve updated the handoff notes with the two items we discussed.\n\nCould you confirm the owner for the remaining follow-up?\n\nThanks,\nMaya"),
        ("Liam Weber", "Invoice for November", null),
        ("Design Team", "New navigation concepts", null),
        ("Ava Patel", "Notes from our call", null),
        ("Build Service", "Desktop build passed", null),
        ("Nora Kim", "Coffee next week?", null),
        ("Owen Clark", "Budget draft for review", null),
        ("Support", "Summary of open tickets", null),
        ("Travel Desk", "Booking confirmation", null),
        ("Lea Hoffmann", "Re: Updated timeline", null),
        ("Facilities", "Access card collection", null),
        ("Samira Ali", "Workshop agenda", null),
        ("Daniel Park", "Contract comments", null),
        ("Finance", "October expense report", null),
        ("Product Team", "Backlog review notes", null),
        ("Jamie Brooks", "Photo album link", null),
        ("Shipping", "Your order is on its way", null),
        ("Mina Torres", "Meeting moved to Tuesday", null),
        ("Ravi Shah", "Question about the prototype", null),
        ("Operations", "Weekly team update", null),
        ("Sara Jensen", "Re: Lunch on Thursday", null),
        ("IT Helpdesk", "Scheduled maintenance", null),
        ("Amir Novak", "Updated spreadsheet", null),
        ("Events", "Registration confirmed", null),
    ];

    private static readonly int[] Unread = [0, 2, 4, 7, 11, 16];
    private static readonly string[] Folders = ["Inbox", "Sent", "Drafts", "Archive", "Trash"];

    public static IReadOnlyList<string> FolderNames => Folders;

    public static IReadOnlyList<StoredMessage> Messages()
    {
        var list = new List<StoredMessage>();
        for (var group = 0; group < Folders.Length; group++)
        {
            var count = group == 0 ? Sources.Length : 7;
            for (var i = 0; i < count; i++)
            {
                var source = Sources[i];
                var account = i % 3 == 2 ? "Personal" : "Work";
                var time = i < 7
                    ? $"09:{(54 - (i * 7)):00}"
                    : $"Nov {14 - ((i - 7) / 4)}";
                var body = source.Body ?? $"Hi,\n\nFollowing up on “{source.Subject}.” Let me know if you have any questions.\n\nThanks,\n{source.From}";
                list.Add(new StoredMessage
                {
                    AccountId = account == "Work" ? "demo-work" : "demo-personal",
                    AccountName = account,
                    FolderId = Folders[group],
                    Uid = (group * 100) + i,
                    Subject = source.Subject,
                    FromName = source.From,
                    FromAddress = AddressFor(source.From),
                    ReceivedUtc = new DateTimeOffset(2026, 11, i < 7 ? 15 : 14 - ((i - 7) / 4), 9, Math.Max(0, 54 - (i * 7)), 0, TimeSpan.Zero),
                    TimeLabel = time,
                    IsUnread = group == 0 && Unread.Contains(i),
                    HasAttachments = source.Subject is "Invoice for November" or "Photo album link" or "Updated spreadsheet" or "Your order is on its way",
                    BodyDownloaded = true,
                    Availability = ItemAvailability.Downloaded,
                    Preview = body,
                    LocalOnly = false,
                });
            }
        }

        return list;
    }

    public static StoredBody BodyFor(StoredMessage message) => new()
    {
        Text = string.IsNullOrEmpty(message.Preview)
            ? $"Hi,\n\nFollowing up on “{message.Subject}.” Let me know if you have any questions.\n\nThanks,\n{message.FromName}"
            : message.Preview,
    };

    public static IReadOnlyList<StoredMessage> Query(string folder, string? account, string? search)
    {
        var query = (search ?? "").Trim();
        return Messages().Where(message =>
            message.FolderId == folder
            && (string.IsNullOrEmpty(account) || account == "All accounts" || message.AccountName == account)
            && (query.Length == 0 || $"{message.FromName} {message.Subject} {message.Preview}".Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static IReadOnlyList<CalendarEntry> Events() =>
    [
        new CalendarEntry { Id = "e1", AccountId = "demo-work", AccountName = "Work", Subject = "Project check-in", Start = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero) },
        new CalendarEntry { Id = "e2", AccountId = "demo-work", AccountName = "Work", Subject = "Design review", Start = new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero) },
        new CalendarEntry { Id = "e3", AccountId = "demo-work", AccountName = "Work", Subject = "Workshop", Start = new DateTimeOffset(2026, 11, 24, 11, 30, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.Zero) },
    ];

    public static IReadOnlyList<TaskEntry> Tasks() =>
    [
        new TaskEntry { Id = "t1", AccountId = "demo-work", AccountName = "Work", Title = "Confirm the handoff owner", Completed = false },
        new TaskEntry { Id = "t2", AccountId = "demo-work", AccountName = "Work", Title = "Review the November invoice", Completed = false },
        new TaskEntry { Id = "t3", AccountId = "demo-work", AccountName = "Work", Title = "Send the workshop agenda", Completed = true },
    ];

    public static IReadOnlyList<NoteEntry> Notes() =>
    [
        new NoteEntry { Id = "n1", AccountId = "demo-work", AccountName = "Work", Title = "Friday handoff", Body = "Confirm the owner for the follow-up.\nSend the final review notes to the team.", Revision = "1", UpdatedUtc = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero) },
        new NoteEntry { Id = "n2", AccountId = "demo-work", AccountName = "Work", Title = "Ideas", Body = "Add keyboard shortcuts for moving between folders.", Revision = "1", UpdatedUtc = new DateTimeOffset(2026, 11, 15, 9, 5, 0, TimeSpan.Zero) },
    ];

    public static IReadOnlyList<ContactEntry> Contacts() =>
    [
        new ContactEntry { Id = "c1", AccountId = "demo-work", AccountName = "Work", Name = "Maya Chen", Email = "maya.chen@example.com" },
        new ContactEntry { Id = "c2", AccountId = "demo-work", AccountName = "Work", Name = "Liam Weber", Email = "liam.weber@example.com" },
        new ContactEntry { Id = "c3", AccountId = "demo-work", AccountName = "Work", Name = "Ava Patel", Email = "ava.patel@example.com" },
        new ContactEntry { Id = "c4", AccountId = "demo-work", AccountName = "Work", Name = "Nora Kim", Email = "nora.kim@example.com" },
    ];

    private static string AddressFor(string name)
    {
        var slug = name.ToLowerInvariant().Replace(" ", ".", StringComparison.Ordinal);
        return slug + "@example.com";
    }
}
