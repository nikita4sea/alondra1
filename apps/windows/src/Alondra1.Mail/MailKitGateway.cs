using System.Net.Sockets;
using Alondra1.Core.Config;
using Alondra1.Core.Html;
using Alondra1.Core.Mail;
using Alondra1.Core.Ports;
using Alondra1.Core.Security;
using Alondra1.Core.Sync;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MailSendResult = Alondra1.Core.Mail.SendResult;

namespace Alondra1.Mail;

public sealed class MailKitGateway : IMailGateway
{
    /// <summary>
    /// Legacy charsets (windows-1251, koi8-r, iso-8859-2, …) must be available before MimeKit first looks one up: it
    /// caches the answer, and without them Cyrillic and Central European subjects decode as Latin-1.
    /// </summary>
    static MailKitGateway() => MimeHeaders.EnsureCharsets();

    public Task<ProbeResult> ProbeIncomingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken) =>
        account.Incoming.Protocol == MailProtocol.Imap
            ? ProbeImapAsync(account, secrets, cancellationToken)
            : ProbePopAsync(account, secrets, cancellationToken);

    public async Task<ProbeResult> ProbeOutgoingAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        if (PortAdvisor.OutgoingProblem(account.Outgoing) is { } problem)
            return new ProbeResult(false, problem, FailureKind.Protocol);
        using var client = new SmtpClient { Timeout = 30000 };
        try
        {
            await client.ConnectAsync(account.Outgoing.Host, account.Outgoing.Port, Map(account.Outgoing.Security), cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(client, account, secrets, incoming: false, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true, "Outgoing server accepted the connection.", null);
        }
        catch (Exception ex)
        {
            return MapFailure(account, ex);
        }
    }

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
        {
            return [new RemoteFolder { Id = "Inbox", Name = "Inbox", SpecialUse = "inbox" }];
        }

        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        var result = new List<RemoteFolder>();
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = await client.GetFoldersAsync(personal, StatusItems.None, false, cancellationToken).ConfigureAwait(false);
        foreach (var folder in folders)
        {
            result.Add(new RemoteFolder
            {
                Id = folder.FullName,
                Name = string.IsNullOrEmpty(folder.Name) ? folder.FullName : folder.Name,
                SpecialUse = SpecialUse(folder, client),
            });
        }

        if (result.All(folder => folder.SpecialUse != "inbox"))
            result.Insert(0, new RemoteFolder { Id = client.Inbox.FullName, Name = "Inbox", SpecialUse = "inbox" });
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<RemoteSyncBatch> FetchChangesAsync(AccountConfig account, SecretBundle secrets, RemoteFolder folder, string? checkpoint, int? historyLimit, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
            return await FetchPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);

        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        var usedQuick = false;
        if (client.Capabilities.Contains(ImapCapability.QuickResync))
        {
            try
            {
                await client.EnableAsync(ImapFeatures.QuickResync, cancellationToken).ConfigureAwait(false);
                usedQuick = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                usedQuick = false;
            }
        }

        var mailFolder = await client.GetFolderAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var parsed = ImapReconciler.ParseCheckpoint(checkpoint);
        var reset = ImapReconciler.OnOpen(parsed.UidValidity, mailFolder.UidValidity) == ImapReconcileAction.ResetMailbox;
        var condStore = usedQuick || client.Capabilities.Contains(ImapCapability.CondStore);
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure | MessageSummaryItems.Size;
        if (condStore)
            items |= MessageSummaryItems.ModSeq;

        IList<IMessageSummary> summaries;
        var complete = false;
        if (!reset && parsed.LastUid > 0)
        {
            var range = new UniqueIdRange(new UniqueId((uint)Math.Min(uint.MaxValue, parsed.LastUid + 1)), UniqueId.MaxValue);
            summaries = await mailFolder.FetchAsync(range, items, cancellationToken).ConfigureAwait(false);
            if (condStore && parsed.ModSeq > 0)
            {
                var changed = await mailFolder.SearchAsync(SearchQuery.ChangedSince(parsed.ModSeq), cancellationToken).ConfigureAwait(false);
                if (changed.Count > 0)
                {
                    var extra = await mailFolder.FetchAsync(changed, items, cancellationToken).ConfigureAwait(false);
                    summaries = summaries.Concat(extra).GroupBy(item => item.UniqueId).Select(group => group.First()).ToList();
                }
            }

            var known = await mailFolder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken).ConfigureAwait(false);
            complete = true;
            var batch = ToBatch(account, folder, known.Concat(summaries).GroupBy(item => item.UniqueId).Select(group => group.Last()).ToList(), mailFolder, complete);
            batch.ResetMailbox = false;
            batch.UsedCondStore = condStore;
            batch.UsedQuickResync = usedQuick;
            batch.SupportsIdle = client.Capabilities.Contains(ImapCapability.Idle);
            batch.NextCheckpoint = ImapReconciler.WriteCheckpoint(mailFolder.UidValidity, batch.RemoteUids.DefaultIfEmpty(0).Max(), mailFolder.HighestModSeq);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return batch;
        }

        if (historyLimit is > 0 && mailFolder.Count > historyLimit.Value)
        {
            var start = mailFolder.Count - historyLimit.Value;
            summaries = await mailFolder.FetchAsync(start, -1, items, cancellationToken).ConfigureAwait(false);
            complete = false;
        }
        else
        {
            summaries = mailFolder.Count == 0
                ? []
                : await mailFolder.FetchAsync(0, -1, items, cancellationToken).ConfigureAwait(false);
            complete = true;
        }

        var full = ToBatch(account, folder, summaries, mailFolder, complete);
        full.ResetMailbox = reset;
        full.UsedCondStore = condStore;
        full.UsedQuickResync = usedQuick;
        full.SupportsIdle = client.Capabilities.Contains(ImapCapability.Idle);
        var last = full.RemoteUids.DefaultIfEmpty(0).Max();
        full.NextCheckpoint = ImapReconciler.WriteCheckpoint(mailFolder.UidValidity, last, mailFolder.HighestModSeq);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        return full;
    }

    public async Task<StoredBody> FetchBodyAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
        {
            using var pop = await ConnectPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);
            var index = await FindPopIndexAsync(account, pop, uid, cancellationToken).ConfigureAwait(false);
            var message = await pop.GetMessageAsync(index, cancellationToken).ConfigureAwait(false);
            await pop.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return MimeBody.Extract(message, account.Id, folderId, uid);
        }

        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        var folder = await client.GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var mime = await folder.GetMessageAsync(new UniqueId((uint)uid), cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        return MimeBody.Extract(mime, account.Id, folderId, uid);
    }

    public async Task FetchAttachmentAsync(AccountConfig account, SecretBundle secrets, string folderId, long uid, string fileName, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
        {
            using var pop = await ConnectPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);
            var index = await FindPopIndexAsync(account, pop, uid, cancellationToken).ConfigureAwait(false);
            var message = await pop.GetMessageAsync(index, cancellationToken).ConfigureAwait(false);
            await pop.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            var popPart = MimeBody.FindAttachment(message, fileName) ?? throw new InvalidOperationException("That attachment is no longer on the server.");
            await MimeBody.WriteAsync(popPart, destination, cancellationToken).ConfigureAwait(false);
            progress?.Report(destination.Length);
            return;
        }

        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        client.Timeout = 120000;
        var folder = await client.GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        var summary = (await folder.FetchAsync(new[] { new UniqueId((uint)uid) }, MessageSummaryItems.BodyStructure, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var part = summary?.Attachments.FirstOrDefault(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? summary?.BodyParts.FirstOrDefault(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        MimeEntity? entity;
        if (part is not null)
            entity = await folder.GetBodyPartAsync(new UniqueId((uint)uid), part, cancellationToken).ConfigureAwait(false);
        else
        {
            // Parts without a file name get a generated one ("attachment-2.pdf"); find those in the full message.
            var message = await folder.GetMessageAsync(new UniqueId((uint)uid), cancellationToken).ConfigureAwait(false);
            entity = MimeBody.FindAttachment(message, fileName);
        }

        if (entity is null)
            throw new InvalidOperationException("That attachment is no longer on the server.");
        await MimeBody.WriteAsync(entity, destination, cancellationToken).ConfigureAwait(false);
        progress?.Report(destination.Length);

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyAsync(AccountConfig account, SecretBundle secrets, RemoteMailCommand command, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol == MailProtocol.Pop3)
            throw new InvalidOperationException("POP3 does not support remote folders, flags, move, or server drafts.");

        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        if (command.Kind == MailCommandKind.SaveDraft && command.Draft is not null)
        {
            var drafts = await ResolveSpecialAsync(client, account.Folders.Drafts, FolderAttributes.Drafts, "Drafts", cancellationToken).ConfigureAwait(false);
            var mime = BuildMime(command.Draft);
            await drafts.AppendAsync(mime, MessageFlags.Draft, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var folder = await client.GetFolderAsync(command.FolderId, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        var uid = new UniqueId((uint)command.Uid);
        switch (command.Kind)
        {
            case MailCommandKind.SetRead:
                await folder.SetFlagsAsync(new[] { uid }, MessageFlags.Seen, command.Value, cancellationToken).ConfigureAwait(false);
                break;
            case MailCommandKind.SetFlagged:
                await folder.SetFlagsAsync(new[] { uid }, MessageFlags.Flagged, command.Value, cancellationToken).ConfigureAwait(false);
                break;
            case MailCommandKind.Delete:
                var trash = await ResolveSpecialAsync(client, account.Folders.Trash, FolderAttributes.Trash, "Trash", cancellationToken).ConfigureAwait(false);
                if (client.Capabilities.Contains(ImapCapability.Move))
                    await folder.MoveToAsync(uid, trash, cancellationToken).ConfigureAwait(false);
                else
                {
                    await folder.CopyToAsync(uid, trash, cancellationToken).ConfigureAwait(false);
                    await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
                    await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            case MailCommandKind.Archive:
            case MailCommandKind.Move:
                // An explicit destination is a real folder id from the folder list; Archive is resolved like Sent and Trash.
                var destination = command.DestinationFolderId is { } destinationId
                    ? await client.GetFolderAsync(destinationId, cancellationToken).ConfigureAwait(false)
                    : await ResolveSpecialAsync(client, account.Folders.Archive, FolderAttributes.Archive, "Archive", cancellationToken).ConfigureAwait(false);
                if (client.Capabilities.Contains(ImapCapability.Move))
                    await folder.MoveToAsync(uid, destination, cancellationToken).ConfigureAwait(false);
                else
                {
                    await folder.CopyToAsync(uid, destination, cancellationToken).ConfigureAwait(false);
                    await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
                    await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MailSendResult> SendAsync(AccountConfig account, SecretBundle secrets, OutgoingMail mail, CancellationToken cancellationToken)
    {
        var mime = BuildMime(mail);
        var messageId = MessageIdentity.Ensure(mail.MessageId);
        mime.MessageId = messageId.Trim('<', '>');
        // Nothing was sent: a contradiction between port and security cannot connect, so do not try (and do not wait).
        if (PortAdvisor.OutgoingProblem(account.Outgoing) is { } problem)
            return new MailSendResult(SmtpAcceptance.Failed, messageId, problem, false);
        using var client = new SmtpClient { Timeout = 120000 };
        var started = false;
        try
        {
            await client.ConnectAsync(account.Outgoing.Host, account.Outgoing.Port, Map(account.Outgoing.Security), cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(client, account, secrets, incoming: false, cancellationToken).ConfigureAwait(false);
            started = true;
            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            var stored = false;
            if (!SentCopyPolicy.ProviderFilesSentAutomatically(account.Outgoing.Host) && account.Incoming.Protocol == MailProtocol.Imap)
                stored = await TryAppendSentAsync(account, secrets, mime, cancellationToken).ConfigureAwait(false);
            return new MailSendResult(SmtpAcceptance.Accepted, messageId, null, stored);
        }
        catch (SmtpCommandException ex)
        {
            return new MailSendResult(SmtpStateMachine.Classify(started, serverRejected: true, ex), messageId, ProviderDiagnostics.Explain(account.Kind, ex.Message), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MailSendResult(SmtpStateMachine.Classify(started, serverRejected: false, ex), messageId, ProviderDiagnostics.Explain(account.Kind, ex.Message), false);
        }
    }

    public async Task IdleAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        if (account.Incoming.Protocol != MailProtocol.Imap)
            return;
        using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        if (!client.Capabilities.Contains(ImapCapability.Idle))
        {
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        using var done = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, done.Token);
        try
        {
            await client.IdleAsync(linked.Token, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        if (client.IsConnected)
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task DeletePopMessagesAsync(AccountConfig account, SecretBundle secrets, IReadOnlyList<string> uidls, CancellationToken cancellationToken)
    {
        if (!account.PopDeleteAfterDownload || !account.PopDeleteConsented || uidls.Count == 0)
            return;
        using var client = await ConnectPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        if (!client.SupportsUids)
        {
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var remote = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
        var wanted = uidls.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < remote.Count; index++)
        {
            if (wanted.Contains(remote[index]))
                await client.DeleteMessageAsync(index, cancellationToken).ConfigureAwait(false);
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeImapAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
            var detail = client.Capabilities.Contains(ImapCapability.Idle) ? "IMAP connected. The server supports IDLE." : "IMAP connected.";
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true, detail, null);
        }
        catch (Exception ex)
        {
            return MapFailure(account, ex);
        }
    }

    private async Task<ProbeResult> ProbePopAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);
            var detail = client.SupportsUids ? "POP3 connected. UIDL is available." : "POP3 connected. UIDL is not available.";
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true, detail, null);
        }
        catch (Exception ex)
        {
            return MapFailure(account, ex);
        }
    }

    private async Task<RemoteSyncBatch> FetchPopAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        using var client = await ConnectPopAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        var batch = new RemoteSyncBatch { UidlSupported = client.SupportsUids, DegradedDedup = !client.SupportsUids };
        if (client.Count == 0)
        {
            batch.RemoteUidsComplete = client.SupportsUids;
            batch.NextCheckpoint = client.SupportsUids ? "pop=uidl" : "pop=index";
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return batch;
        }

        IList<string>? uidls = null;
        if (client.SupportsUids)
            uidls = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < client.Count; index++)
        {
            var headers = await client.GetMessageHeadersAsync(index, cancellationToken).ConfigureAwait(false);
            var uidl = uidls is not null && index < uidls.Count ? uidls[index] : null;
            var uid = uidl is null ? index + 1 : MessageIdentity.StableUid(account.Id + "|" + uidl);
            // The raw header is "Name <address>"; show the name and keep the address, as for IMAP.
            var fromHeader = MimeHeaders.Text(headers, HeaderId.From);
            var sender = InternetAddressList.TryParse(fromHeader, out var parsedFrom) ? parsedFrom.Mailboxes.FirstOrDefault() : null;
            var fromAddress = sender?.Address ?? fromHeader;
            var fromName = string.IsNullOrWhiteSpace(sender?.Name) ? fromAddress : sender.Name;
            var subject = MimeHeaders.Text(headers, HeaderId.Subject);
            var date = headers[HeaderId.Date];
            // RFC 5322 dates ("Sun, 22 Nov 2026 13:20:00 +0000 (UTC)") need MimeKit's parser; DateTimeOffset.TryParse rejects most of them.
            var received = date is not null && MimeKit.Utils.DateUtils.TryParse(date, out var parsed) ? parsed : DateTimeOffset.UtcNow;
            batch.Upserts.Add(new StoredMessage
            {
                AccountId = account.Id,
                AccountName = account.DisplayName,
                FolderId = "Inbox",
                Uid = uid,
                MessageId = headers[HeaderId.MessageId],
                Subject = subject,
                FromName = fromName,
                FromAddress = fromAddress,
                ReceivedUtc = received,
                TimeLabel = MailText.FormatTime(received, DateTimeOffset.Now),
                IsUnread = true,
                LocalOnly = false,
                PopUidl = uidl,
                Availability = ItemAvailability.Downloaded,
            });
            batch.RemoteUids.Add(uid);
        }

        batch.RemoteUidsComplete = client.SupportsUids;
        batch.NextCheckpoint = client.SupportsUids ? "pop=uidl" : "pop=index";
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        return batch;
    }

    private static RemoteSyncBatch ToBatch(AccountConfig account, RemoteFolder folder, IList<IMessageSummary> summaries, IMailFolder mailFolder, bool complete)
    {
        var batch = new RemoteSyncBatch { RemoteUidsComplete = complete, UidlSupported = true };
        foreach (var summary in summaries)
        {
            var from = summary.Envelope?.From?.Mailboxes.FirstOrDefault();
            var received = summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.UtcNow;
            batch.Upserts.Add(new StoredMessage
            {
                AccountId = account.Id,
                AccountName = account.DisplayName,
                FolderId = folder.Id,
                Uid = summary.UniqueId.Id,
                MessageId = summary.Envelope?.MessageId,
                Subject = summary.Envelope?.Subject ?? "",
                FromName = string.IsNullOrWhiteSpace(from?.Name) ? from?.Address ?? "" : from.Name,
                FromAddress = from?.Address ?? "",
                Recipients = string.Join(", ", summary.Envelope?.To?.Mailboxes.Select(item => item.Address) ?? []),
                ReceivedUtc = received,
                TimeLabel = MailText.FormatTime(received, DateTimeOffset.Now),
                IsUnread = !summary.Flags.HasValue || !summary.Flags.Value.HasFlag(MessageFlags.Seen),
                IsFlagged = summary.Flags?.HasFlag(MessageFlags.Flagged) == true,
                HasAttachments = summary.Attachments.Any(),
                Availability = ItemAvailability.RemoteOnly,
            });
            batch.RemoteUids.Add(summary.UniqueId.Id);
        }

        batch.NextCheckpoint = ImapReconciler.WriteCheckpoint(mailFolder.UidValidity, batch.RemoteUids.DefaultIfEmpty(0).Max(), mailFolder.HighestModSeq);
        return batch;
    }

    private static MimeMessage BuildMime(OutgoingMail mail)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(mail.FromName, mail.FromAddress));
        foreach (var address in mail.To)
            message.To.Add(MailboxAddress.Parse(address));
        foreach (var address in mail.Cc)
            message.Cc.Add(MailboxAddress.Parse(address));
        foreach (var address in mail.Bcc)
            message.Bcc.Add(MailboxAddress.Parse(address));
        message.Subject = mail.Subject;
        if (!string.IsNullOrWhiteSpace(mail.InReplyTo))
            message.InReplyTo = mail.InReplyTo;
        var body = new TextPart("plain") { Text = mail.TextBody };
        if (mail.Attachments.Count == 0)
        {
            message.Body = body;
            return message;
        }

        var mixed = new Multipart("mixed") { body };
        foreach (var attachment in mail.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.SourcePath) || !File.Exists(attachment.SourcePath))
                continue;
            var part = new MimePart(attachment.ContentType)
            {
                Content = new MimeContent(File.OpenRead(attachment.SourcePath)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = attachment.FileName,
            };
            mixed.Add(part);
        }

        message.Body = mixed;
        return message;
    }

    private async Task<bool> TryAppendSentAsync(AccountConfig account, SecretBundle secrets, MimeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectImapAsync(account, secrets, cancellationToken).ConfigureAwait(false);
            var sent = await ResolveSpecialAsync(client, account.Folders.Sent, FolderAttributes.Sent, "Sent", cancellationToken).ConfigureAwait(false);
            await sent.AppendAsync(message, MessageFlags.Seen, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds Sent, Drafts, or Trash. Servers name and nest these differently (for example "INBOX.INBOX.Sent"), so the
    /// configured value is matched against the real folder list instead of being opened by path (MailKit's GetFolder
    /// throws for a path that does not exist). Order: exact configured path, the server's special-use flag, a folder
    /// whose display name matches, and only then a new folder.
    /// </summary>
    private static async Task<IMailFolder> ResolveSpecialAsync(ImapClient client, string? configured, FolderAttributes attribute, string fallback, CancellationToken cancellationToken)
    {
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var folders = await client.GetFoldersAsync(personal, StatusItems.None, false, cancellationToken).ConfigureAwait(false);
        var wanted = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        var match = folders.FirstOrDefault(folder => string.Equals(folder.FullName, wanted, StringComparison.OrdinalIgnoreCase))
            ?? folders.FirstOrDefault(folder => folder.Attributes.HasFlag(attribute))
            ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, fallback, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        var root = client.GetFolder(personal);
        return await root.CreateAsync(fallback, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> FindPopIndexAsync(AccountConfig account, Pop3Client client, long uid, CancellationToken cancellationToken)
    {
        if (!client.SupportsUids)
            return (int)Math.Max(0, uid - 1);
        var uidls = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < uidls.Count; index++)
        {
            if (MessageIdentity.StableUid(account.Id + "|" + uidls[index]) == uid)
                return index;
        }

        throw new InvalidOperationException("That message is no longer on the POP server.");
    }

    private static async Task<ImapClient> ConnectImapAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var decision = ConnectionPolicy.Evaluate(account, secrets);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Reason);
        var client = new ImapClient { Timeout = 60000 };
        try
        {
            await client.ConnectAsync(account.Incoming.Host, account.Incoming.Port, Map(account.Incoming.Security), cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(client, account, secrets, incoming: true, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<Pop3Client> ConnectPopAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var decision = ConnectionPolicy.Evaluate(account, secrets);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Reason);
        var client = new Pop3Client { Timeout = 60000 };
        try
        {
            await client.ConnectAsync(account.Incoming.Host, account.Incoming.Port, Map(account.Incoming.Security), cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(client, account, secrets, incoming: true, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task AuthenticateAsync(MailService client, AccountConfig account, SecretBundle secrets, bool incoming, CancellationToken cancellationToken)
    {
        var authentication = incoming ? account.Incoming.Authentication : account.Outgoing.Authentication;
        var user = incoming ? account.Incoming.Username : account.Outgoing.Username;
        if (string.IsNullOrWhiteSpace(user))
            user = account.Address;
        if (authentication == AuthenticationKind.None)
            return;
        if (authentication == AuthenticationKind.OAuth)
        {
            var token = account.Kind == AccountKind.Gmail ? secrets.Google?.AccessToken : secrets.Exchange?.AccessToken;
            if (string.IsNullOrEmpty(token))
                throw new AuthenticationException("Sign in again to refresh this account.");
            await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.AuthenticateAsync(user, secrets.Password ?? "", cancellationToken).ConfigureAwait(false);
    }

    private static SecureSocketOptions Map(TransportSecurity security) => security switch
    {
        TransportSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
        TransportSecurity.RequiredStartTls => SecureSocketOptions.StartTls,
        TransportSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.SslOnConnect,
    };

    private static string? SpecialUse(IMailFolder folder, ImapClient client)
    {
        if (folder.FullName == client.Inbox.FullName || folder.Attributes.HasFlag(FolderAttributes.Inbox))
            return "inbox";
        if (folder.Attributes.HasFlag(FolderAttributes.Sent))
            return "sent";
        if (folder.Attributes.HasFlag(FolderAttributes.Drafts))
            return "drafts";
        if (folder.Attributes.HasFlag(FolderAttributes.Trash))
            return "trash";
        if (folder.Attributes.HasFlag(FolderAttributes.Archive))
            return "archive";
        if (folder.Attributes.HasFlag(FolderAttributes.Junk))
            return "junk";
        return null;
    }

    private static ProbeResult MapFailure(AccountConfig account, Exception ex)
    {
        if (ex is OperationCanceledException)
            throw ex;
        if (ex is AuthenticationException)
            return new ProbeResult(false, ProviderDiagnostics.Explain(account.Kind, ex.Message), FailureKind.Authentication);
        if (ex is SslHandshakeException ssl)
        {
            var upgrade = ssl.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase);
            return upgrade
                ? new ProbeResult(false, "The server did not offer the required STARTTLS upgrade.", FailureKind.RequiredUpgrade)
                : new ProbeResult(false, "The server certificate was rejected.", FailureKind.Certificate);
        }

        if (ex is IOException or SocketException or TimeoutException)
            return new ProbeResult(false, "The server could not be reached.", FailureKind.Network);
        return new ProbeResult(false, ProviderDiagnostics.Explain(account.Kind, ex.Message), FailureKind.Protocol);
    }
}
