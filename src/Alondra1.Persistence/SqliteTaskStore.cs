using System.Globalization;
using System.Text.Json;
using Alondra1.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Alondra1.Persistence;

/// <summary>
/// Local to-do storage in its own SQLite file (todo.db) next to the Personal mail database. Entities are stored as
/// versioned JSON documents with indexed key columns. Every change set is one transaction.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore, IAsyncDisposable
{
    /// <summary>Schema version written by this build. Opening a newer file is refused without changing it.</summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = TodoTransfer.CreateOptions();
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteTaskStore(string databasePath) => _path = databasePath;

    public string DatabasePath => _path;

    /// <summary>Path of the copy made before the last migration, if one was needed.</summary>
    public string? LastMigrationBackup { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default, int targetVersion = SchemaVersion)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existed = File.Exists(_path) && new FileInfo(_path).Length > 0;
            var connection = Open(_path, SqliteOpenMode.ReadWriteCreate);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await UserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new TodoStorageException($"The to-do database was written by a newer Alondra1 (schema {version}). This version supports {SchemaVersion}. The file was not changed.");
            }

            if (existed && version > 0 && version < targetVersion)
            {
                // Protect against data loss during upgrades: keep a consistent copy of the old file.
                LastMigrationBackup = _path + $".pre-v{targetVersion}.bak";
                await using (var backup = Open(LastMigrationBackup, SqliteOpenMode.ReadWriteCreate))
                {
                    await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                    connection.BackupDatabase(backup);
                }
            }

            await MigrateAsync(connection, version, targetVersion, cancellationToken).ConfigureAwait(false);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TodoData> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            var data = new TodoData
            {
                Groups = await ReadAllAsync<ListGroup>(connection, "SELECT json FROM list_groups", cancellationToken).ConfigureAwait(false),
                Lists = await ReadAllAsync<TaskList>(connection, "SELECT json FROM task_lists", cancellationToken).ConfigureAwait(false),
                Tasks = await ReadAllAsync<TodoTask>(connection, "SELECT json FROM tasks", cancellationToken).ConfigureAwait(false),
                History = await ReadAllAsync<CompletionRecord>(connection, "SELECT json FROM completion_history", cancellationToken).ConfigureAwait(false),
            };
            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(TodoChangeSet changes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAllAsync(TodoData data, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Require();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM tasks; DELETE FROM task_lists; DELETE FROM list_groups; DELETE FROM completion_history;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var changes = new TodoChangeSet();
            changes.UpsertGroups.AddRange(data.Groups);
            changes.UpsertLists.AddRange(data.Lists);
            changes.UpsertTasks.AddRange(data.Tasks);
            changes.UpsertHistory.AddRange(data.History);
            await WriteAsync(connection, tx, changes, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TaskPreferences> LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT json FROM preferences WHERE key = 'todo'";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json)
            {
                try
                {
                    return JsonSerializer.Deserialize<TaskPreferences>(json, Json) ?? new TaskPreferences();
                }
                catch (JsonException)
                {
                    return new TaskPreferences();
                }
            }

            return new TaskPreferences();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePreferencesAsync(TaskPreferences preferences, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "INSERT INTO preferences(key, json) VALUES ('todo', $json) ON CONFLICT(key) DO UPDATE SET json = excluded.json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preferences, Json));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes a consistent copy of the database using SQLite's online backup.</summary>
    public async Task BackupToAsync(string destination, CancellationToken cancellationToken = default)
    {
        var temporary = destination + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            var target = Open(temporary, SqliteOpenMode.ReadWriteCreate);
            await using (target.ConfigureAwait(false))
            {
                await target.OpenAsync(cancellationToken).ConfigureAwait(false);
                Require().BackupDatabase(target);
            }
        }
        finally
        {
            _gate.Release();
        }

        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>
    /// Reads a backup file without changing it: the file is copied to a temporary location, checked, and migrated there.
    /// </summary>
    public static async Task<TodoData> ReadBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
            throw new TodoStorageException("The backup file does not exist.");
        var header = new byte[16];
        await using (var stream = File.OpenRead(backupPath))
        {
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) < 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                throw new TodoStorageException("The file is not a Alondra1 to-do backup.");
        }

        var scratch = Path.Combine(Path.GetTempPath(), "alondra1-restore-" + Guid.NewGuid().ToString("n") + ".db");
        File.Copy(backupPath, scratch);
        try
        {
            var probe = Open(scratch, SqliteOpenMode.ReadWrite);
            await using (probe.ConfigureAwait(false))
            {
                await probe.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var check = probe.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                var result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new TodoStorageException("The backup file is damaged: " + result);
                check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('tasks', 'task_lists')";
                if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 2)
                    throw new TodoStorageException("The file is not a Alondra1 to-do backup.");
            }

            var store = new SqliteTaskStore(scratch);
            await using (store.ConfigureAwait(false))
            {
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(scratch);
            TryDelete(scratch + ".pre-v" + SchemaVersion + ".bak");
        }
    }

    public async Task<long> CountTasksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Require().CreateCommand();
            command.CommandText = "SELECT count(*) FROM tasks";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            var connection = _connection;
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------- Schema and migrations ----------

    /// <summary>Ordered migrations. Each runs in its own transaction and sets user_version when it succeeds.</summary>
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS list_groups (
              id TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS task_lists (
              id TEXT PRIMARY KEY,
              group_id TEXT,
              json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks (
              id TEXT PRIMARY KEY,
              list_id TEXT NOT NULL,
              json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_list ON tasks(list_id);
            """),
        (2, """
            ALTER TABLE tasks ADD COLUMN due_date TEXT;
            ALTER TABLE tasks ADD COLUMN completed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE tasks ADD COLUMN modified_utc TEXT NOT NULL DEFAULT '';
            UPDATE tasks SET
              due_date = json_extract(json, '$.dueDate'),
              completed = coalesce(json_extract(json, '$.completed'), 0),
              modified_utc = coalesce(json_extract(json, '$.modifiedUtc'), '');
            CREATE INDEX IF NOT EXISTS ix_tasks_due ON tasks(due_date) WHERE completed = 0;
            CREATE TABLE IF NOT EXISTS completion_history (
              id TEXT PRIMARY KEY,
              task_id TEXT NOT NULL,
              completed_utc TEXT NOT NULL,
              json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_completed ON completion_history(completed_utc);
            CREATE TABLE IF NOT EXISTS preferences (
              key TEXT PRIMARY KEY,
              json TEXT NOT NULL
            );
            """),
    ];

    private static async Task MigrateAsync(SqliteConnection connection, long from, int to, CancellationToken cancellationToken)
    {
        foreach (var (version, sql) in Migrations)
        {
            if (version <= from || version > to)
                continue;
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql + $"\nPRAGMA user_version = {version};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> UserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    // ---------- Reads and writes ----------

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction tx, TodoChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var id in changes.DeleteTasks)
            await ExecAsync(connection, tx, "DELETE FROM tasks WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteLists)
            await ExecAsync(connection, tx, "DELETE FROM task_lists WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteGroups)
            await ExecAsync(connection, tx, "DELETE FROM list_groups WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var id in changes.DeleteHistory)
            await ExecAsync(connection, tx, "DELETE FROM completion_history WHERE id = $id", cancellationToken, ("$id", id)).ConfigureAwait(false);
        foreach (var group in changes.UpsertGroups)
        {
            await ExecAsync(connection, tx, "INSERT INTO list_groups(id, json) VALUES ($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                cancellationToken, ("$id", group.Id), ("$json", JsonSerializer.Serialize(group, Json))).ConfigureAwait(false);
        }

        foreach (var list in changes.UpsertLists)
        {
            await ExecAsync(connection, tx, "INSERT INTO task_lists(id, group_id, json) VALUES ($id, $group, $json) ON CONFLICT(id) DO UPDATE SET group_id = excluded.group_id, json = excluded.json",
                cancellationToken, ("$id", list.Id), ("$group", list.GroupId), ("$json", JsonSerializer.Serialize(list, Json))).ConfigureAwait(false);
        }

        foreach (var task in changes.UpsertTasks)
        {
            await ExecAsync(connection, tx, """
                INSERT INTO tasks(id, list_id, json, due_date, completed, modified_utc) VALUES ($id, $list, $json, $due, $done, $modified)
                ON CONFLICT(id) DO UPDATE SET list_id = excluded.list_id, json = excluded.json, due_date = excluded.due_date,
                  completed = excluded.completed, modified_utc = excluded.modified_utc
                """,
                cancellationToken,
                ("$id", task.Id), ("$list", task.ListId), ("$json", JsonSerializer.Serialize(task, Json)),
                ("$due", task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$done", task.Completed ? 1 : 0),
                ("$modified", task.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        foreach (var record in changes.UpsertHistory)
        {
            await ExecAsync(connection, tx, "INSERT INTO completion_history(id, task_id, completed_utc, json) VALUES ($id, $task, $at, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
                cancellationToken, ("$id", record.Id), ("$task", record.TaskId), ("$at", record.CompletedUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", JsonSerializer.Serialize(record, Json))).ConfigureAwait(false);
        }
    }

    private static async Task ExecAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<T>> ReadAllAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var list = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            try
            {
                if (JsonSerializer.Deserialize<T>(json, Json) is { } item)
                    list.Add(item);
            }
            catch (JsonException)
            {
                // A damaged row is skipped rather than blocking every other task from loading.
            }
        }

        return list;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private SqliteConnection Require() => _connection ?? throw new TodoStorageException("The to-do store is not open.");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
