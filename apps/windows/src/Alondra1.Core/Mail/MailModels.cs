namespace Alondra1.Core.Mail;

public enum ItemAvailability
{
    RemoteOnly,
    Downloaded,
    Pending,
    Failed,
}

public enum SmtpAcceptance
{
    Queued,
    Submitting,
    Accepted,
    Failed,
    Uncertain,
}

public enum FailureKind
{
    Network,
    Authentication,
    Certificate,
    RequiredUpgrade,
    Protocol,
    Cancelled,
}

public readonly record struct MessageKey(string AccountId, string FolderId, long Uid);

public sealed class StoredMessage
{
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public long Uid { get; set; }
    public string? MessageId { get; set; }
    public string Subject { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string Recipients { get; set; } = "";
    public DateTimeOffset ReceivedUtc { get; set; }
    public string TimeLabel { get; set; } = "";
    public bool IsUnread { get; set; }
    public bool IsFlagged { get; set; }
    public bool HasAttachments { get; set; }
    public bool BodyDownloaded { get; set; }
    public bool LocalOnly { get; set; }
    public ItemAvailability Availability { get; set; } = ItemAvailability.Downloaded;
    public string? PopUidl { get; set; }
    public string Preview { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string? SendState { get; set; }

    public string AccessibilityName
    {
        get
        {
            var unread = IsUnread ? "Unread, " : "";
            var account = string.IsNullOrWhiteSpace(AccountName) ? "" : AccountName + ", ";
            return $"{unread}{FromName}, {account}{Subject}, {TimeLabel}";
        }
    }
}

public sealed class StoredBody
{
    public string Text { get; set; } = "";

    /// <summary>The HTML version, with inline (cid:) images already embedded as data: URIs so it shows offline.</summary>
    public string? Html { get; set; }

    /// <summary>Downloadable parts (not the inline images used by <see cref="Html"/>).</summary>
    public List<StoredAttachment> Attachments { get; set; } = [];
}

public sealed class StoredAttachment
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public long Uid { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? CacheRelativePath { get; set; }
    public bool Downloaded { get; set; }
    public bool Pinned { get; set; }
    public string? ContentId { get; set; }
}

public sealed class QueuedOperation
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public SmtpAcceptance State { get; set; } = SmtpAcceptance.Queued;
    public string? MessageId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public int Attempt { get; set; }
    public string? LastError { get; set; }
}

public sealed class OutgoingAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public string? SourcePath { get; set; }
}

public sealed class OutgoingMail
{
    public string? MessageId { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public List<string> Bcc { get; set; } = [];
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string? InReplyTo { get; set; }
    public List<OutgoingAttachment> Attachments { get; set; } = [];
}

public sealed class RemoteFolder
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SpecialUse { get; set; }
    public uint UidValidity { get; set; }
    public ulong HighestModSeq { get; set; }
}

public sealed class RemoteSyncBatch
{
    public bool ResetMailbox { get; set; }
    public bool UidlSupported { get; set; } = true;
    public bool DegradedDedup { get; set; }
    public List<StoredMessage> Upserts { get; set; } = [];
    public List<long> RemoteUids { get; set; } = [];
    public bool RemoteUidsComplete { get; set; }
    public string NextCheckpoint { get; set; } = "";
    public bool SupportsIdle { get; set; }
    public bool UsedCondStore { get; set; }
    public bool UsedQuickResync { get; set; }
}

public sealed record ProbeResult(bool Ok, string Detail, FailureKind? Failure);

public sealed record SendResult(SmtpAcceptance Acceptance, string MessageId, string? Error, bool SentCopyStored);

public sealed record TransportFailure(FailureKind Kind, string Message);

public sealed class PersonalBatch
{
    public string? ClearFolderAccountId { get; set; }
    public string? ClearFolderId { get; set; }
    public List<StoredMessage> Upserts { get; } = [];
    public List<MessageKey> Deletes { get; } = [];
    public List<(MessageKey Key, string Text, string? Html)> Bodies { get; } = [];
    public List<StoredAttachment> Attachments { get; } = [];
    public List<QueuedOperation> Operations { get; } = [];
    public (string AccountId, string Scope, string Token)? Checkpoint { get; set; }
}

public enum MailCommandKind
{
    SetRead,
    SetFlagged,
    Move,
    Delete,
    Archive,
    SaveDraft,
}

public sealed class RemoteMailCommand
{
    public MailCommandKind Kind { get; set; }
    public string FolderId { get; set; } = "";
    public long Uid { get; set; }
    public string? DestinationFolderId { get; set; }
    public bool Value { get; set; }
    public OutgoingMail? Draft { get; set; }
}

public static class MessageIdentity
{
    public static string Ensure(string? existing) =>
        string.IsNullOrWhiteSpace(existing) ? $"<{Guid.NewGuid():N}@alondra1.local>" : existing.Trim();

    public static long StableUid(string value)
    {
        ulong hash = 14695981039346656037ul;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211ul;
        }

        var id = (long)(hash & 0x7FFF_FFFF_FFFF_FFFF);
        return id == 0 ? 1 : id;
    }
}

public static class MailText
{
    public static string FormatTime(DateTimeOffset received, DateTimeOffset now)
    {
        var local = received.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
            return local.ToString("HH:mm");
        return local.ToString("MMM d");
    }

    public static bool IsExecutableName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".com" or ".msi" or ".js" or ".vbs" or ".ps1" or ".scr" or ".dll" or ".lnk" or ".hta" or ".cpl" or ".jar";
    }
}
