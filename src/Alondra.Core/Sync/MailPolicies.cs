using Alondra.Core.Config;
using Alondra.Core.Mail;

namespace Alondra.Core.Sync;

public enum ImapReconcileAction
{
    Initialize,
    Continue,
    ResetMailbox,
}

public enum ConflictResolution
{
    TakeIncoming,
    KeepLocal,
    MergeFlags,
    PreserveBoth,
}

public enum ModeTransitionKind
{
    None,
    StartInitialSync,
    MustResolveOrDiscardPending,
    ReviewLocalThenRemove,
}

public static class ImapReconciler
{
    public static ImapReconcileAction OnOpen(uint? storedUidValidity, uint remoteUidValidity)
    {
        if (storedUidValidity is null)
            return ImapReconcileAction.Initialize;
        if (storedUidValidity.Value != remoteUidValidity)
            return ImapReconcileAction.ResetMailbox;
        return ImapReconcileAction.Continue;
    }

    public static bool CheckpointIsValid(string? checkpoint, uint remoteUidValidity)
    {
        var parsed = ParseCheckpoint(checkpoint);
        if (parsed.UidValidity is null)
            return true;
        return parsed.UidValidity.Value == remoteUidValidity;
    }

    public static (uint? UidValidity, long LastUid, ulong ModSeq) ParseCheckpoint(string? checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint))
            return (null, 0, 0);
        uint? validity = null;
        long last = 0;
        ulong mod = 0;
        foreach (var part in checkpoint.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split('=', 2);
            if (bits.Length != 2)
                continue;
            if (bits[0] == "v" && uint.TryParse(bits[1], out var v))
                validity = v;
            else if (bits[0] == "uid" && long.TryParse(bits[1], out var uid))
                last = uid;
            else if (bits[0] == "mod" && ulong.TryParse(bits[1], out var m))
                mod = m;
        }

        return (validity, last, mod);
    }

    public static string WriteCheckpoint(uint uidValidity, long lastUid, ulong modSeq) =>
        $"v={uidValidity};uid={lastUid};mod={modSeq}";
}

public static class Pop3Policy
{
    public static bool AlreadyStored(string? uidl, IEnumerable<string?> known) =>
        !string.IsNullOrEmpty(uidl) && known.Contains(uidl);

    public static bool CanDeleteAfterDownload(bool uidlSupported, bool enabled, bool consented, bool durableCommit) =>
        uidlSupported && enabled && consented && durableCommit;

    public static string DegradedDedupNotice =>
        "This POP server does not provide UIDL. Alondra cannot reliably tell messages apart, so delete-after-download stays off until you confirm that duplicates are acceptable.";
}

public static class SmtpStateMachine
{
    public static SmtpAcceptance Classify(bool sendStarted, bool serverRejected, Exception? error)
    {
        if (error is null && !serverRejected)
            return SmtpAcceptance.Accepted;
        if (serverRejected || !sendStarted)
            return SmtpAcceptance.Failed;
        return SmtpAcceptance.Uncertain;
    }

    public static bool AutoRetry(SmtpAcceptance state) =>
        state is SmtpAcceptance.Queued or SmtpAcceptance.Failed;

    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Max(0, attempt)));
        return TimeSpan.FromSeconds(seconds);
    }
}

public static class SentCopyPolicy
{
    public static bool ProviderFilesSentAutomatically(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        return name is "smtp.gmail.com" or "imap.gmail.com" or "smtp.office365.com" or "outlook.office365.com"
            || name.EndsWith(".gmail.com", StringComparison.Ordinal)
            || name.EndsWith(".googlemail.com", StringComparison.Ordinal)
            || name.EndsWith(".office365.com", StringComparison.Ordinal)
            || name.EndsWith(".outlook.com", StringComparison.Ordinal);
    }
}

public static class ConflictPolicy
{
    public static ConflictResolution Resolve(bool localChanged, bool remoteChanged, bool draftBody)
    {
        if (!localChanged && remoteChanged)
            return ConflictResolution.TakeIncoming;
        if (localChanged && !remoteChanged)
            return ConflictResolution.KeepLocal;
        if (localChanged && remoteChanged && draftBody)
            return ConflictResolution.PreserveBoth;
        if (localChanged && remoteChanged)
            return ConflictResolution.MergeFlags;
        return ConflictResolution.TakeIncoming;
    }
}

public static class ModeTransition
{
    public static ModeTransitionKind Evaluate(AccountMode current, AccountMode target, int pendingOperations)
    {
        if (current == target)
            return ModeTransitionKind.None;
        if (current == AccountMode.Online && target == AccountMode.Personal)
            return ModeTransitionKind.StartInitialSync;
        if (pendingOperations > 0)
            return ModeTransitionKind.MustResolveOrDiscardPending;
        return ModeTransitionKind.ReviewLocalThenRemove;
    }
}

public static class TlsMapping
{
    public static string Describe(TransportSecurity security) => security switch
    {
        TransportSecurity.ImplicitTls => "Implicit TLS",
        TransportSecurity.RequiredStartTls => "Required STARTTLS",
        TransportSecurity.None => "No encryption",
        _ => security.ToString(),
    };
}

public static class ProviderDiagnostics
{
    public static string Explain(AccountKind kind, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "The mail server could not be reached.";
        var text = error;
        if (Contains(text, "IMAP is disabled") || Contains(text, "Protocol IMAP") || Contains(text, "IMAP disabled"))
            return "IMAP is disabled for this mailbox. Enable it in the mailbox settings, or choose POP3.";
        if (Contains(text, "POP is disabled") || Contains(text, "POP disabled"))
            return "POP is disabled for this mailbox. Enable it in the mailbox settings, or choose IMAP.";
        if (Contains(text, "SmtpClientAuthentication is disabled") || Contains(text, "5.7.57") || Contains(text, "SMTP AUTH"))
            return "SMTP AUTH is disabled for this mailbox. An administrator can enable authenticated SMTP submission.";
        if (Contains(text, "AADSTS65001") || Contains(text, "consent_required") || Contains(text, "admin_consent"))
            return "Sign-in needs consent that this account or tenant has not granted.";
        if (Contains(text, "AADSTS700016"))
            return "The Microsoft application id was not found in this tenant. Check the public client id in Settings.";
        if (Contains(text, "invalid_grant") || Contains(text, "revoked"))
            return "The saved sign-in was revoked or expired. Connect the account again.";
        if (Contains(text, "ACCESS_TOKEN_SCOPE_INSUFFICIENT") || Contains(text, "insufficient"))
            return "The account has not granted the permission this feature needs.";
        if (kind == AccountKind.Gmail && Contains(text, "Web login required"))
            return "Google blocked the sign-in. Confirm the OAuth client and that IMAP or POP is enabled for the mailbox.";
        return text.Length > 280 ? text[..280] : text;
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
