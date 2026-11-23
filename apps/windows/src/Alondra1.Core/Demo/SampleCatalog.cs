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

    public static StoredBody BodyFor(StoredMessage message)
    {
        var body = new StoredBody
        {
            Text = string.IsNullOrEmpty(message.Preview)
                ? $"Hi,\n\nFollowing up on “{message.Subject}.” Let me know if you have any questions.\n\nThanks,\n{message.FromName}"
                : message.Preview,
        };
        if (message.Subject == "New navigation concepts")
            body.Html = NewsletterHtml;
        foreach (var (name, type, size) in AttachmentsFor(message.Subject))
        {
            body.Attachments.Add(new StoredAttachment
            {
                Id = $"{message.AccountId}|{message.FolderId}|{message.Uid}|{name}",
                AccountId = message.AccountId,
                FolderId = message.FolderId,
                Uid = message.Uid,
                FileName = name,
                ContentType = type,
                Size = size,
            });
        }

        return body;
    }

    private static IEnumerable<(string Name, string Type, long Size)> AttachmentsFor(string subject) => subject switch
    {
        "Invoice for November" => [("invoice-november.pdf", "application/pdf", 48_213)],
        "Photo album link" => [("beach.jpg", "image/jpeg", 1_204_551), ("sunset.jpg", "image/jpeg", 986_120)],
        "Updated spreadsheet" => [("budget-2027.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 31_877)],
        "Your order is on its way" => [("receipt.pdf", "application/pdf", 22_014)],
        _ => [],
    };

    /// <summary>A small HTML newsletter for the demo: layout, colors, a button, an embedded image, and a remote image.</summary>
    private const string NewsletterHtml = """
        <html><body bgcolor="#f3f2f8">
        <table width="600" align="center" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:12px;font-family:Segoe UI,sans-serif">
          <tr><td style="padding:24px 28px;background:#4b4fa8;color:#ffffff;border-radius:12px 12px 0 0">
            <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFUlEQVR4nGP4TyJgGNUwquH/sNUAAK+t/R8hxUFZAAAAAElFTkSuQmCC" width="16" height="16" alt="" style="vertical-align:middle">
            <span style="font-size:20px;font-weight:600;vertical-align:middle">&nbsp;Design notes · November</span></td></tr>
          <tr><td style="padding:20px 28px;color:#2a2940;font-size:14px;line-height:1.6">
            <h2 style="margin:0 0 8px;font-size:18px">New navigation concepts</h2>
            <p>We explored three directions for the workspace tabs. The rounded strip tested best: people found the open module faster and liked that nothing moved when switching.</p>
            <img src="https://images.example.com/concepts/tabs-overview.png" alt="Overview of the three tab concepts" width="544" height="220">
            <table width="100%" cellpadding="8" style="margin:12px 0;border-collapse:collapse">
              <tr style="background:#f1f0f5"><td><b>Concept</b></td><td><b>Found the open module</b></td></tr>
              <tr><td>Rounded strip</td><td>96%</td></tr>
              <tr style="background:#f9f8fc"><td>Underline tabs</td><td>81%</td></tr>
            </table>
            <p style="text-align:center;margin:20px 0"><a href="https://example.com/design/navigation" style="background:#4b4fa8;color:#ffffff;padding:10px 18px;border-radius:8px;text-decoration:none">Open the prototype</a></p>
            <p style="color:#5f5e7a;font-size:12px">You receive this because you are on the design list.</p>
          </td></tr>
        </table>
        </body></html>
        """;

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
