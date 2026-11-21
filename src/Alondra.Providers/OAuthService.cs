using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Alondra.Core.Config;
using Alondra.Core.Ports;
using Alondra.Core.Security;
using Microsoft.Identity.Client;

namespace Alondra.Providers;

public static class OAuthScopes
{
    public const string Imap = "https://outlook.office365.com/IMAP.AccessAsUser.All";
    public const string Pop = "https://outlook.office365.com/POP.AccessAsUser.All";
    public const string Smtp = "https://outlook.office365.com/SMTP.Send";
    public const string Offline = "offline_access";
    public const string GraphUser = "User.Read";
    public const string GraphCalendar = "Calendars.ReadWrite";
    public const string GraphTasks = "Tasks.ReadWrite";
    public const string GraphContacts = "Contacts.ReadWrite";
    public const string GraphNotes = "Files.ReadWrite.AppFolder";

    public const string Gmail = "https://mail.google.com/";
    public const string GoogleCalendar = "https://www.googleapis.com/auth/calendar";
    public const string GoogleTasks = "https://www.googleapis.com/auth/tasks";
    public const string GoogleContacts = "https://www.googleapis.com/auth/contacts";
    public const string GoogleNotes = "https://www.googleapis.com/auth/drive.appdata";

    public static string[] MicrosoftMail(AccountConfig account)
    {
        var protocol = account.Incoming.Protocol == MailProtocol.Pop3 ? Pop : Imap;
        return [protocol, Smtp, Offline];
    }

    public static string[] MicrosoftGraph(OAuthClientSettings settings)
    {
        var scopes = new List<string> { GraphUser };
        if (settings.Calendar)
            scopes.Add(GraphCalendar);
        if (settings.Tasks)
            scopes.Add(GraphTasks);
        if (settings.Contacts)
            scopes.Add(GraphContacts);
        if (settings.Notes)
            scopes.Add(GraphNotes);
        return scopes.ToArray();
    }

    public static string[] Google(AccountConfig account, OAuthClientSettings settings, TokenAudience audience)
    {
        var scopes = new List<string>();
        if (audience == TokenAudience.Mail)
            scopes.Add(Gmail);
        if (settings.Calendar)
            scopes.Add(GoogleCalendar);
        if (settings.Tasks)
            scopes.Add(GoogleTasks);
        if (settings.Contacts)
            scopes.Add(GoogleContacts);
        if (settings.Notes)
            scopes.Add(GoogleNotes);
        if (scopes.Count == 0)
            scopes.Add(Gmail);
        return scopes.ToArray();
    }
}

public sealed class OAuthService : ITokenRefresher
{
    private readonly OAuthClientSettings _clients;

    public OAuthService(OAuthClientSettings clients) => _clients = clients;

    public async Task<SecretBundle> EnsureFreshAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.Manual || (account.Incoming.Authentication != AuthenticationKind.OAuth && audience == TokenAudience.Mail))
            return secrets;
        if (account.Kind == AccountKind.Gmail)
            return await RefreshGoogleAsync(account, secrets, audience, cancellationToken).ConfigureAwait(false);
        if (audience == TokenAudience.Graph)
            return await RefreshMicrosoftAsync(account, secrets, graph: true, cancellationToken).ConfigureAwait(false);
        return await RefreshMicrosoftAsync(account, secrets, graph: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecretBundle> SignInAsync(AccountConfig account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Kind == AccountKind.Gmail ? _clients.GoogleClientId : _clients.MicrosoftClientId))
            throw new InvalidOperationException("Add the public OAuth client id in Settings before signing in. Alondra does not ship a client secret.");

        var existing = new SecretBundle();
        if (account.Kind == AccountKind.Gmail)
        {
            var scopes = OAuthScopes.Google(account, _clients, TokenAudience.Mail);
            var token = await AuthorizeGoogleAsync(scopes, cancellationToken).ConfigureAwait(false);
            existing.Google = token;
            return existing;
        }

        var mail = await AuthorizeMicrosoftAsync(OAuthScopes.MicrosoftMail(account), graph: false, existing, cancellationToken).ConfigureAwait(false);
        existing.Exchange = ToToken(mail);
        existing.ExchangeCache = mail.Cache;
        return existing;
    }

    public async Task<SecretBundle> SignInGraphAsync(AccountConfig account, SecretBundle existing, CancellationToken cancellationToken)
    {
        var result = await AuthorizeMicrosoftAsync(OAuthScopes.MicrosoftGraph(_clients), graph: true, existing, cancellationToken).ConfigureAwait(false);
        existing.Graph = ToToken(result);
        existing.GraphCache = result.Cache;
        return existing;
    }

    private async Task<SecretBundle> RefreshMicrosoftAsync(AccountConfig account, SecretBundle secrets, bool graph, CancellationToken cancellationToken)
    {
        var scopes = graph ? OAuthScopes.MicrosoftGraph(_clients) : OAuthScopes.MicrosoftMail(account);
        var app = BuildMicrosoft(graph, secrets);
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var current = accounts.FirstOrDefault();
        if (current is null)
        {
            var stored = graph ? secrets.Graph : secrets.Exchange;
            if (string.IsNullOrEmpty(stored?.RefreshToken) && string.IsNullOrEmpty(stored?.AccessToken))
                throw new InvalidOperationException("Sign in again to refresh this account.");
            if (stored?.ExpiresUtc is null || stored.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
                return secrets;
            throw new InvalidOperationException("The saved sign-in was revoked or expired. Connect the account again.");
        }

        try
        {
            var result = await app.AcquireTokenSilent(scopes, current).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var token = new OAuthToken
            {
                AccessToken = result.AccessToken,
                ExpiresUtc = result.ExpiresOn,
                Scope = string.Join(' ', result.Scopes),
                RefreshToken = (graph ? secrets.Graph : secrets.Exchange)?.RefreshToken,
            };
            if (graph)
                secrets.Graph = token;
            else
                secrets.Exchange = token;
            return secrets;
        }
        catch (MsalUiRequiredException)
        {
            throw new InvalidOperationException("The saved sign-in was revoked or expired. Connect the account again.");
        }
    }

    private async Task<SecretBundle> RefreshGoogleAsync(AccountConfig account, SecretBundle secrets, TokenAudience audience, CancellationToken cancellationToken)
    {
        var current = secrets.Google;
        if (current?.ExpiresUtc is null || current.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            if (!string.IsNullOrEmpty(current?.AccessToken))
                return secrets;
        }

        if (string.IsNullOrEmpty(current?.RefreshToken))
            throw new InvalidOperationException("The saved sign-in was revoked or expired. Connect the account again.");
        var flow = BuildGoogle(OAuthScopes.Google(account, _clients, audience));
        var response = await flow.RefreshTokenAsync("alondra", current.RefreshToken, cancellationToken).ConfigureAwait(false);
        secrets.Google = FromGoogle(response, current.RefreshToken);
        return secrets;
    }

    private async Task<MicrosoftSignIn> AuthorizeMicrosoftAsync(string[] scopes, bool graph, SecretBundle existing, CancellationToken cancellationToken)
    {
        var app = BuildMicrosoft(graph, existing);
        var result = await app.AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return new MicrosoftSignIn(result.AccessToken, result.ExpiresOn, string.Join(' ', result.Scopes), graph ? existing.GraphCache : existing.ExchangeCache);
    }

    private IPublicClientApplication BuildMicrosoft(bool graph, SecretBundle secrets)
    {
        var app = PublicClientApplicationBuilder.Create(_clients.MicrosoftClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(_clients.MicrosoftTenant) ? "common" : _clients.MicrosoftTenant)
            .WithRedirectUri("http://localhost")
            .Build();
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            var data = graph ? secrets.GraphCache : secrets.ExchangeCache;
            if (data is { Length: > 0 })
                args.TokenCache.DeserializeMsalV3(data);
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;
            var data = args.TokenCache.SerializeMsalV3();
            if (graph)
                secrets.GraphCache = data;
            else
                secrets.ExchangeCache = data;
        });
        return app;
    }

    private async Task<OAuthToken> AuthorizeGoogleAsync(string[] scopes, CancellationToken cancellationToken)
    {
        var flow = BuildGoogle(scopes);
        var installed = new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver());
        var credential = await installed.AuthorizeAsync("alondra", cancellationToken).ConfigureAwait(false);
        return FromGoogle(credential.Token, credential.Token.RefreshToken);
    }

    private PkceGoogleAuthorizationCodeFlow BuildGoogle(IEnumerable<string> scopes) =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = _clients.GoogleClientId, ClientSecret = "" },
            Scopes = scopes,
        });

    private static OAuthToken FromGoogle(TokenResponse response, string? refresh) => new()
    {
        AccessToken = response.AccessToken,
        RefreshToken = string.IsNullOrEmpty(response.RefreshToken) ? refresh : response.RefreshToken,
        ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 3600),
        Scope = response.Scope,
    };

    private static OAuthToken ToToken(MicrosoftSignIn result) => new()
    {
        AccessToken = result.AccessToken,
        ExpiresUtc = result.Expires,
        Scope = result.Scope,
    };

    private sealed record MicrosoftSignIn(string AccessToken, DateTimeOffset Expires, string Scope, byte[]? Cache);
}
