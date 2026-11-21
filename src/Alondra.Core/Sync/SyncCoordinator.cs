using System.Text.Json;
using Alondra.Core.Config;
using Alondra.Core.Mail;
using Alondra.Core.Ports;
using Alondra.Core.Security;

namespace Alondra.Core.Sync;

public sealed class SyncCoordinator
{
    private readonly IMailGateway _mail;
    private readonly IPersonalStore _store;
    private readonly ISessionCache _cache;
    private readonly ISecretStore _secrets;
    private readonly ITokenRefresher _tokens;
    private readonly IClock _clock;
    private readonly IConflictSink _conflicts;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public SyncCoordinator(
        IMailGateway mail,
        IPersonalStore store,
        ISessionCache cache,
        ISecretStore secrets,
        ITokenRefresher tokens,
        IClock clock,
        IConflictSink conflicts)
    {
        _mail = mail;
        _store = store;
        _cache = cache;
        _secrets = secrets;
        _tokens = tokens;
        _clock = clock;
        _conflicts = conflicts;
    }

    public async Task<SyncReport> SyncAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        var gate = Gate(account.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await _secrets.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
            var decision = ConnectionPolicy.Evaluate(account, secrets);
            if (!decision.Allowed)
                return SyncReport.Stopped(decision.Reason ?? "Connection blocked.");

            try
            {
                secrets = await _tokens.EnsureFreshAsync(account, secrets, TokenAudience.Mail, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SyncReport.Failed(ProviderDiagnostics.Explain(account.Kind, ex.Message), IsOffline(ex));
            }

            return account.Mode == AccountMode.Online
                ? await SyncOnlineAsync(account, secrets, cancellationToken).ConfigureAwait(false)
                : await SyncPersonalAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SyncReport.Failed("Sync cancelled.");
        }
        catch (Exception ex)
        {
            return SyncReport.Failed(ProviderDiagnostics.Explain(account.Kind, ex.Message), IsOffline(ex));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SendResult> SendAsync(AccountConfig account, OutgoingMail mail, CancellationToken cancellationToken)
    {
        mail.MessageId = MessageIdentity.Ensure(mail.MessageId);
        var secrets = await _secrets.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var decision = ConnectionPolicy.Evaluate(account, secrets);
        if (!decision.Allowed && account.Mode == AccountMode.Online)
            return new SendResult(SmtpAcceptance.Failed, mail.MessageId, decision.Reason, false);

        if (account.Mode == AccountMode.Personal)
        {
            await EnqueueSendAsync(account, mail, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return new SendResult(SmtpAcceptance.Queued, mail.MessageId, decision.Reason, false);
            return await DrainSendsAsync(account, cancellationToken).ConfigureAwait(false);
        }

        secrets = await _tokens.EnsureFreshAsync(account, secrets, TokenAudience.Mail, cancellationToken).ConfigureAwait(false);
        var result = await _mail.SendAsync(account, secrets, mail, cancellationToken).ConfigureAwait(false);
        if (result.Acceptance == SmtpAcceptance.Accepted)
        {
            _cache.Upsert(LocalCopy(account, mail, "Sent", ItemAvailability.Downloaded, localOnly: account.Incoming.Protocol == MailProtocol.Pop3, result.Acceptance));
        }
        else if (result.Acceptance == SmtpAcceptance.Uncertain)
        {
            _cache.Upsert(LocalCopy(account, mail, "Outbox", ItemAvailability.Pending, localOnly: true, result.Acceptance));
        }

        return result;
    }

    public async Task<SendResult> RetryAsync(AccountConfig account, string operationId, CancellationToken cancellationToken)
    {
        var ops = await _store.ListOperationsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var op = ops.FirstOrDefault(item => item.Id == operationId);
        if (op is null)
            return new SendResult(SmtpAcceptance.Failed, "", "That outgoing message is no longer queued.", false);
        op.State = SmtpAcceptance.Queued;
        op.UpdatedUtc = _clock.UtcNow;
        var batch = new PersonalBatch();
        batch.Operations.Add(op);
        await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
        return await DrainSendsAsync(account, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDraftAsync(AccountConfig account, OutgoingMail mail, CancellationToken cancellationToken)
    {
        mail.MessageId = MessageIdentity.Ensure(mail.MessageId);
        if (account.Mode == AccountMode.Online && account.Incoming.Protocol == MailProtocol.Pop3)
        {
            _cache.Upsert(LocalCopy(account, mail, "Drafts", ItemAvailability.RemoteOnly, localOnly: true, null));
            return;
        }

        if (account.Mode == AccountMode.Online)
        {
            var secrets = await _secrets.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
            secrets = await _tokens.EnsureFreshAsync(account, secrets, TokenAudience.Mail, cancellationToken).ConfigureAwait(false);
            await _mail.ApplyAsync(account, secrets, new RemoteMailCommand
            {
                Kind = MailCommandKind.SaveDraft,
                Draft = mail,
                FolderId = account.Folders.Drafts ?? "Drafts",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var copy = LocalCopy(account, mail, account.Folders.Drafts ?? "Drafts", ItemAvailability.Downloaded, localOnly: true, null);
        var batch = new PersonalBatch();
        batch.Upserts.Add(copy);
        batch.Bodies.Add((new MessageKey(copy.AccountId, copy.FolderId, copy.Uid), mail.TextBody, null));
        await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyLocalCommandAsync(AccountConfig account, RemoteMailCommand command, CancellationToken cancellationToken)
    {
        if (account.Mode == AccountMode.Online)
        {
            if (account.Incoming.Protocol == MailProtocol.Pop3 && command.Kind is not MailCommandKind.SaveDraft)
                throw new InvalidOperationException("POP3 does not support remote folders, read state, or move.");
            var secrets = await _secrets.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
            var decision = ConnectionPolicy.Evaluate(account, secrets);
            if (!decision.Allowed)
                throw new InvalidOperationException(decision.Reason);
            secrets = await _tokens.EnsureFreshAsync(account, secrets, TokenAudience.Mail, cancellationToken).ConfigureAwait(false);
            await _mail.ApplyAsync(account, secrets, command, cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = await _store.ListAsync(account.Id, command.FolderId, cancellationToken).ConfigureAwait(false);
        var message = messages.FirstOrDefault(item => item.Uid == command.Uid);
        if (message is null)
            return;
        switch (command.Kind)
        {
            case MailCommandKind.SetRead:
                message.IsUnread = !command.Value;
                break;
            case MailCommandKind.SetFlagged:
                message.IsFlagged = command.Value;
                break;
            case MailCommandKind.Delete:
                message.FolderId = account.Folders.Trash ?? "Trash";
                message.LocalOnly = account.Incoming.Protocol == MailProtocol.Pop3 || message.LocalOnly;
                break;
            case MailCommandKind.Archive:
                message.FolderId = account.Folders.Archive ?? "Archive";
                break;
            case MailCommandKind.Move:
                message.FolderId = command.DestinationFolderId ?? message.FolderId;
                break;
        }

        message.Availability = ItemAvailability.Pending;
        var op = new QueuedOperation
        {
            Id = Guid.NewGuid().ToString("n"),
            AccountId = account.Id,
            Kind = command.Kind.ToString(),
            PayloadJson = JsonSerializer.Serialize(command, ConfigJson.Options),
            State = SmtpAcceptance.Queued,
            CreatedUtc = _clock.UtcNow,
            UpdatedUtc = _clock.UtcNow,
        };
        var batch = new PersonalBatch();
        batch.Deletes.Add(new MessageKey(account.Id, command.FolderId, command.Uid));
        batch.Upserts.Add(message);
        if (account.Incoming.Protocol != MailProtocol.Pop3)
            batch.Operations.Add(op);
        await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PendingCountAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        var ops = await _store.ListOperationsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        return ops.Count(op => op.State is SmtpAcceptance.Queued or SmtpAcceptance.Submitting or SmtpAcceptance.Failed or SmtpAcceptance.Uncertain);
    }

    public async Task DiscardPendingAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        var ops = await _store.ListOperationsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var batch = new PersonalBatch();
        foreach (var op in ops)
        {
            op.State = SmtpAcceptance.Failed;
            op.LastError = "Discarded while leaving Personal mode.";
            op.UpdatedUtc = _clock.UtcNow;
            batch.Operations.Add(op);
        }

        await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
        await _store.DeleteAccountDataAsync(account.Id, cancellationToken).ConfigureAwait(false);
        _cache.ClearAccount(account.Id);
    }

    private async Task<SyncReport> SyncOnlineAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var folders = await _mail.ListFoldersAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _mail.FetchChangesAsync(account, secrets, folder, checkpoint: null, historyLimit: account.Sync.HistoryLimit, cancellationToken).ConfigureAwait(false);
            foreach (var message in batch.Upserts)
            {
                message.AccountId = account.Id;
                message.AccountName = account.DisplayName;
                message.Availability = ItemAvailability.RemoteOnly;
                message.BodyDownloaded = false;
            }

            _cache.SetMessages(account.Id, folder.Id, batch.Upserts);
        }

        return SyncReport.Done("Online view refreshed.");
    }

    private async Task<SyncReport> SyncPersonalAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var folders = await _mail.ListFoldersAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        var degraded = false;
        foreach (var folder in folders.Where(folder => FolderSelected(account, folder)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = await _store.GetCheckpointAsync(account.Id, folder.Id, cancellationToken).ConfigureAwait(false);
            var action = ImapReconciler.OnOpen(ImapReconciler.ParseCheckpoint(checkpoint).UidValidity, folder.UidValidity);
            if (account.Incoming.Protocol == MailProtocol.Pop3)
                action = ImapReconcileAction.Continue;
            if (action == ImapReconcileAction.ResetMailbox)
                checkpoint = null;

            var limit = checkpoint is null ? account.Sync.HistoryLimit : null;
            var remote = await _mail.FetchChangesAsync(account, secrets, folder, checkpoint, limit, cancellationToken).ConfigureAwait(false);
            degraded |= remote.DegradedDedup;
            if (remote.DegradedDedup && !account.PopUidlMissingDisclosed)
                account.PopUidlMissingDisclosed = true;

            if (account.Incoming.Protocol == MailProtocol.Pop3 && remote.UidlSupported)
            {
                var known = await _store.ListPopUidlsAsync(account.Id, cancellationToken).ConfigureAwait(false);
                remote.Upserts = remote.Upserts.Where(message => !Pop3Policy.AlreadyStored(message.PopUidl, known)).ToList();
            }

            var batch = new PersonalBatch();
            if (action == ImapReconcileAction.ResetMailbox)
            {
                batch.ClearFolderAccountId = account.Id;
                batch.ClearFolderId = folder.Id;
            }

            foreach (var message in remote.Upserts)
            {
                message.AccountId = account.Id;
                message.AccountName = account.DisplayName;
                message.Availability = ItemAvailability.Downloaded;
                var local = await ExistingAsync(account.Id, folder.Id, message.Uid, cancellationToken).ConfigureAwait(false);
                if (local is not null && ConflictPolicy.Resolve(local.LocalOnly, true, folder.SpecialUse == "drafts") == ConflictResolution.PreserveBoth)
                {
                    message.Uid = -Math.Abs(message.Uid == 0 ? 1 : message.Uid);
                    message.LocalOnly = true;
                    message.Subject = "Remote copy · " + message.Subject;
                    _conflicts.ReportPreservedBoth($"Kept both versions of “{message.Subject}”.");
                }

                batch.Upserts.Add(message);
            }

            if (remote.RemoteUidsComplete)
            {
                var localMessages = await _store.ListAsync(account.Id, folder.Id, cancellationToken).ConfigureAwait(false);
                var remoteSet = remote.RemoteUids.ToHashSet();
                foreach (var local in localMessages.Where(item => item.Uid > 0 && !item.LocalOnly && !remoteSet.Contains(item.Uid)))
                    batch.Deletes.Add(new MessageKey(account.Id, folder.Id, local.Uid));
            }

            batch.Checkpoint = (account.Id, folder.Id, remote.NextCheckpoint);
            await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);

            if (ShouldDownloadBodies(account, folder))
                await DownloadBodiesAsync(account, secrets, folder, remote.Upserts, cancellationToken).ConfigureAwait(false);

            if (Pop3Policy.CanDeleteAfterDownload(remote.UidlSupported, account.PopDeleteAfterDownload, account.PopDeleteConsented, durableCommit: true))
            {
                var uidls = remote.Upserts.Select(item => item.PopUidl).Where(item => !string.IsNullOrEmpty(item)).Cast<string>().ToList();
                if (uidls.Count > 0)
                    await _mail.DeletePopMessagesAsync(account, secrets, uidls, cancellationToken).ConfigureAwait(false);
            }
        }

        await RecoverSubmittingAsync(account, cancellationToken).ConfigureAwait(false);
        await DrainSendsAsync(account, cancellationToken).ConfigureAwait(false);
        var detail = degraded ? Pop3Policy.DegradedDedupNotice : "Saved mail is up to date.";
        return new SyncReport { Ok = true, Detail = detail, DegradedDedup = degraded };
    }

    private async Task DownloadBodiesAsync(AccountConfig account, SecretBundle secrets, RemoteFolder folder, IReadOnlyList<StoredMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages.Where(item => !item.BodyDownloaded && item.Uid > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await _mail.FetchBodyAsync(account, secrets, folder.Id, message.Uid, cancellationToken).ConfigureAwait(false);
            message.BodyDownloaded = true;
            message.Availability = ItemAvailability.Downloaded;
            message.Preview = body.Text;
            var batch = new PersonalBatch();
            batch.Upserts.Add(message);
            batch.Bodies.Add((new MessageKey(account.Id, folder.Id, message.Uid), body.Text, body.Html));
            await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnqueueSendAsync(AccountConfig account, OutgoingMail mail, CancellationToken cancellationToken)
    {
        var copy = LocalCopy(account, mail, "Outbox", ItemAvailability.Pending, localOnly: true, SmtpAcceptance.Queued);
        var op = new QueuedOperation
        {
            Id = Guid.NewGuid().ToString("n"),
            AccountId = account.Id,
            Kind = "send",
            PayloadJson = JsonSerializer.Serialize(mail, ConfigJson.Options),
            State = SmtpAcceptance.Queued,
            MessageId = mail.MessageId,
            CreatedUtc = _clock.UtcNow,
            UpdatedUtc = _clock.UtcNow,
        };
        var batch = new PersonalBatch();
        batch.Upserts.Add(copy);
        batch.Bodies.Add((new MessageKey(copy.AccountId, copy.FolderId, copy.Uid), mail.TextBody, null));
        batch.Operations.Add(op);
        await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendResult> DrainSendsAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        SendResult? last = null;
        var ops = await _store.ListOperationsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        foreach (var op in ops.Where(item => item.Kind == "send").OrderBy(item => item.CreatedUtc))
        {
            if (op.State == SmtpAcceptance.Uncertain)
            {
                last = new SendResult(SmtpAcceptance.Uncertain, op.MessageId ?? "", "Delivery is uncertain. Retry only if you mean to send it again.", false);
                continue;
            }

            if (!SmtpStateMachine.AutoRetry(op.State) && op.State != SmtpAcceptance.Submitting)
                continue;
            if (op.State == SmtpAcceptance.Failed && _clock.UtcNow < op.UpdatedUtc + SmtpStateMachine.Backoff(op.Attempt))
                continue;

            var mail = JsonSerializer.Deserialize<OutgoingMail>(op.PayloadJson, ConfigJson.Options);
            if (mail is null)
                continue;
            mail.MessageId = MessageIdentity.Ensure(op.MessageId);
            op.State = SmtpAcceptance.Submitting;
            op.UpdatedUtc = _clock.UtcNow;
            op.Attempt += 1;
            var mark = new PersonalBatch();
            mark.Operations.Add(op);
            await _store.CommitAsync(mark, cancellationToken).ConfigureAwait(false);

            try
            {
                var secrets = await _secrets.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
                secrets = await _tokens.EnsureFreshAsync(account, secrets, TokenAudience.Mail, cancellationToken).ConfigureAwait(false);
                var result = await _mail.SendAsync(account, secrets, mail, cancellationToken).ConfigureAwait(false);
                op.State = result.Acceptance;
                op.MessageId = result.MessageId;
                op.LastError = result.Error;
                op.UpdatedUtc = _clock.UtcNow;
                var done = new PersonalBatch();
                done.Operations.Add(op);
                var sent = LocalCopy(account, mail, result.Acceptance == SmtpAcceptance.Accepted ? "Sent" : "Outbox",
                    result.Acceptance == SmtpAcceptance.Accepted ? ItemAvailability.Downloaded : ItemAvailability.Pending,
                    localOnly: account.Incoming.Protocol == MailProtocol.Pop3 || result.Acceptance != SmtpAcceptance.Accepted,
                    result.Acceptance);
                done.Upserts.Add(sent);
                await _store.CommitAsync(done, cancellationToken).ConfigureAwait(false);
                last = result;
                if (result.Acceptance == SmtpAcceptance.Uncertain)
                    break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                op.State = SmtpAcceptance.Failed;
                op.LastError = ProviderDiagnostics.Explain(account.Kind, ex.Message);
                op.UpdatedUtc = _clock.UtcNow;
                var failed = new PersonalBatch();
                failed.Operations.Add(op);
                await _store.CommitAsync(failed, cancellationToken).ConfigureAwait(false);
                last = new SendResult(SmtpAcceptance.Failed, mail.MessageId ?? "", op.LastError, false);
            }
        }

        return last ?? new SendResult(SmtpAcceptance.Queued, "", null, false);
    }

    private async Task RecoverSubmittingAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        var ops = await _store.ListOperationsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var batch = new PersonalBatch();
        foreach (var op in ops.Where(item => item.Kind == "send" && item.State == SmtpAcceptance.Submitting))
        {
            op.State = SmtpAcceptance.Uncertain;
            op.LastError = "The app stopped while sending. Retry only if the message should be sent again.";
            op.UpdatedUtc = _clock.UtcNow;
            batch.Operations.Add(op);
        }

        if (batch.Operations.Count > 0)
            await _store.CommitAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredMessage?> ExistingAsync(string accountId, string folderId, long uid, CancellationToken cancellationToken)
    {
        var list = await _store.ListAsync(accountId, folderId, cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(item => item.Uid == uid);
    }

    private static bool FolderSelected(AccountConfig account, RemoteFolder folder)
    {
        if (account.Sync.SelectedFolders.Count == 0)
            return true;
        return account.Sync.SelectedFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)
            || account.Sync.SelectedFolders.Contains(folder.Id, StringComparer.OrdinalIgnoreCase)
            || account.Sync.PinnedFolders.Contains(folder.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldDownloadBodies(AccountConfig account, RemoteFolder folder) =>
        account.Sync.FullBodyFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)
        || account.Sync.FullBodyFolders.Contains(folder.Id, StringComparer.OrdinalIgnoreCase)
        || account.Sync.PinnedFolders.Contains(folder.Id, StringComparer.OrdinalIgnoreCase);

    private StoredMessage LocalCopy(AccountConfig account, OutgoingMail mail, string folder, ItemAvailability availability, bool localOnly, SmtpAcceptance? state)
    {
        return new StoredMessage
        {
            AccountId = account.Id,
            AccountName = account.DisplayName,
            FolderId = folder,
            Uid = -Math.Abs(MessageIdentity.StableUid(mail.MessageId ?? Guid.NewGuid().ToString("n"))),
            MessageId = mail.MessageId,
            Subject = mail.Subject,
            FromName = mail.FromName,
            FromAddress = mail.FromAddress,
            Recipients = string.Join(", ", mail.To),
            ReceivedUtc = _clock.UtcNow,
            TimeLabel = MailText.FormatTime(_clock.UtcNow, _clock.UtcNow),
            BodyDownloaded = true,
            LocalOnly = localOnly,
            Availability = availability,
            Preview = mail.TextBody,
            HasAttachments = mail.Attachments.Count > 0,
            SendState = state?.ToString(),
        };
    }

    private SemaphoreSlim Gate(string accountId)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(accountId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[accountId] = gate;
            }

            return gate;
        }
    }

    private static bool IsOffline(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException
        || ex.InnerException is HttpRequestException or IOException or TimeoutException;
}
