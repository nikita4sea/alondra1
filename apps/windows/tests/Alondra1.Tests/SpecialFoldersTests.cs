using Alondra1.Core.Config;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Displayed folders (Inbox, Sent, …) map to the server's real, possibly nested, folder ids.</summary>
public class SpecialFoldersTests
{
    private static AccountConfig Account(MailProtocol protocol = MailProtocol.Imap) => new()
    {
        Id = "a",
        DisplayName = "Dev",
        Address = "dev@example.com",
        Incoming = new IncomingSettings { Protocol = protocol, Host = "mail.example.com", Port = 993, Security = TransportSecurity.ImplicitTls },
    };

    // A server that nests every folder under INBOX and advertises no special-use flags except the inbox.
    private static readonly RemoteFolder[] Nested =
    [
        new() { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox" },
        new() { Id = "INBOX.Junk", Name = "Junk" },
        new() { Id = "INBOX.INBOX.Trash", Name = "Trash" },
        new() { Id = "INBOX.INBOX.Sent", Name = "Sent" },
        new() { Id = "INBOX.INBOX.Drafts", Name = "Drafts" },
    ];

    [Fact]
    public void UnlearnedImapAccountUsesStandardNames()
    {
        var account = Account();
        Assert.Equal("INBOX", SpecialFolders.Resolve(account, "Inbox"));
        Assert.Equal("Sent", SpecialFolders.Resolve(account, "Sent"));
        Assert.Equal("Custom", SpecialFolders.Resolve(account, "Custom"));
    }

    [Fact]
    public void LearnsNestedFoldersByName()
    {
        var account = Account();
        Assert.True(SpecialFolders.Learn(account, Nested));
        Assert.Equal("INBOX", SpecialFolders.Resolve(account, "Inbox"));
        Assert.Equal("INBOX.INBOX.Sent", SpecialFolders.Resolve(account, "Sent"));
        Assert.Equal("INBOX.INBOX.Drafts", SpecialFolders.Resolve(account, "Drafts"));
        Assert.Equal("INBOX.INBOX.Trash", SpecialFolders.Resolve(account, "Trash"));
        Assert.Equal("Archive", SpecialFolders.Resolve(account, "Archive")); // the server has none
        Assert.False(SpecialFolders.Learn(account, Nested)); // nothing new the second time
    }

    [Fact]
    public void SpecialUseFlagWinsOverName()
    {
        var account = Account();
        SpecialFolders.Learn(account,
        [
            new() { Id = "INBOX", Name = "INBOX", SpecialUse = "inbox" },
            new() { Id = "Sent", Name = "Sent" },
            new() { Id = "[Gmail]/Sent Mail", Name = "Sent Mail", SpecialUse = "sent" },
        ]);
        Assert.Equal("[Gmail]/Sent Mail", SpecialFolders.Resolve(account, "Sent"));
    }

    [Fact]
    public void Pop3KeepsDisplayNames()
    {
        var account = Account(MailProtocol.Pop3);
        Assert.False(SpecialFolders.Learn(account, Nested));
        Assert.Equal("Inbox", SpecialFolders.Resolve(account, "Inbox"));
        Assert.Equal("Sent", SpecialFolders.Resolve(account, "Sent")); // local sent copies
    }
}
