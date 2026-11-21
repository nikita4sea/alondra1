using System.Diagnostics;
using System.Text;
using Alondra1.Core.Mail;
using Alondra1.Persistence;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Alondra1.Tests;

public sealed class MailboxScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Refreshing_message_flags_keeps_the_body_searchable()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "personal.db");
        try
        {
            await using var store = new SqlitePersonalStore(path);
            await store.InitializeAsync(default);
            var key = new MessageKey("account", "Inbox", 1);
            var message = new StoredMessage { AccountId = key.AccountId, FolderId = key.FolderId, Uid = key.Uid };
            var original = new PersonalBatch();
            original.Upserts.Add(message);
            original.Bodies.Add((key, "uniquebodytoken", null));
            await store.CommitAsync(original, default);
            message.IsUnread = true;
            var refresh = new PersonalBatch();
            refresh.Upserts.Add(message);
            await store.CommitAsync(refresh, default);
            Assert.True(Assert.Single(await store.SearchAsync(key.AccountId, key.FolderId, "uniquebodytoken", default)).IsUnread);
        }
        finally
        {
            RemoveFixture(root, path);
        }
    }

    [Fact]
    public async Task Legacy_search_index_is_rebuilt_on_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "alondra1-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "personal.db");
        try
        {
            var key = new MessageKey("account", "Inbox", 7);
            await using (var store = new SqlitePersonalStore(path))
            {
                await store.InitializeAsync(default);
                var batch = new PersonalBatch();
                batch.Upserts.Add(new StoredMessage { AccountId = key.AccountId, FolderId = key.FolderId, Uid = key.Uid });
                batch.Bodies.Add((key, "legacybodytoken", null));
                await store.CommitAsync(batch, default);
            }
            using (var legacy = OpenFixtureConnection(path))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = """
                    DROP TABLE search_keys;
                    UPDATE messages_fts SET rowid = 99, body = 'oldpreview';
                    PRAGMA user_version = 0;
                    """;
                command.ExecuteNonQuery();
            }

            await using var reopened = new SqlitePersonalStore(path);
            await reopened.InitializeAsync(default);
            Assert.Equal(7L, Assert.Single(await reopened.SearchAsync(key.AccountId, key.FolderId, "legacybodytoken", default)).Uid);
            var delete = new PersonalBatch();
            delete.Deletes.Add(key);
            await reopened.CommitAsync(delete, default);
            Assert.Empty(await reopened.SearchAsync(key.AccountId, key.FolderId, "legacybodytoken", default));
        }
        finally
        {
            RemoveFixture(root, path);
        }
    }

    [MailboxScaleFact]
    public async Task Ten_gigabyte_personal_mailbox()
    {
        var parent = Environment.GetEnvironmentVariable("ALONDRA1_SCALE_TEST_ROOT");
        var root = Path.Combine(Path.GetFullPath(parent!), "alondra1-scale-" + Guid.NewGuid().ToString("N"));
        var available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        Assert.True(available >= 12_000_000_000L, $"Need 12 GB of free space, found {available:N0} bytes.");
        Directory.CreateDirectory(root);
        try
        {
            await RunAsync(root);
        }
        finally
        {
            RemoveFixture(root, Path.Combine(root, "personal.db"));
        }
    }

    private static void RemoveFixture(string root, string database)
    {
        using var connection = OpenFixtureConnection(database);
        SqliteConnection.ClearPool(connection);
        Directory.Delete(root, recursive: true);
    }

    private static SqliteConnection OpenFixtureConnection(string database) => new(new SqliteConnectionStringBuilder
    {
        DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString());

    private async Task RunAsync(string root)
    {
        const long target = 10_000_000_000L;
        const int largeMessage = 20_000_000;
        const string account = "scale";
        const string folder = "Inbox";
        const string marker = "lighthookfind";
        var path = Path.Combine(root, "personal.db");
        var files = new AttachmentFiles(Path.Combine(root, "attachments"));
        var random = new Random(82);
        var buffer = new byte[largeMessage];
        var text = string.Concat(Enumerable.Repeat("Weekly review and budget summary for the support team. ", 100));
        var bytes = 0L;
        var uid = 0L;
        var largeCount = 0;
        long largeUid = 0;
        string? largePath = null;
        var clock = Stopwatch.StartNew();
        await using (var store = new SqlitePersonalStore(path))
        {
            await store.InitializeAsync(default);
            while (bytes < target)
            {
                var batch = new PersonalBatch();
                while (batch.Upserts.Count < 150 && bytes < target)
                {
                    uid++;
                    var size = uid % 1500 == 0 ? largeMessage : random.Next(300_000, 1_000_001);
                    var body = uid == 1500 ? text + marker : text;
                    var attachmentSize = size - Encoding.UTF8.GetByteCount(body);
                    var (relative, full) = files.Reserve(account, "fixture.bin");
                    random.NextBytes(buffer.AsSpan(0, attachmentSize));
                    await using (var stream = File.Create(full))
                        await stream.WriteAsync(buffer.AsMemory(0, attachmentSize));
                    if (size == largeMessage)
                    {
                        largeCount++;
                        largeUid = uid;
                        largePath = full;
                    }
                    var key = new MessageKey(account, folder, uid);
                    batch.Upserts.Add(new StoredMessage
                    {
                        AccountId = account, AccountName = "Scale test", FolderId = folder, Uid = uid,
                        Subject = $"Weekly report {uid}", FromName = "Fixture sender", FromAddress = "fixture@example.test",
                        HasAttachments = true, BodyDownloaded = true, ReceivedUtc = DateTimeOffset.UnixEpoch.AddMinutes(uid),
                    });
                    batch.Bodies.Add((key, body, null));
                    batch.Attachments.Add(new StoredAttachment
                    {
                        Id = uid.ToString(), AccountId = account, FolderId = folder, Uid = uid,
                        FileName = "fixture.bin", Size = attachmentSize, CacheRelativePath = relative, Downloaded = true,
                    });
                    bytes += size;
                }
                await store.CommitAsync(batch, default);
            }
        }
        output.WriteLine($"Seed and index: {clock.Elapsed.TotalSeconds:F2} s; {uid:N0} messages; {bytes:N0} payload bytes; {largeCount} messages at 20 MB.");
        Assert.True(bytes >= target);
        Assert.True(largeCount > 1);
        Assert.InRange(new FileInfo(largePath!).Length, largeMessage - 10_000L, largeMessage);
        await using var reopened = new SqlitePersonalStore(path);
        clock.Restart();
        await reopened.InitializeAsync(default);
        var list = await reopened.ListAsync(account, folder, default);
        output.WriteLine($"Reopen and list: {clock.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(uid, (long)list.Count);
        var bodyResult = await reopened.GetBodyAsync(new MessageKey(account, folder, 1500), default);
        Assert.Contains(marker, bodyResult?.Text ?? "");
        var attachment = Assert.Single(await reopened.ListAttachmentsAsync(new MessageKey(account, folder, largeUid), default));
        clock.Restart();
        await using (var stream = File.OpenRead(files.Resolve(attachment.CacheRelativePath!)))
            await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)attachment.Size));
        output.WriteLine($"Read cached 20 MB message attachment: {clock.Elapsed.TotalMilliseconds:F0} ms.");
        await MeasureSearch(marker, 1, 500);
        await MeasureSearch("budget summary", list.Count, 2000);
        await MeasureSearch("absenttoken", 0, 500);

        async Task MeasureSearch(string query, int hits, double budgetMs)
        {
            var elapsed = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var timer = Stopwatch.StartNew();
                Assert.Equal(hits, (await reopened.SearchAsync(account, folder, query, default)).Count);
                elapsed.Add(timer.Elapsed.TotalMilliseconds);
            }
            elapsed.Sort();
            var p95 = elapsed[18];
            output.WriteLine($"Search '{query}': {hits:N0} hits, p95 {p95:F1} ms, max {elapsed[^1]:F1} ms.");
            Assert.True(p95 <= budgetMs, $"Search '{query}' p95 {p95:F1} ms exceeded {budgetMs} ms.");
        }
    }
}

public sealed class MailboxScaleFactAttribute : FactAttribute
{
    public MailboxScaleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALONDRA1_SCALE_TEST_ROOT")))
            Skip = "Set ALONDRA1_SCALE_TEST_ROOT to a scratch directory to run the 10 GB test.";
    }
}
