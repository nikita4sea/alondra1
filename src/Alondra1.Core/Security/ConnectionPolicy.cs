using Alondra1.Core.Config;

namespace Alondra1.Core.Security;

public sealed class SecretBundle
{
    public string? Password { get; set; }
    public OAuthToken? Exchange { get; set; }
    public OAuthToken? Graph { get; set; }
    public OAuthToken? Google { get; set; }
    public byte[]? ExchangeCache { get; set; }
    public byte[]? GraphCache { get; set; }
}

public sealed class OAuthToken
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public string? Scope { get; set; }
}

public enum TokenAudience
{
    Mail,
    Graph,
    Google,
}

public sealed record ConnectionDecision(bool Allowed, string? Reason);

public static class ConnectionPolicy
{
    public const string PlaintextWarning =
        "This account connects without encryption. Message content and any password can be exposed on the network. Connect only if you accept that.";

    public static ConnectionDecision Evaluate(AccountConfig account, SecretBundle? secrets)
    {
        if (ConfigWorkflow.UsesPlaintext(account) && ConfigWorkflow.UsesOAuth(account))
            return new ConnectionDecision(false, "OAuth tokens are not sent over an unencrypted connection.");

        if (ConfigWorkflow.UsesPlaintext(account) && !account.PlaintextConfirmed)
            return new ConnectionDecision(false, PlaintextWarning);

        if (ConfigWorkflow.UsesPlaintext(account) && secrets?.Exchange?.AccessToken is not null)
            return new ConnectionDecision(false, "A saved Microsoft token cannot be used on an unencrypted server.");
        if (ConfigWorkflow.UsesPlaintext(account) && secrets?.Google?.AccessToken is not null)
            return new ConnectionDecision(false, "A saved Google token cannot be used on an unencrypted server.");

        if (account.Incoming.Authentication == AuthenticationKind.Password && string.IsNullOrEmpty(secrets?.Password))
            return new ConnectionDecision(false, "Enter the password for this account.");

        if (account.Incoming.Authentication == AuthenticationKind.OAuth)
        {
            var token = account.Kind == AccountKind.Gmail ? secrets?.Google : secrets?.Exchange;
            if (string.IsNullOrEmpty(token?.RefreshToken) && string.IsNullOrEmpty(token?.AccessToken))
                return new ConnectionDecision(false, "Sign in again to refresh this account.");
        }

        return new ConnectionDecision(true, null);
    }
}
