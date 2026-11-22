using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Security;
using Alondra1.Core.Sync;
using Alondra1.Mail;
using Alondra1.Persistence;
using Xunit.Abstractions;

namespace Alondra1.Tests;

/// <summary>
/// Opt-in tests against two real mailboxes. They run only when ALONDRA1_LIVE_MAIL_FILE names a local, git-ignored file:
///   USER1=... PASSWORD1=... USER2=... PASSWORD2=... POP3=host:port SMTP=host:port IMAP=host:port
/// Credentials are read from that file at run time and never written anywhere.
/// </summary>
public class LiveMailTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string>? Settings = Load();

    private static Dictionary<string, string>? Load()
    {
        var path = Environment.GetEnvironmentVariable("ALONDRA1_LIVE_MAIL_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static (string Host, int Port) Server(string key)
    {
        var value = Settings![key];
        var colon = value.LastIndexOf(':');
        return (value[..colon], int.Parse(value[(colon + 1)..]));
    }

    /// <summary>An account as a user would enter it for a provider with implicit TLS everywhere (993, 995, 465).</summary>
    private static AccountConfig Account(int user, MailProtocol protocol, AccountMode mode)
    {
        var address = Settings![$"USER{user}"];
        var (inHost, inPort) = Server(protocol == MailProtocol.Imap ? "IMAP" : "POP3");
        var (outHost, outPort) = Server("SMTP");
        return new AccountConfig
        {
            Id = $"live-{user}-{protocol}-{mode}".ToLowerInvariant(),
            Kind = AccountKind.Manual,
            Mode = mode,
            DisplayName = address,
            Address = address,
            Incoming = new IncomingSettings { Protocol = protocol, Host = inHost, Port = inPort, Security = TransportSecurity.ImplicitTls, Username = address },
            Outgoing = new OutgoingSettings { Host = outHost, Port = outPort, Security = TransportSecurity.ImplicitTls, Username = address },
            Folders = new FolderMap { Sent = "Sent", Drafts = "Drafts", Trash = "Trash", Archive = "Archive" },
        };
    }

    private static SecretBundle Secret(int user) => new() { Password = Settings![$"PASSWORD{user}"] };

    [LiveMailFact]
    public async Task Probes_succeed_for_imap_pop3_and_smtp()
    {
        var gateway = new MailKitGateway();
        foreach (var protocol in new[] { MailProtocol.Imap, MailProtocol.Pop3 })
        {
            var account = Account(1, protocol, AccountMode.Online);
            var incoming = await gateway.ProbeIncomingAsync(account, Secret(1), CancellationToken.None);
            output.WriteLine($"{protocol} incoming: {incoming.Ok} {incoming.Detail} ({incoming.Failure})");
            Assert.True(incoming.Ok, $"{protocol}: {incoming.Detail} ({incoming.Failure})");
        }

        var outgoing = await gateway.ProbeOutgoingAsync(Account(1, MailProtocol.Imap, AccountMode.Online), Secret(1), CancellationToken.None);
        output.WriteLine($"SMTP: {outgoing.Ok} {outgoing.Detail} ({outgoing.Failure})");
        Assert.True(outgoing.Ok, $"SMTP: {outgoing.Detail} ({outgoing.Failure})");
    }

    [LiveMailFact]
    public async Task Imap_folders_are_listed_with_their_special_uses()
    {
        var folders = await new MailKitGateway().ListFoldersAsync(Account(1, MailProtocol.Imap, AccountMode.Online), Secret(1), CancellationToken.None);
        foreach (var folder in folders)
            output.WriteLine($"folder: id='{folder.Id}' name='{folder.Name}' special='{folder.SpecialUse}'");
        Assert.Contains(folders, folder => folder.SpecialUse == "inbox");
    }

    /// <summary>Drafts and Trash live under nested names on some servers ("INBOX.INBOX.Trash").</summary>
    [LiveMailFact]
    public async Task Drafts_and_delete_find_the_servers_special_folders()
    {
        var gateway = new MailKitGateway();
        var account = Account(2, MailProtocol.Imap, AccountMode.Online);
        var subject = $"Alondra1 folder test {Guid.NewGuid():N}";
        await gateway.ApplyAsync(account, Secret(2), new RemoteMailCommand
        {
            Kind = MailCommandKind.SaveDraft,
            Draft = new OutgoingMail { FromAddress = account.Address, To = [account.Address], Subject = subject + " draft", TextBody = "draft" },
        }, CancellationToken.None);

        var sent = await gateway.SendAsync(Account(1, MailProtocol.Imap, AccountMode.Online), Secret(1), new OutgoingMail
        {
            FromAddress = Settings!["USER1"], To = [account.Address], Subject = subject, TextBody = "Deleted by the test. Safe to delete.",
        }, CancellationToken.None);
        Assert.Equal(SmtpAcceptance.Accepted, sent.Acceptance);

        var folders = await gateway.ListFoldersAsync(account, Secret(2), CancellationToken.None);
        var inbox = folders.Single(folder => folder.SpecialUse == "inbox");
        var drafts = folders.Single(folder => folder.SpecialUse == "drafts");
        var trash = folders.Single(folder => folder.SpecialUse == "trash");
        Assert.Contains(await Subjects(gateway, account, drafts), s => s == subject + " draft");

        StoredMessage? arrived = null;
        for (var attempt = 0; attempt < 12 && arrived is null; attempt++)
        {
            var batch = await gateway.FetchChangesAsync(account, Secret(2), inbox, null, null, CancellationToken.None);
            arrived = batch.Upserts.FirstOrDefault(message => message.Subject == subject);
            if (arrived is null)
                await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Assert.NotNull(arrived);
        await gateway.ApplyAsync(account, Secret(2), new RemoteMailCommand { Kind = MailCommandKind.Delete, FolderId = inbox.Id, Uid = arrived.Uid }, CancellationToken.None);
        Assert.Contains(await Subjects(gateway, account, trash), s => s == subject);
        Assert.DoesNotContain(await Subjects(gateway, account, inbox), s => s == subject);
        output.WriteLine($"draft saved to '{drafts.Id}', deleted message moved to '{trash.Id}'");
    }

    private static async Task<List<string>> Subjects(MailKitGateway gateway, AccountConfig account, RemoteFolder folder) =>
        (await gateway.FetchChangesAsync(account, Secret(account.Address == Settings!["USER1"] ? 1 : 2), folder, null, null, CancellationToken.None))
            .Upserts.Select(message => message.Subject).ToList();

    /// <summary>Reproduces what a manual account shows when the security setting does not match the port.</summary>
    [LiveMailFact]
    public async Task Mismatched_security_fails_with_a_message_that_names_the_fix()
    {
        var gateway = new MailKitGateway();
        var smtp = Account(1, MailProtocol.Pop3, AccountMode.Online);
        smtp.Outgoing.Security = TransportSecurity.RequiredStartTls; // the outgoing default, on port 465
        var result = await gateway.ProbeOutgoingAsync(smtp, Secret(1), new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        output.WriteLine($"SMTP 465 + STARTTLS: {result.Ok} {result.Detail} ({result.Failure})");
        Assert.False(result.Ok);

        var pop = Account(1, MailProtocol.Pop3, AccountMode.Online);
        pop.Incoming.Security = TransportSecurity.RequiredStartTls;
        var popResult = await gateway.ProbeIncomingAsync(pop, Secret(1), new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        output.WriteLine($"POP3 995 + STARTTLS: {popResult.Ok} {popResult.Detail} ({popResult.Failure})");
        Assert.False(popResult.Ok);
    }

    [LiveMailFact]
    public async Task Mail_sent_from_one_account_arrives_and_syncs_over_pop3_and_imap()
    {
        var gateway = new MailKitGateway();
        var subject = $"Alondra1 live test {Guid.NewGuid():N}";
        var sender = Account(1, MailProtocol.Imap, AccountMode.Online);
        var sent = await gateway.SendAsync(sender, Secret(1), new OutgoingMail
        {
            FromAddress = sender.Address,
            FromName = "Alondra1 test",
            To = [Settings!["USER2"]],
            Subject = subject,
            TextBody = "Sent by Alondra1.Tests.LiveMailTests. Safe to delete.",
        }, CancellationToken.None);
        output.WriteLine($"send: {sent.Acceptance} {sent.Error} stored-in-sent={sent.SentCopyStored}");
        Assert.Equal(SmtpAcceptance.Accepted, sent.Acceptance);
        Assert.True(sent.SentCopyStored, "the copy must be stored in the server's Sent folder (special-use, nested names)");

        foreach (var mode in new[] { AccountMode.Online, AccountMode.Personal })
        {
            foreach (var protocol in new[] { MailProtocol.Pop3, MailProtocol.Imap })
            {
                var account = Account(2, protocol, mode);
                var root = Directory.CreateTempSubdirectory("alondra1-live-").FullName;
                var store = new SqlitePersonalStore(Path.Combine(root, "personal.db"));
                await store.InitializeAsync(CancellationToken.None);
                var cache = new SessionCache();
                var sync = new SyncCoordinator(gateway, store, cache, new FixedSecrets(Secret(2)), new NoTokens(), new SystemClock(), new IgnoreConflicts());
                var found = false;
                for (var attempt = 0; attempt < 12 && !found; attempt++)
                {
                    var report = await sync.SyncAsync(account, CancellationToken.None);
                    output.WriteLine($"{mode}/{protocol} sync {attempt + 1}: ok={report.Ok} {report.Detail}");
                    Assert.True(report.Ok, $"{mode}/{protocol}: {report.Detail}");
                    var messages = mode == AccountMode.Online
                        ? cache.Search(account.Id, null, subject)
                        : await store.SearchAsync(account.Id, null, subject, CancellationToken.None);
                    var match = messages.FirstOrDefault(message => message.Subject == subject);
                    found = match is not null;
                    if (match is not null)
                    {
                        // POP3 and IMAP show the sender the same way: display name, and the plain address.
                        Assert.Equal("Alondra1 test", match.FromName);
                        Assert.Equal(Settings!["USER1"], match.FromAddress, ignoreCase: true);
                    }
                    if (!found)
                        await Task.Delay(TimeSpan.FromSeconds(5));
                }

                await store.DisposeAsync();
                Assert.True(found, $"{mode}/{protocol}: the test message did not arrive");
            }
        }
    }

    private sealed class FixedSecrets(SecretBundle bundle) : ISecretStore
    {
        public Task<SecretBundle> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult(bundle);
        public Task SaveAsync(string accountId, SecretBundle bundle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoTokens : ITokenRefresher
    {
        public Task<SecretBundle> EnsureFreshAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken) =>
            Task.FromResult(secrets);
    }

    private sealed class IgnoreConflicts : IConflictSink
    {
        public void ReportPreservedBoth(string description)
        {
        }
    }
}

public sealed class LiveMailFactAttribute : FactAttribute
{
    public LiveMailFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALONDRA1_LIVE_MAIL_FILE")))
            Skip = "Set ALONDRA1_LIVE_MAIL_FILE to a local credentials file to run tests against real mailboxes.";
    }
}
