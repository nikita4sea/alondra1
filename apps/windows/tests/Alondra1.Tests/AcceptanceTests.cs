using Alondra1.Core.Config;
using Alondra1.Core.Html;
using Alondra1.Core.Layout;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Security;
using Alondra1.Core.Sync;
using Alondra1.Persistence;

namespace Alondra1.Tests;

public class AcceptanceTests
{
    [Fact]
    public void Layout_covers_every_dock_combination_without_overlap()
    {
        var edges = Enum.GetValues<DockEdge>();
        Assert.Equal(25, edges.Length * edges.Length);
        foreach (var folders in edges)
        {
            foreach (var reading in edges)
            {
                var frames = LayoutArranger.Arrange(folders, reading, 1200, 800, 152, 330);
                Assert.True(frames.Inbox.Width >= 0 && frames.Inbox.Height >= 0);
                Assert.False(frames.Inbox.Overlaps(frames.Folders));
                Assert.False(frames.Inbox.Overlaps(frames.Reader));
                Assert.False(frames.Folders.Overlaps(frames.Reader));
                if (folders == DockEdge.Hidden)
                    Assert.False(frames.Folders.HasArea);
                if (reading == DockEdge.Hidden)
                    Assert.False(frames.Reader.HasArea);
                Assert.True(frames.Inbox.HasArea);
            }
        }
    }

    [Fact]
    public void Layout_default_matches_the_reference_order()
    {
        var tree = LayoutArranger.Describe(LayoutArranger.Compose(DockEdge.Top, DockEdge.Right));
        Assert.Equal("V(folders|H(inbox|reader))", tree);
        var frames = LayoutArranger.Arrange(DockEdge.Top, DockEdge.Right, 1000, 700, 52, 330);
        Assert.True(frames.Folders.Y < frames.Inbox.Y);
        Assert.True(frames.Inbox.X < frames.Reader.X);
        Assert.Equal(frames.Inbox.Y, frames.Reader.Y);
    }

    [Fact]
    public void Layout_same_edge_keeps_the_reference_order()
    {
        Assert.Equal("H(folders|H(reader|inbox))", LayoutArranger.Describe(LayoutArranger.Compose(DockEdge.Left, DockEdge.Left)));
        Assert.Equal("H(H(inbox|reader)|folders)", LayoutArranger.Describe(LayoutArranger.Compose(DockEdge.Right, DockEdge.Right)));
        Assert.Equal("V(folders|V(reader|inbox))", LayoutArranger.Describe(LayoutArranger.Compose(DockEdge.Top, DockEdge.Top)));
    }

    [Fact]
    public void Config_rejects_a_newer_file_without_rewriting_it()
    {
        var directory = Temp();
        var store = new ConfigFileStore(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), """{"schemaVersion":99,"accounts":[]}""");
        var before = File.ReadAllText(Path.Combine(directory, "config.json"));
        var error = Assert.Throws<ConfigFormatException>(() => store.Load());
        Assert.Contains("was not changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "config.json")));
    }

    [Fact]
    public void Config_recovers_the_previous_file_after_a_damaged_write()
    {
        var directory = Temp();
        var store = new ConfigFileStore(directory);
        var good = Account();
        good.DisplayName = "Kept";
        store.Save(new AppConfig { Accounts = [good] });
        store.Save(new AppConfig { Accounts = [good] });
        File.WriteAllText(Path.Combine(directory, "config.json"), "{ this is not json");
        var loaded = store.Load();
        Assert.Equal("Kept", loaded.Accounts[0].DisplayName);
    }

    [Fact]
    public void Import_keeps_the_existing_mode_and_asks_again_for_plaintext()
    {
        var current = new AppConfig { Accounts = [Account(AccountMode.Personal)] };
        var incomingAccount = Account(AccountMode.Online);
        incomingAccount.Id = "other-id";
        incomingAccount.Incoming.Security = TransportSecurity.None;
        incomingAccount.Incoming.Port = 143; // plaintext IMAP port (993 is Implicit TLS only)
        incomingAccount.PlaintextConfirmed = true;
        var plan = ConfigWorkflow.Preview(current, new AppConfig { Accounts = [incomingAccount] });
        Assert.True(plan.Ok);
        Assert.NotNull(plan.Merged);
        Assert.Equal(AccountMode.Personal, plan.Merged!.Accounts[0].Mode);
        Assert.Equal(current.Accounts[0].Id, plan.Merged.Accounts[0].Id);
        Assert.False(plan.Merged.Accounts[0].PlaintextConfirmed);
        Assert.Contains(plan.Warnings, warning => warning.Contains("mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_of_a_broken_file_does_not_produce_a_replacement()
    {
        var plan = ConfigWorkflow.Preview(new AppConfig(), new AppConfig { SchemaVersion = 40 });
        Assert.False(plan.Ok);
        Assert.Null(plan.Merged);
        Assert.Null(plan.Replaced);
    }

    [Fact]
    public void Export_contains_public_client_ids_and_no_secrets()
    {
        var config = new AppConfig
        {
            OAuth = new OAuthClientSettings { MicrosoftClientId = "11111111-1111-1111-1111-111111111111", GoogleClientId = "app.apps.googleusercontent.com" },
            Accounts = [Account()],
        };
        var json = ConfigWorkflow.ExportJson(config);
        Assert.Contains("11111111-1111-1111-1111-111111111111", json, StringComparison.Ordinal);
        Assert.Empty(ConfigWorkflow.FindSecretMaterial(json));
    }

    [Fact]
    public void Plaintext_and_oauth_cannot_connect()
    {
        var account = Account();
        account.Incoming.Security = TransportSecurity.None;
        account.Incoming.Port = 143; // plaintext IMAP port (993 is Implicit TLS only)
        account.Incoming.Authentication = AuthenticationKind.OAuth;
        account.PlaintextConfirmed = true;
        var decision = ConnectionPolicy.Evaluate(account, new SecretBundle { Exchange = new OAuthToken { AccessToken = "token" } });
        Assert.False(decision.Allowed);
        account.Incoming.Authentication = AuthenticationKind.Password;
        account.Outgoing.Authentication = AuthenticationKind.Password;
        account.PlaintextConfirmed = false;
        Assert.False(ConnectionPolicy.Evaluate(account, new SecretBundle { Password = "secret" }).Allowed);
        account.PlaintextConfirmed = true;
        Assert.True(ConnectionPolicy.Evaluate(account, new SecretBundle { Password = "secret" }).Allowed);
    }

    [Fact]
    public void Smtp_disconnect_after_send_starts_is_uncertain_and_keeps_the_message_id()
    {
        Assert.Equal(SmtpAcceptance.Uncertain, SmtpStateMachine.Classify(sendStarted: true, serverRejected: false, new IOException("dropped")));
        Assert.Equal(SmtpAcceptance.Failed, SmtpStateMachine.Classify(sendStarted: true, serverRejected: true, new IOException("rejected")));
        Assert.Equal(SmtpAcceptance.Failed, SmtpStateMachine.Classify(sendStarted: false, serverRejected: false, new IOException("connect")));
        Assert.False(SmtpStateMachine.AutoRetry(SmtpAcceptance.Uncertain));
        var id = MessageIdentity.Ensure("<same@alondra1.local>");
        Assert.Equal(id, MessageIdentity.Ensure(id));
    }

    [Fact]
    public void Pop_dedup_and_delete_require_uidl_consent_and_a_durable_commit()
    {
        Assert.True(Pop3Policy.AlreadyStored("abc", ["abc"]));
        Assert.False(Pop3Policy.AlreadyStored("abc", ["other"]));
        Assert.False(Pop3Policy.CanDeleteAfterDownload(uidlSupported: false, enabled: true, consented: true, durableCommit: true));
        Assert.False(Pop3Policy.CanDeleteAfterDownload(true, true, consented: false, true));
        Assert.False(Pop3Policy.CanDeleteAfterDownload(true, true, true, durableCommit: false));
        Assert.True(Pop3Policy.CanDeleteAfterDownload(true, true, true, true));
    }

    [Fact]
    public void Imap_uid_validity_change_resets_the_mailbox()
    {
        Assert.Equal(ImapReconcileAction.Initialize, ImapReconciler.OnOpen(null, 5));
        Assert.Equal(ImapReconcileAction.Continue, ImapReconciler.OnOpen(5, 5));
        Assert.Equal(ImapReconcileAction.ResetMailbox, ImapReconciler.OnOpen(5, 9));
        Assert.False(ImapReconciler.CheckpointIsValid("v=5;uid=10;mod=1", 9));
    }

    [Fact]
    public void Html_sanitizer_strips_scripts_handlers_and_remote_images()
    {
        var html = """<p onclick="alert(1)">Hi</p><script>alert(1)</script><img src="https://evil.test/a.png"><a href="javascript:alert(1)">x</a>""";
        var clean = HtmlMailSanitizer.Sanitize(html, allowRemoteImages: false);
        Assert.DoesNotContain("script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://evil.test", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hi", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_switch_blocks_while_sends_are_pending()
    {
        Assert.Equal(ModeTransitionKind.StartInitialSync, ModeTransition.Evaluate(AccountMode.Online, AccountMode.Personal, 0));
        Assert.Equal(ModeTransitionKind.MustResolveOrDiscardPending, ModeTransition.Evaluate(AccountMode.Personal, AccountMode.Online, 2));
        Assert.Equal(ModeTransitionKind.ReviewLocalThenRemove, ModeTransition.Evaluate(AccountMode.Personal, AccountMode.Online, 0));
    }

    [Fact]
    public void Provider_presets_use_the_documented_endpoints()
    {
        var microsoft = Account();
        ProviderPresets.ApplyMicrosoft(microsoft, MailProtocol.Imap);
        Assert.Equal("outlook.office365.com", microsoft.Incoming.Host);
        Assert.Equal(993, microsoft.Incoming.Port);
        Assert.Equal("smtp.office365.com", microsoft.Outgoing.Host);
        Assert.Equal(587, microsoft.Outgoing.Port);
        Assert.Equal(TransportSecurity.RequiredStartTls, microsoft.Outgoing.Security);
        var gmail = Account();
        ProviderPresets.ApplyGmail(gmail, MailProtocol.Pop3);
        Assert.Equal("pop.gmail.com", gmail.Incoming.Host);
        Assert.Equal(995, gmail.Incoming.Port);
        Assert.Equal(AuthenticationKind.OAuth, gmail.Incoming.Authentication);
    }

    [Fact]
    public async Task Personal_search_is_scoped_and_online_sync_does_not_store_mail()
    {
        var root = Temp();
        var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
        await store.InitializeAsync(CancellationToken.None);
        var batch = new PersonalBatch();
        batch.Upserts.Add(Message("work", "Inbox", 1, "alpha lighthouse"));
        batch.Upserts.Add(Message("home", "Inbox", 1, "beta lighthouse"));
        batch.Bodies.Add((new MessageKey("work", "Inbox", 1), "alpha lighthouse body", null));
        await store.CommitAsync(batch, CancellationToken.None);
        var scoped = await store.SearchAsync("work", "Inbox", "lighthouse", CancellationToken.None);
        Assert.Single(scoped);
        Assert.Equal("work", scoped[0].AccountId);

        var cache = new SessionCache();
        var sync = new SyncCoordinator(new FakeGateway(), store, cache, new MemorySecrets(), new PassthroughTokens(), new SystemClock(), new RecordingConflicts());
        var online = Account();
        online.Mode = AccountMode.Online;
        online.Incoming.Authentication = AuthenticationKind.None;
        online.Outgoing.Authentication = AuthenticationKind.None;
        var report = await sync.SyncAsync(online, CancellationToken.None);
        Assert.True(report.Ok);
        Assert.Equal(2, await store.CountMessagesAsync(null, CancellationToken.None));
        Assert.NotEmpty(cache.List(online.Id, "Inbox"));
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Removing_an_account_deletes_only_its_local_data_and_secrets()
    {
        var root = Temp();
        var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
        await store.InitializeAsync(CancellationToken.None);
        var batch = new PersonalBatch();
        batch.Upserts.Add(Message("work", "Inbox", 1, "alpha lighthouse"));
        batch.Upserts.Add(Message("home", "Inbox", 1, "beta lighthouse"));
        await store.CommitAsync(batch, CancellationToken.None);
        var cache = new SessionCache();
        cache.SetMessages("work", "Inbox", [Message("work", "Inbox", 2, "cached")]);
        cache.SetMessages("home", "Inbox", [Message("home", "Inbox", 2, "cached")]);
        var secrets = new DpapiSecretStore(Path.Combine(root, "secrets"));
        await secrets.SaveAsync("work", new SecretBundle { Password = "one" }, CancellationToken.None);
        await secrets.SaveAsync("home", new SecretBundle { Password = "two" }, CancellationToken.None);
        var sync = new SyncCoordinator(new FakeGateway(), store, cache, secrets, new PassthroughTokens(), new SystemClock(), new RecordingConflicts());

        await sync.RemoveAccountAsync("work", CancellationToken.None);

        Assert.Equal(0, await store.CountMessagesAsync("work", CancellationToken.None));
        Assert.Equal(1, await store.CountMessagesAsync("home", CancellationToken.None));
        Assert.Empty(cache.List("work", "Inbox"));
        Assert.NotEmpty(cache.List("home", "Inbox"));
        Assert.Null((await secrets.LoadAsync("work", CancellationToken.None)).Password);
        Assert.Equal("two", (await secrets.LoadAsync("home", CancellationToken.None)).Password);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Outbox_survives_restart_and_uncertain_send_is_not_retried_automatically()
    {
        var root = Temp();
        var path = Path.Combine(root, "personal.db");
        var store = new SqlitePersonalStore(path);
        await store.InitializeAsync(CancellationToken.None);
        var gateway = new FakeGateway { Acceptance = SmtpAcceptance.Uncertain };
        var sync = new SyncCoordinator(gateway, store, new SessionCache(), new MemorySecrets(), new PassthroughTokens(), new SystemClock(), new RecordingConflicts());
        var account = Account(AccountMode.Personal);
        account.Incoming.Authentication = AuthenticationKind.None;
        account.Outgoing.Authentication = AuthenticationKind.None;
        var mail = new OutgoingMail { FromAddress = account.Address, To = ["person@example.com"], Subject = "Hello", TextBody = "Body", MessageId = "<stable@alondra1.local>" };
        var first = await sync.SendAsync(account, mail, CancellationToken.None);
        Assert.Equal(SmtpAcceptance.Uncertain, first.Acceptance);
        Assert.Equal("<stable@alondra1.local>", first.MessageId);
        await store.DisposeAsync();

        var reopened = new SqlitePersonalStore(path);
        await reopened.InitializeAsync(CancellationToken.None);
        var ops = await reopened.ListOperationsAsync(account.Id, CancellationToken.None);
        Assert.Contains(ops, operation => operation.State == SmtpAcceptance.Uncertain && operation.MessageId == "<stable@alondra1.local>");
        var again = new SyncCoordinator(gateway, reopened, new SessionCache(), new MemorySecrets(), new PassthroughTokens(), new SystemClock(), new RecordingConflicts());
        await again.SyncAsync(account, CancellationToken.None);
        Assert.Equal(1, gateway.SendCount);
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task Ten_thousand_messages_can_be_indexed_and_searched()
    {
        var store = new SqlitePersonalStore(Path.Combine(Temp(), "personal.db"));
        await store.InitializeAsync(CancellationToken.None);
        var batch = new PersonalBatch();
        for (var i = 0; i < 10000; i++)
            batch.Upserts.Add(Message("perf", "Inbox", i + 1, i == 4242 ? "unique lighthouse token" : "ordinary note " + i));
        var started = DateTime.UtcNow;
        await store.CommitAsync(batch, CancellationToken.None);
        var commit = DateTime.UtcNow - started;
        started = DateTime.UtcNow;
        var found = await store.SearchAsync("perf", "Inbox", "lighthouse", CancellationToken.None);
        var search = DateTime.UtcNow - started;
        Assert.Single(found);
        Assert.True(commit < TimeSpan.FromSeconds(30), $"commit {commit}");
        Assert.True(search < TimeSpan.FromSeconds(5), $"search {search}");
        await store.DisposeAsync();
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "alondra1-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AccountConfig Account(AccountMode mode = AccountMode.Online) => new()
    {
        Id = "account-1",
        Kind = AccountKind.Manual,
        Mode = mode,
        DisplayName = "Work",
        Address = "a@example.com",
        Incoming = new IncomingSettings { Host = "imap.example.com", Port = 993, Username = "a@example.com" },
        Outgoing = new OutgoingSettings { Host = "smtp.example.com", Port = 587, Username = "a@example.com" },
    };

    private static StoredMessage Message(string account, string folder, long uid, string subject) => new()
    {
        AccountId = account,
        FolderId = folder,
        Uid = uid,
        Subject = subject,
        FromName = "Maya Chen",
        FromAddress = "maya@example.com",
        ReceivedUtc = DateTimeOffset.Parse("2026-11-15T09:54:00Z"),
        TimeLabel = "09:54",
        Preview = subject,
        Availability = ItemAvailability.Downloaded,
        BodyDownloaded = true,
    };
}

file sealed class FakeGateway : IMailGateway
{
    public SmtpAcceptance Acceptance { get; set; } = SmtpAcceptance.Accepted;
    public int SendCount { get; private set; }

    public Task<ProbeResult> ProbeIncomingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) =>
        Task.FromResult(new ProbeResult(true, "ok", null));

    public Task<ProbeResult> ProbeOutgoingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) =>
        Task.FromResult(new ProbeResult(true, "ok", null));

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteFolder>>([new RemoteFolder { Id = "Inbox", Name = "Inbox", SpecialUse = "inbox", UidValidity = 3 }]);

    public Task<RemoteSyncBatch> FetchChangesAsync(AccountConfig account, SecretBundle secrets, RemoteFolder folder, string? checkpoint, int? historyLimit, CancellationToken cancellationToken)
    {
        var batch = new RemoteSyncBatch
        {
            NextCheckpoint = "v=3;uid=1;mod=0",
            RemoteUids = [1],
            RemoteUidsComplete = true,
            Upserts = [new StoredMessage { FolderId = folder.Id, Uid = 1, Subject = "Server", FromName = "Ada", ReceivedUtc = DateTimeOffset.UnixEpoch, TimeLabel = "09:00" }],
        };
        return Task.FromResult(batch);
    }

    public Task<StoredBody> FetchBodyAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, CancellationToken cancellationToken) =>
        Task.FromResult(new StoredBody { Text = "body" });

    public Task FetchAttachmentAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, string fileName, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ApplyAsync(AccountConfig account, SecretBundle secrets, RemoteMailCommand command, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<SendResult> SendAsync(AccountConfig account, SecretBundle secrets, OutgoingMail mail, CancellationToken cancellationToken)
    {
        SendCount++;
        return Task.FromResult(new SendResult(Acceptance, MessageIdentity.Ensure(mail.MessageId), Acceptance == SmtpAcceptance.Uncertain ? "unknown" : null, false));
    }

    public Task IdleAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeletePopMessagesAsync(AccountConfig account, SecretBundle secrets, IReadOnlyList<string> uidls, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

file sealed class MemorySecrets : ISecretStore
{
    public Task<SecretBundle> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult(new SecretBundle());
    public Task SaveAsync(string accountId, SecretBundle bundle, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class PassthroughTokens : ITokenRefresher
{
    public Task<SecretBundle> EnsureFreshAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken) =>
        Task.FromResult(secrets);
}

file sealed class RecordingConflicts : IConflictSink
{
    public void ReportPreservedBoth(string description) { }
}
