using System.Globalization;
using System.Text;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

public sealed class SqlitePersonalStore : IPersonalStore, IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqlitePersonalStore(string databasePath) => _path = databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = _connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await MigrateSearchKeysAsync(_connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitAsync(PersonalBatch batch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(batch.ClearFolderAccountId) && !string.IsNullOrEmpty(batch.ClearFolderId))
                await ClearFolderAsync(connection, tx, batch.ClearFolderAccountId, batch.ClearFolderId, cancellationToken).ConfigureAwait(false);
            foreach (var key in batch.Deletes)
                await DeleteMessageAsync(connection, tx, key, cancellationToken).ConfigureAwait(false);
            var bodiesInBatch = batch.Bodies.Select(body => body.Key).ToHashSet();
            foreach (var message in batch.Upserts)
                await UpsertMessageAsync(connection, tx, message, bodiesInBatch, cancellationToken).ConfigureAwait(false);
            foreach (var (key, text, html) in batch.Bodies)
                await UpsertBodyAsync(connection, tx, key, text, html, cancellationToken).ConfigureAwait(false);
            foreach (var attachment in batch.Attachments)
                await UpsertAttachmentAsync(connection, tx, attachment, cancellationToken).ConfigureAwait(false);
            foreach (var operation in batch.Operations)
                await UpsertOperationAsync(connection, tx, operation, cancellationToken).ConfigureAwait(false);
            if (batch.Checkpoint is { } checkpoint)
                await SetCheckpointAsync(connection, tx, checkpoint.AccountId, checkpoint.Scope, checkpoint.Token, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<StoredMessage>> ListAsync(string? accountId, string folderId, CancellationToken cancellationToken) =>
        QueryMessagesAsync(accountId, folderId, null, cancellationToken);

    public async Task<IReadOnlyList<StoredMessage>> SearchAsync(string? accountId, string? folderId, string query, CancellationToken cancellationToken)
    {
        var match = BuildFtsMatch(query);
        if (match is null)
            return await QueryMessagesAsync(accountId, folderId, null, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var command = connection.CreateCommand();
            var sql = new StringBuilder("""
                SELECT m.* FROM messages_fts
                INNER JOIN search_keys k ON k.id = messages_fts.rowid
                INNER JOIN messages m ON m.account_id = k.account_id AND m.folder_id = k.folder_id AND m.uid = k.uid
                WHERE messages_fts MATCH $match
                """);
            command.Parameters.AddWithValue("$match", match);
            if (!string.IsNullOrEmpty(accountId))
            {
                sql.Append(" AND k.account_id = $account");
                command.Parameters.AddWithValue("$account", accountId);
            }

            if (!string.IsNullOrEmpty(folderId))
            {
                sql.Append(" AND k.folder_id = $folder");
                command.Parameters.AddWithValue("$folder", folderId);
            }

            sql.Append(" ORDER BY m.received_utc DESC");
            command.CommandText = sql.ToString();
            return await ReadMessagesAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredBody?> GetBodyAsync(MessageKey key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT text_body, html_body FROM bodies WHERE account_id = $a AND folder_id = $f AND uid = $u";
            command.Parameters.AddWithValue("$a", key.AccountId);
            command.Parameters.AddWithValue("$f", key.FolderId);
            command.Parameters.AddWithValue("$u", key.Uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            return new StoredBody
            {
                Text = reader.GetString(0),
                Html = reader.IsDBNull(1) ? null : reader.GetString(1),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetCheckpointAsync(string accountId, string scope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT token FROM checkpoints WHERE account_id = $a AND scope = $s";
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$s", scope);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value as string;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedOperation>> ListOperationsAsync(string? accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = string.IsNullOrEmpty(accountId)
                ? "SELECT id, account_id, kind, payload_json, state, message_id, created_utc, updated_utc, attempt, last_error FROM operations"
                : "SELECT id, account_id, kind, payload_json, state, message_id, created_utc, updated_utc, attempt, last_error FROM operations WHERE account_id = $a";
            if (!string.IsNullOrEmpty(accountId))
                command.Parameters.AddWithValue("$a", accountId);
            var list = new List<QueuedOperation>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new QueuedOperation
                {
                    Id = reader.GetString(0),
                    AccountId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    PayloadJson = reader.GetString(3),
                    State = Enum.Parse<SmtpAcceptance>(reader.GetString(4)),
                    MessageId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    UpdatedUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    Attempt = reader.GetInt32(8),
                    LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListPopUidlsAsync(string accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT pop_uidl FROM messages WHERE account_id = $a AND pop_uidl IS NOT NULL";
            command.Parameters.AddWithValue("$a", accountId);
            var list = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(reader.GetString(0));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountMessagesAsync(string? accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = string.IsNullOrEmpty(accountId)
                ? "SELECT COUNT(*) FROM messages"
                : "SELECT COUNT(*) FROM messages WHERE account_id = $a";
            if (!string.IsNullOrEmpty(accountId))
                command.Parameters.AddWithValue("$a", accountId);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAccountDataAsync(string accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var removeIndex = connection.CreateCommand();
            removeIndex.Transaction = tx;
            removeIndex.CommandText = "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM search_keys WHERE account_id = $a)";
            removeIndex.Parameters.AddWithValue("$a", accountId);
            await removeIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var table in new[] { "search_keys", "bodies", "attachments", "operations", "checkpoints", "organizer", "messages" })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = $"DELETE FROM {table} WHERE account_id = $a";
                command.Parameters.AddWithValue("$a", accountId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAttachmentsAsync(MessageKey key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, file_name, content_type, size, cache_path, downloaded, pinned, content_id
                FROM attachments WHERE account_id = $a AND folder_id = $f AND uid = $u
                """;
            command.Parameters.AddWithValue("$a", key.AccountId);
            command.Parameters.AddWithValue("$f", key.FolderId);
            command.Parameters.AddWithValue("$u", key.Uid);
            return await ReadAttachmentsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredAttachment?> FindAttachmentAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, file_name, content_type, size, cache_path, downloaded, pinned, content_id
                FROM attachments WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            var list = await ReadAttachmentsAsync(command, cancellationToken).ConfigureAwait(false);
            return list.FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAttachmentPinnedAsync(string id, bool pinned, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "UPDATE attachments SET pinned = $p WHERE id = $id";
            command.Parameters.AddWithValue("$p", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListUnpinnedAttachmentsAsync(string accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, file_name, content_type, size, cache_path, downloaded, pinned, content_id
                FROM attachments WHERE account_id = $a AND pinned = 0 AND downloaded = 1
                """;
            command.Parameters.AddWithValue("$a", accountId);
            return await ReadAttachmentsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetAttachmentFileAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "UPDATE attachments SET cache_path = NULL, downloaded = 0 WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertOrganizerAsync(string accountId, string kind, string id, string json, string? revision, bool pending, bool deleted, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = """
                INSERT INTO organizer(account_id, kind, remote_id, revision, json, pending, deleted)
                VALUES ($a, $k, $id, $r, $j, $p, $d)
                ON CONFLICT(account_id, kind, remote_id) DO UPDATE SET
                  revision = excluded.revision, json = excluded.json, pending = excluded.pending, deleted = excluded.deleted
                """;
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$k", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$r", (object?)revision ?? DBNull.Value);
            command.Parameters.AddWithValue("$j", json);
            command.Parameters.AddWithValue("$p", pending ? 1 : 0);
            command.Parameters.AddWithValue("$d", deleted ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<(string Id, string Json, string? Revision, bool Pending, bool Deleted)>> ListOrganizerAsync(string accountId, string kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT remote_id, json, revision, pending, deleted FROM organizer WHERE account_id = $a AND kind = $k";
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$k", kind);
            var list = new List<(string, string, string?, bool, bool)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3) == 1,
                    reader.GetInt32(4) == 1));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<long> DatabaseBytesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return Task.FromResult(0L);
        return Task.FromResult(new FileInfo(_path).Length);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public static string? BuildFtsMatch(string query)
    {
        var cleaned = new string(query.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '@' or '.' or '-' or '\'' ? ch : ' ').ToArray());
        var terms = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return null;
        return string.Join(" AND ", terms.Select(term => "\"" + term.Replace("\"", "", StringComparison.Ordinal) + "\"*"));
    }

    private async Task<IReadOnlyList<StoredMessage>> QueryMessagesAsync(string? accountId, string? folderId, string? extra, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            var sql = new StringBuilder("SELECT * FROM messages WHERE 1 = 1");
            if (!string.IsNullOrEmpty(accountId))
            {
                sql.Append(" AND account_id = $account");
                command.Parameters.AddWithValue("$account", accountId);
            }

            if (!string.IsNullOrEmpty(folderId))
            {
                sql.Append(" AND folder_id = $folder");
                command.Parameters.AddWithValue("$folder", folderId);
            }

            if (!string.IsNullOrEmpty(extra))
                sql.Append(extra);
            sql.Append(" ORDER BY received_utc DESC");
            command.CommandText = sql.ToString();
            return await ReadMessagesAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<StoredMessage>> ReadMessagesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = ColumnMap(reader);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new StoredMessage
            {
                AccountId = reader.GetString(ordinal["account_id"]),
                FolderId = reader.GetString(ordinal["folder_id"]),
                Uid = reader.GetInt64(ordinal["uid"]),
                MessageId = reader.IsDBNull(ordinal["message_id"]) ? null : reader.GetString(ordinal["message_id"]),
                Subject = reader.GetString(ordinal["subject"]),
                FromName = reader.GetString(ordinal["from_name"]),
                FromAddress = reader.GetString(ordinal["from_address"]),
                Recipients = reader.GetString(ordinal["recipients"]),
                ReceivedUtc = DateTimeOffset.Parse(reader.GetString(ordinal["received_utc"]), CultureInfo.InvariantCulture),
                TimeLabel = reader.GetString(ordinal["time_label"]),
                IsUnread = reader.GetInt32(ordinal["is_unread"]) == 1,
                IsFlagged = reader.GetInt32(ordinal["is_flagged"]) == 1,
                HasAttachments = reader.GetInt32(ordinal["has_attachments"]) == 1,
                BodyDownloaded = reader.GetInt32(ordinal["body_downloaded"]) == 1,
                LocalOnly = reader.GetInt32(ordinal["local_only"]) == 1,
                Availability = Enum.Parse<ItemAvailability>(reader.GetString(ordinal["availability"])),
                PopUidl = reader.IsDBNull(ordinal["pop_uidl"]) ? null : reader.GetString(ordinal["pop_uidl"]),
                Preview = reader.GetString(ordinal["preview"]),
                AccountName = reader.GetString(ordinal["account_name"]),
                SendState = reader.IsDBNull(ordinal["send_state"]) ? null : reader.GetString(ordinal["send_state"]),
            });
        }

        return list;
    }

    private static Dictionary<string, int> ColumnMap(SqliteDataReader reader)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            map[reader.GetName(i)] = i;
        return map;
    }

    private static async Task<List<StoredAttachment>> ReadAttachmentsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<StoredAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new StoredAttachment
            {
                Id = reader.GetString(0),
                AccountId = reader.GetString(1),
                FolderId = reader.GetString(2),
                Uid = reader.GetInt64(3),
                FileName = reader.GetString(4),
                ContentType = reader.GetString(5),
                Size = reader.GetInt64(6),
                CacheRelativePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                Downloaded = reader.GetInt32(8) == 1,
                Pinned = reader.GetInt32(9) == 1,
                ContentId = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return list;
    }

    private static async Task ClearFolderAsync(SqliteConnection connection, SqliteTransaction tx, string accountId, string folderId, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM search_keys WHERE account_id = $a AND folder_id = $f)",
            "DELETE FROM search_keys WHERE account_id = $a AND folder_id = $f",
            "DELETE FROM bodies WHERE account_id = $a AND folder_id = $f",
            "DELETE FROM attachments WHERE account_id = $a AND folder_id = $f",
            "DELETE FROM messages WHERE account_id = $a AND folder_id = $f",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$a", accountId);
            command.Parameters.AddWithValue("$f", folderId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteMessageAsync(SqliteConnection connection, SqliteTransaction tx, MessageKey key, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "DELETE FROM messages_fts WHERE rowid = (SELECT id FROM search_keys WHERE account_id = $a AND folder_id = $f AND uid = $u)",
            "DELETE FROM search_keys WHERE account_id = $a AND folder_id = $f AND uid = $u",
            "DELETE FROM bodies WHERE account_id = $a AND folder_id = $f AND uid = $u",
            "DELETE FROM attachments WHERE account_id = $a AND folder_id = $f AND uid = $u",
            "DELETE FROM messages WHERE account_id = $a AND folder_id = $f AND uid = $u",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            BindKey(command, key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertMessageAsync(SqliteConnection connection, SqliteTransaction tx, StoredMessage message, HashSet<MessageKey> bodiesInBatch, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO messages(account_id, folder_id, uid, message_id, subject, from_name, from_address, recipients, received_utc, time_label, is_unread, is_flagged, has_attachments, body_downloaded, local_only, availability, pop_uidl, preview, account_name, send_state)
            VALUES ($a,$f,$u,$mid,$subject,$from,$addr,$to,$received,$label,$unread,$flag,$attach,$body,$local,$avail,$uidl,$preview,$name,$send)
            ON CONFLICT(account_id, folder_id, uid) DO UPDATE SET
              message_id = excluded.message_id, subject = excluded.subject, from_name = excluded.from_name, from_address = excluded.from_address,
              recipients = excluded.recipients, received_utc = excluded.received_utc, time_label = excluded.time_label, is_unread = excluded.is_unread,
              is_flagged = excluded.is_flagged, has_attachments = excluded.has_attachments, body_downloaded = excluded.body_downloaded,
              local_only = excluded.local_only, availability = excluded.availability, pop_uidl = excluded.pop_uidl, preview = excluded.preview,
              account_name = excluded.account_name, send_state = excluded.send_state
            """;
        BindMessage(command, message);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var key = new MessageKey(message.AccountId, message.FolderId, message.Uid);
        if (bodiesInBatch.Contains(key))
            return;

        // A metadata-only sync can refresh flags without downloading the body again.
        await using var existingBody = connection.CreateCommand();
        existingBody.Transaction = tx;
        existingBody.CommandText = "SELECT text_body FROM bodies WHERE account_id = $a AND folder_id = $f AND uid = $u";
        BindKey(existingBody, key);
        var indexedBody = await existingBody.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        await ReplaceFtsAsync(connection, tx, message, indexedBody ?? message.Preview, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertBodyAsync(SqliteConnection connection, SqliteTransaction tx, MessageKey key, string text, string? html, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO bodies(account_id, folder_id, uid, text_body, html_body) VALUES ($a,$f,$u,$text,$html)
            ON CONFLICT(account_id, folder_id, uid) DO UPDATE SET text_body = excluded.text_body, html_body = excluded.html_body
            """;
        BindKey(command, key);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$html", (object?)html ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE messages SET body_downloaded = 1, preview = $text, availability = 'Downloaded' WHERE account_id = $a AND folder_id = $f AND uid = $u";
        BindKey(update, key);
        update.Parameters.AddWithValue("$text", text.Length > 280 ? text[..280] : text);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT subject, from_name, from_address FROM messages WHERE account_id = $a AND folder_id = $f AND uid = $u";
        BindKey(read, key);
            StoredMessage? message = null;
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    message = new StoredMessage
                    {
                        AccountId = key.AccountId,
                        FolderId = key.FolderId,
                        Uid = key.Uid,
                        Subject = reader.GetString(0),
                        FromName = reader.GetString(1),
                        FromAddress = reader.GetString(2),
                    };
                }
            }

            if (message is not null)
                await ReplaceFtsAsync(connection, tx, message, text, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceFtsAsync(SqliteConnection connection, SqliteTransaction tx, StoredMessage message, string body, CancellationToken cancellationToken)
    {
        await using var key = connection.CreateCommand();
        key.Transaction = tx;
        key.CommandText = """
            INSERT OR IGNORE INTO search_keys(account_id, folder_id, uid) VALUES ($a, $f, $u);
            SELECT id FROM search_keys WHERE account_id = $a AND folder_id = $f AND uid = $u;
            """;
        BindKey(key, new MessageKey(message.AccountId, message.FolderId, message.Uid));
        var rowId = (long)(await key.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The search key was not created."));
        await using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM messages_fts WHERE rowid = $row";
        delete.Parameters.AddWithValue("$row", rowId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO messages_fts(rowid, subject, from_name, from_address, body, account_id, folder_id, uid)
            VALUES ($row,$subject,$from,$addr,$body,$a,$f,$u)
            """;
        insert.Parameters.AddWithValue("$subject", message.Subject);
        insert.Parameters.AddWithValue("$from", message.FromName);
        insert.Parameters.AddWithValue("$addr", message.FromAddress);
        insert.Parameters.AddWithValue("$body", body);
        insert.Parameters.AddWithValue("$row", rowId);
        BindKey(insert, new MessageKey(message.AccountId, message.FolderId, message.Uid));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAttachmentAsync(SqliteConnection connection, SqliteTransaction tx, StoredAttachment attachment, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO attachments(id, account_id, folder_id, uid, file_name, content_type, size, cache_path, downloaded, pinned, content_id)
            VALUES ($id,$a,$f,$u,$name,$type,$size,$path,$down,$pin,$cid)
            ON CONFLICT(id) DO UPDATE SET file_name = excluded.file_name, size = excluded.size, cache_path = excluded.cache_path,
              downloaded = excluded.downloaded, pinned = excluded.pinned, content_id = excluded.content_id
            """;
        command.Parameters.AddWithValue("$id", attachment.Id);
        command.Parameters.AddWithValue("$name", attachment.FileName);
        command.Parameters.AddWithValue("$type", attachment.ContentType);
        command.Parameters.AddWithValue("$size", attachment.Size);
        command.Parameters.AddWithValue("$path", (object?)attachment.CacheRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$down", attachment.Downloaded ? 1 : 0);
        command.Parameters.AddWithValue("$pin", attachment.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$cid", (object?)attachment.ContentId ?? DBNull.Value);
        BindKey(command, new MessageKey(attachment.AccountId, attachment.FolderId, attachment.Uid));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertOperationAsync(SqliteConnection connection, SqliteTransaction tx, QueuedOperation operation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO operations(id, account_id, kind, payload_json, state, message_id, created_utc, updated_utc, attempt, last_error)
            VALUES ($id,$a,$kind,$payload,$state,$mid,$created,$updated,$attempt,$error)
            ON CONFLICT(id) DO UPDATE SET state = excluded.state, message_id = excluded.message_id, updated_utc = excluded.updated_utc,
              attempt = excluded.attempt, last_error = excluded.last_error, payload_json = excluded.payload_json
            """;
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$a", operation.AccountId);
        command.Parameters.AddWithValue("$kind", operation.Kind);
        command.Parameters.AddWithValue("$payload", operation.PayloadJson);
        command.Parameters.AddWithValue("$state", operation.State.ToString());
        command.Parameters.AddWithValue("$mid", (object?)operation.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", operation.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", operation.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$attempt", operation.Attempt);
        command.Parameters.AddWithValue("$error", (object?)operation.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetCheckpointAsync(SqliteConnection connection, SqliteTransaction tx, string accountId, string scope, string token, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO checkpoints(account_id, scope, token, updated_utc) VALUES ($a,$s,$t,$u)
            ON CONFLICT(account_id, scope) DO UPDATE SET token = excluded.token, updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$a", accountId);
        command.Parameters.AddWithValue("$s", scope);
        command.Parameters.AddWithValue("$t", token);
        command.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindKey(SqliteCommand command, MessageKey key)
    {
        command.Parameters.AddWithValue("$a", key.AccountId);
        command.Parameters.AddWithValue("$f", key.FolderId);
        command.Parameters.AddWithValue("$u", key.Uid);
    }

    private static void BindMessage(SqliteCommand command, StoredMessage message)
    {
        BindKey(command, new MessageKey(message.AccountId, message.FolderId, message.Uid));
        command.Parameters.AddWithValue("$mid", (object?)message.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", message.Subject);
        command.Parameters.AddWithValue("$from", message.FromName);
        command.Parameters.AddWithValue("$addr", message.FromAddress);
        command.Parameters.AddWithValue("$to", message.Recipients);
        command.Parameters.AddWithValue("$received", message.ReceivedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$label", message.TimeLabel);
        command.Parameters.AddWithValue("$unread", message.IsUnread ? 1 : 0);
        command.Parameters.AddWithValue("$flag", message.IsFlagged ? 1 : 0);
        command.Parameters.AddWithValue("$attach", message.HasAttachments ? 1 : 0);
        command.Parameters.AddWithValue("$body", message.BodyDownloaded ? 1 : 0);
        command.Parameters.AddWithValue("$local", message.LocalOnly ? 1 : 0);
        command.Parameters.AddWithValue("$avail", message.Availability.ToString());
        command.Parameters.AddWithValue("$uidl", (object?)message.PopUidl ?? DBNull.Value);
        command.Parameters.AddWithValue("$preview", message.Preview);
        command.Parameters.AddWithValue("$name", message.AccountName);
        command.Parameters.AddWithValue("$send", (object?)message.SendState ?? DBNull.Value);
    }

    private SqliteConnection Require() => _connection ?? throw new InvalidOperationException("The personal store is not open.");

    private static async Task MigrateSearchKeysAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "PRAGMA user_version";
        if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 0)
        {
            command.CommandText = """
                DELETE FROM messages_fts;
                INSERT INTO search_keys(account_id, folder_id, uid) SELECT account_id, folder_id, uid FROM messages;
                INSERT INTO messages_fts(rowid, subject, from_name, from_address, body, account_id, folder_id, uid)
                SELECT k.id, m.subject, m.from_name, m.from_address, coalesce(b.text_body, m.preview), m.account_id, m.folder_id, m.uid
                FROM messages m
                JOIN search_keys k USING (account_id, folder_id, uid)
                LEFT JOIN bodies b USING (account_id, folder_id, uid);
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS messages (
          account_id TEXT NOT NULL,
          folder_id TEXT NOT NULL,
          uid INTEGER NOT NULL,
          message_id TEXT,
          subject TEXT NOT NULL DEFAULT '',
          from_name TEXT NOT NULL DEFAULT '',
          from_address TEXT NOT NULL DEFAULT '',
          recipients TEXT NOT NULL DEFAULT '',
          received_utc TEXT NOT NULL,
          time_label TEXT NOT NULL DEFAULT '',
          is_unread INTEGER NOT NULL DEFAULT 1,
          is_flagged INTEGER NOT NULL DEFAULT 0,
          has_attachments INTEGER NOT NULL DEFAULT 0,
          body_downloaded INTEGER NOT NULL DEFAULT 0,
          local_only INTEGER NOT NULL DEFAULT 0,
          availability TEXT NOT NULL DEFAULT 'Downloaded',
          pop_uidl TEXT,
          preview TEXT NOT NULL DEFAULT '',
          account_name TEXT NOT NULL DEFAULT '',
          send_state TEXT,
          PRIMARY KEY (account_id, folder_id, uid)
        );
        CREATE INDEX IF NOT EXISTS ix_messages_uidl ON messages(account_id, pop_uidl);
        CREATE TABLE IF NOT EXISTS bodies (
          account_id TEXT NOT NULL,
          folder_id TEXT NOT NULL,
          uid INTEGER NOT NULL,
          text_body TEXT NOT NULL DEFAULT '',
          html_body TEXT,
          PRIMARY KEY (account_id, folder_id, uid)
        );
        CREATE TABLE IF NOT EXISTS attachments (
          id TEXT PRIMARY KEY,
          account_id TEXT NOT NULL,
          folder_id TEXT NOT NULL,
          uid INTEGER NOT NULL,
          file_name TEXT NOT NULL,
          content_type TEXT NOT NULL,
          size INTEGER NOT NULL,
          cache_path TEXT,
          downloaded INTEGER NOT NULL DEFAULT 0,
          pinned INTEGER NOT NULL DEFAULT 0,
          content_id TEXT
        );
        CREATE TABLE IF NOT EXISTS operations (
          id TEXT PRIMARY KEY,
          account_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          state TEXT NOT NULL,
          message_id TEXT,
          created_utc TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          attempt INTEGER NOT NULL DEFAULT 0,
          last_error TEXT
        );
        CREATE TABLE IF NOT EXISTS checkpoints (
          account_id TEXT NOT NULL,
          scope TEXT NOT NULL,
          token TEXT NOT NULL,
          updated_utc TEXT NOT NULL,
          PRIMARY KEY (account_id, scope)
        );
        CREATE TABLE IF NOT EXISTS organizer (
          account_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          remote_id TEXT NOT NULL,
          revision TEXT,
          json TEXT NOT NULL,
          pending INTEGER NOT NULL DEFAULT 0,
          deleted INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (account_id, kind, remote_id)
        );
        CREATE TABLE IF NOT EXISTS search_keys (
          id INTEGER PRIMARY KEY,
          account_id TEXT NOT NULL,
          folder_id TEXT NOT NULL,
          uid INTEGER NOT NULL,
          UNIQUE(account_id, folder_id, uid)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
          subject, from_name, from_address, body, account_id UNINDEXED, folder_id UNINDEXED, uid UNINDEXED, tokenize = 'unicode61'
        );
        """;
}
