using Alondra1.Core.Config;

namespace Alondra1.Core.Mail;

/// <summary>
/// Maps the folder names the app shows (Inbox, Sent, Drafts, Archive, Trash) to each account's real folder ids. IMAP
/// servers name and nest these differently ("INBOX", "INBOX.INBOX.Sent", "[Gmail]/Sent Mail"), so the ids come from
/// the server's special-use flags seen during sync and are kept in <see cref="AccountConfig.Folders"/>.
/// </summary>
public static class SpecialFolders
{
    public static readonly IReadOnlyList<string> Displayed = ["Inbox", "Sent", "Drafts", "Archive", "Trash"];

    /// <summary>The folder id to read for a displayed folder of this account.</summary>
    public static string Resolve(AccountConfig account, string display)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
            return display; // POP3 downloads into "Inbox"; sent copies and other folders exist only locally, under their display names
        var folders = account.Folders;
        return display switch
        {
            "Inbox" => folders.Inbox ?? "INBOX", // RFC 3501: the inbox is always reachable as INBOX
            "Sent" => folders.Sent ?? "Sent",
            "Drafts" => folders.Drafts ?? "Drafts",
            "Archive" => folders.Archive ?? "Archive",
            "Trash" => folders.Trash ?? "Trash",
            _ => display,
        };
    }

    /// <summary>
    /// Records the real ids from a sync's folder list: a special-use flag wins, then a folder whose display name matches.
    /// Returns true when the account changed (the caller saves the configuration).
    /// </summary>
    public static bool Learn(AccountConfig account, IReadOnlyList<RemoteFolder> folders)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3 || folders.Count == 0)
            return false;
        var map = account.Folders;
        var changed = false;
        void Set(string? found, Func<string?> get, Action<string> set)
        {
            if (found is not null && !string.Equals(get(), found, StringComparison.Ordinal))
            {
                set(found);
                changed = true;
            }
        }

        string? Find(string special, string display)
        {
            return folders.FirstOrDefault(folder => string.Equals(folder.SpecialUse, special, StringComparison.OrdinalIgnoreCase))?.Id
                ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, display, StringComparison.OrdinalIgnoreCase))?.Id
                ?? folders.FirstOrDefault(folder => string.Equals(folder.Id, display, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        Set(Find("inbox", "Inbox"), () => map.Inbox, value => map.Inbox = value);
        Set(Find("sent", "Sent"), () => map.Sent, value => map.Sent = value);
        Set(Find("drafts", "Drafts"), () => map.Drafts, value => map.Drafts = value);
        Set(Find("archive", "Archive"), () => map.Archive, value => map.Archive = value);
        Set(Find("trash", "Trash"), () => map.Trash, value => map.Trash = value);
        return changed;
    }
}
