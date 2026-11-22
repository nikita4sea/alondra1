using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Organizer;
using Alondra1.Core.Security;

namespace Alondra1.Core.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISecretStore
{
    Task<SecretBundle> LoadAsync(string accountId, CancellationToken cancellationToken);
    Task SaveAsync(string accountId, SecretBundle bundle, CancellationToken cancellationToken);
    Task DeleteAsync(string accountId, CancellationToken cancellationToken);
}

public interface ITokenRefresher
{
    Task<SecretBundle> EnsureFreshAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken);
}

public interface IPersonalStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task CommitAsync(PersonalBatch batch, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredMessage>> ListAsync(string? accountId, string folderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredMessage>> SearchAsync(string? accountId, string? folderId, string query, CancellationToken cancellationToken);
    Task<StoredBody?> GetBodyAsync(MessageKey key, CancellationToken cancellationToken);
    Task<string?> GetCheckpointAsync(string accountId, string scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<QueuedOperation>> ListOperationsAsync(string? accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListPopUidlsAsync(string accountId, CancellationToken cancellationToken);
    Task<int> CountMessagesAsync(string? accountId, CancellationToken cancellationToken);
    Task DeleteAccountDataAsync(string accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredAttachment>> ListAttachmentsAsync(MessageKey key, CancellationToken cancellationToken);
    Task<StoredAttachment?> FindAttachmentAsync(string id, CancellationToken cancellationToken);
    Task SetAttachmentPinnedAsync(string id, bool pinned, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredAttachment>> ListUnpinnedAttachmentsAsync(string accountId, CancellationToken cancellationToken);
    Task ForgetAttachmentFileAsync(string id, CancellationToken cancellationToken);
    Task UpsertOrganizerAsync(string accountId, string kind, string id, string json, string? revision, bool pending, bool deleted, CancellationToken cancellationToken);
    Task<IReadOnlyList<(string Id, string Json, string? Revision, bool Pending, bool Deleted)>> ListOrganizerAsync(string accountId, string kind, CancellationToken cancellationToken);
    Task<long> DatabaseBytesAsync(CancellationToken cancellationToken);
}

public interface IMailGateway
{
    Task<ProbeResult> ProbeIncomingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<ProbeResult> ProbeOutgoingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<RemoteSyncBatch> FetchChangesAsync(AccountConfig account, SecretBundle secrets, RemoteFolder folder, string? checkpoint, int? historyLimit, CancellationToken cancellationToken);
    Task<StoredBody> FetchBodyAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, CancellationToken cancellationToken);
    Task FetchAttachmentAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, string fileName, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken);
    Task ApplyAsync(AccountConfig account, SecretBundle secrets, RemoteMailCommand command, CancellationToken cancellationToken);
    Task<SendResult> SendAsync(AccountConfig account, SecretBundle secrets, OutgoingMail mail, CancellationToken cancellationToken);
    Task IdleAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task DeletePopMessagesAsync(AccountConfig account, SecretBundle secrets, IReadOnlyList<string> uidls, CancellationToken cancellationToken);
}

public interface IOrganizerGateway
{
    Task<CapabilityStatus> GetStatusAsync(AccountConfig account, SecretBundle secrets, OrganizerFeature feature, CancellationToken cancellationToken);
    Task<IReadOnlyList<CalendarEntry>> ListEventsAsync(AccountConfig account, SecretBundle secrets, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
    Task<CalendarEntry> SaveEventAsync(AccountConfig account, SecretBundle secrets, CalendarEntry entry, CancellationToken cancellationToken);
    Task DeleteEventAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskEntry>> ListTasksAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<TaskEntry> SaveTaskAsync(AccountConfig account, SecretBundle secrets, TaskEntry entry, CancellationToken cancellationToken);
    Task DeleteTaskAsync(AccountConfig account, SecretBundle secrets, string listId, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContactEntry>> ListContactsAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<ContactEntry> SaveContactAsync(AccountConfig account, SecretBundle secrets, ContactEntry entry, CancellationToken cancellationToken);
    Task DeleteContactAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<NoteEntry>> ListNotesAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken);
    Task<NoteEntry> SaveNoteAsync(AccountConfig account, SecretBundle secrets, NoteEntry entry, CancellationToken cancellationToken);
    Task DeleteNoteAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken);
}

public interface IConflictSink
{
    void ReportPreservedBoth(string description);
}

public sealed class SyncReport
{
    public bool Ok { get; init; }
    public bool Offline { get; init; }
    public bool Blocked { get; init; }
    public string Detail { get; init; } = "";
    public bool DegradedDedup { get; init; }
    /// <summary>The server's folders seen by this sync (empty when the sync did not reach the server).</summary>
    public IReadOnlyList<Alondra1.Core.Mail.RemoteFolder> Folders { get; init; } = [];

    public static SyncReport Done(string detail) => new() { Ok = true, Detail = detail };
    public static SyncReport Failed(string detail, bool offline = false) => new() { Detail = detail, Offline = offline };
    public static SyncReport Stopped(string detail) => new() { Blocked = true, Detail = detail };
}

public sealed class SessionCache : ISessionCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<StoredMessage>> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredBody> _bodies = new(StringComparer.Ordinal);

    public void SetMessages(string accountId, string folderId, IReadOnlyList<StoredMessage> messages)
    {
        lock (_gate)
            _messages[Key(accountId, folderId)] = messages.ToList();
    }

    public void Upsert(StoredMessage message)
    {
        lock (_gate)
        {
            var key = Key(message.AccountId, message.FolderId);
            if (!_messages.TryGetValue(key, out var list))
            {
                list = [];
                _messages[key] = list;
            }

            var index = list.FindIndex(item => item.Uid == message.Uid);
            if (index >= 0)
                list[index] = message;
            else
                list.Insert(0, message);
        }
    }

    public IReadOnlyList<StoredMessage> List(string? accountId, string folderId)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(accountId))
                return _messages.TryGetValue(Key(accountId, folderId), out var list) ? list.ToList() : [];
            return _messages.Where(pair => pair.Key.EndsWith("|" + folderId, StringComparison.Ordinal))
                .SelectMany(pair => pair.Value)
                .ToList();
        }
    }

    public IReadOnlyList<StoredMessage> Search(string? accountId, string? folderId, string query)
    {
        var source = List(accountId, folderId ?? "");
        if (string.IsNullOrWhiteSpace(folderId))
        {
            lock (_gate)
            {
                source = _messages.Where(pair => accountId is null || pair.Key.StartsWith(accountId + "|", StringComparison.Ordinal))
                    .SelectMany(pair => pair.Value)
                    .ToList();
            }
        }

        var text = query.Trim();
        return source.Where(message =>
            $"{message.FromName} {message.Subject} {message.Preview}".Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void SetBody(MessageKey key, StoredBody body)
    {
        lock (_gate)
            _bodies[BodyKey(key)] = body;
    }

    public StoredBody? GetBody(MessageKey key)
    {
        lock (_gate)
            return _bodies.TryGetValue(BodyKey(key), out var body) ? body : null;
    }

    public void ClearAccount(string accountId)
    {
        lock (_gate)
        {
            foreach (var key in _messages.Keys.Where(item => item.StartsWith(accountId + "|", StringComparison.Ordinal)).ToList())
                _messages.Remove(key);
        }
    }

    private static string Key(string accountId, string folderId) => accountId + "|" + folderId;
    private static string BodyKey(MessageKey key) => key.AccountId + "|" + key.FolderId + "|" + key.Uid;
}

public interface ISessionCache
{
    void SetMessages(string accountId, string folderId, IReadOnlyList<StoredMessage> messages);
    void Upsert(StoredMessage message);
    IReadOnlyList<StoredMessage> List(string? accountId, string folderId);
    IReadOnlyList<StoredMessage> Search(string? accountId, string? folderId, string query);
    void SetBody(MessageKey key, StoredBody body);
    StoredBody? GetBody(MessageKey key);
    void ClearAccount(string accountId);
}
