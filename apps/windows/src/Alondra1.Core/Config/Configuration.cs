using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alondra1.Core.Config;

public enum ThemePreference { System, Light, Dark }

public enum Density { Compact, Comfortable }

public enum AccountKind { Microsoft365, Gmail, Manual }

public enum AccountMode { Online, Personal }

public enum MailProtocol { Imap, Pop3 }

public enum TransportSecurity { ImplicitTls, RequiredStartTls, None }

public enum AuthenticationKind { Password, OAuth, None }

public static class ConfigSchema
{
    public const int Current = 1;
    public const int Minimum = 1;
}

public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class ConfigFormatException : Exception
{
    public ConfigFormatException(string message) : base(message) { }
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = ConfigSchema.Current;
    public AppearanceSettings Appearance { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public ReadingSettings Reading { get; set; } = new();
    public OAuthClientSettings OAuth { get; set; } = new();
    public List<AccountConfig> Accounts { get; set; } = [];
    public bool DemoMode { get; set; }
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public Density Density { get; set; } = Density.Compact;
    public Dictionary<string, string> LightColors { get; set; } = [];
    public Dictionary<string, string> DarkColors { get; set; } = [];
    /// <summary>All-accounts mail view lists each account as its own group (default) or mixes them together.</summary>
    public bool GroupMailByAccount { get; set; } = true;
    public bool GroupTasksByFolder { get; set; }
    public BackgroundSettings Background { get; set; } = new();
}

/// <summary>How messages are shown in the reader (Settings → Mail). Local to this PC; never contains secrets.</summary>
public sealed class ReadingSettings
{
    /// <summary>Open the HTML version of a message when it has one (default), or the plain text.</summary>
    public bool PreferHtml { get; set; } = true;

    /// <summary>Remote images: ask before loading (default) or always load. There is no image proxy, so loading reveals the PC's address to the image host.</summary>
    public RemoteImagePolicy RemoteImages { get; set; } = RemoteImagePolicy.Ask;

    /// <summary>Senders (addresses, lower case) whose remote images always load.</summary>
    public List<string> TrustedSenders { get; set; } = [];
}

public enum RemoteImagePolicy { Ask, Always }

public enum BackgroundFit { Cover, Contain }

public enum BackgroundPosition { Center, Top, Bottom, Left, Right }

/// <summary>
/// The workspace background. <see cref="Image"/> is a built-in id from <see cref="BackgroundCatalog"/>,
/// <see cref="BackgroundCatalog.Custom"/> for the user's own picture (an app-managed copy at <see cref="CustomPath"/>),
/// or <see cref="BackgroundCatalog.None"/> for a plain color. Older config files without this section get the defaults.
/// </summary>
public sealed class BackgroundSettings
{
    public bool Enabled { get; set; } = true;
    public string Image { get; set; } = BackgroundCatalog.Default;
    public string? CustomPath { get; set; }
    public double Opacity { get; set; } = DefaultOpacity;
    public double Blur { get; set; } = DefaultBlur;
    public double Desaturation { get; set; } = DefaultDesaturation;
    public double Brightness { get; set; }
    public double Overlay { get; set; }
    public BackgroundFit Fit { get; set; } = BackgroundFit.Cover;
    public BackgroundPosition Position { get; set; } = BackgroundPosition.Center;

    /// <summary>
    /// Which set of defaults these values came from. Missing (null) in files written before 2026-11-21's change,
    /// when the defaults were 15% opacity, 8 px blur, and 35% desaturation.
    /// </summary>
    public int? Revision { get; set; }

    public const int CurrentRevision = 2;
    public const double DefaultOpacity = 0.85;
    public const double DefaultBlur = 2;
    public const double DefaultDesaturation = 0.1;
    public const double MaxOpacity = 1.0;
    public const double MaxBlur = 40;
    public const double MaxOverlay = 0.8;

    /// <summary>
    /// Clamps every value into its allowed range and replaces unknown image ids with the default. Settings that still
    /// hold the first, very muted defaults untouched are moved to the current defaults; values someone chose are kept.
    /// </summary>
    public BackgroundSettings Normalized()
    {
        static double Clamp(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        var image = BackgroundCatalog.IsKnown(Image) ? Image : BackgroundCatalog.Default;
        if (image == BackgroundCatalog.Custom && string.IsNullOrWhiteSpace(CustomPath))
            image = BackgroundCatalog.Default;
        var opacity = Opacity;
        var blur = Blur;
        var desaturation = Desaturation;
        if ((Revision ?? 1) < 2 && Math.Abs(opacity - 0.15) < 0.001 && Math.Abs(blur - 8) < 0.001 && Math.Abs(desaturation - 0.35) < 0.001)
        {
            opacity = DefaultOpacity;
            blur = DefaultBlur;
            desaturation = DefaultDesaturation;
            if (image == BackgroundCatalog.PreviousDefault)
                image = BackgroundCatalog.Default;
        }

        return new BackgroundSettings
        {
            Revision = CurrentRevision,
            Enabled = Enabled,
            Image = image,
            CustomPath = string.IsNullOrWhiteSpace(CustomPath) ? null : CustomPath,
            Opacity = Clamp(opacity, 0, MaxOpacity, DefaultOpacity),
            Blur = Clamp(blur, 0, MaxBlur, DefaultBlur),
            Desaturation = Clamp(desaturation, 0, 1, DefaultDesaturation),
            Brightness = Clamp(Brightness, -0.5, 0.5, 0),
            Overlay = Clamp(Overlay, 0, MaxOverlay, 0),
            Fit = Enum.IsDefined(Fit) ? Fit : BackgroundFit.Cover,
            Position = Enum.IsDefined(Position) ? Position : BackgroundPosition.Center,
        };
    }

    public BackgroundSettings Clone() => (BackgroundSettings)MemberwiseClone();
}

public static class BackgroundCatalog
{
    public const string None = "none";
    public const string Custom = "custom";
    public const string Default = "mountains";

    /// <summary>The first default picture; untouched settings that still name it move to <see cref="Default"/>.</summary>
    public const string PreviousDefault = "soft-blue";

    /// <summary>Built-in backgrounds: original artwork bundled with Alondra1 (design/backgrounds).</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> BuiltIn =
    [
        ("soft-blue", "Soft blue"),
        ("gray-geometric", "Gray geometric"),
        ("coast", "Muted coast"),
        ("mountains", "Misty mountains"),
        ("architecture", "Minimal architecture"),
        ("warm-neutral", "Warm neutral"),
        ("dark-blue", "Dark blue"),
        ("ocean", "Ocean"),
    ];

    public static bool IsKnown(string? id) => id is None or Custom || BuiltIn.Any(item => item.Id == id);
}

/// <summary>Checks a picture chosen as a background by its content, not its file name.</summary>
public static class BackgroundImageFile
{
    public const long MaxBytes = 25L * 1024 * 1024;

    public enum Kind { Unknown, Png, Jpeg, WebP }

    public static Kind Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return Kind.Png;
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return Kind.Jpeg;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return Kind.WebP;
        return Kind.Unknown;
    }

    /// <summary>Returns an error message, or null when the file can be used.</summary>
    public static string? Check(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return "The file does not exist.";
        if (info.Length == 0)
            return "The file is empty.";
        if (info.Length > MaxBytes)
            return $"The picture is larger than {MaxBytes / (1024 * 1024)} MB.";
        Span<byte> header = stackalloc byte[16];
        using var stream = info.OpenRead();
        var read = stream.Read(header);
        return Detect(header[..read]) == Kind.Unknown ? "Choose a PNG, JPEG, or WebP picture." : null;
    }
}

public sealed class LayoutSettings
{
    public Layout.DockEdge FoldersDock { get; set; } = Layout.DockEdge.Top;
    public Layout.DockEdge ReadingDock { get; set; } = Layout.DockEdge.Right;
    public double FolderBreadthSide { get; set; } = 152;
    public double FolderBreadthBand { get; set; } = 52;
    /// <summary>Reading pane width beside the list; 520 gives about 55/45 at the default window width.</summary>
    public double ReadingBreadthSide { get; set; } = 520;
    public double ReadingBreadthBand { get; set; } = 174;

    /// <summary>The account shown in Mail (display name), or null for All accounts. Restored at the next start.</summary>
    public string? MailAccount { get; set; }
}

public sealed class OAuthClientSettings
{
    public string MicrosoftClientId { get; set; } = "";
    public string MicrosoftTenant { get; set; } = "common";
    public string GoogleClientId { get; set; } = "";
    public bool Calendar { get; set; }
    public bool Tasks { get; set; }
    public bool Contacts { get; set; }
    public bool Notes { get; set; }
}

public sealed class AccountConfig
{
    public string Id { get; set; } = "";
    public AccountKind Kind { get; set; } = AccountKind.Manual;
    public AccountMode Mode { get; set; } = AccountMode.Online;
    public string DisplayName { get; set; } = "";
    public string Address { get; set; } = "";
    public IncomingSettings Incoming { get; set; } = new();
    public OutgoingSettings Outgoing { get; set; } = new();
    public bool PlaintextConfirmed { get; set; }
    public SyncPreferences Sync { get; set; } = new();
    public FolderMap Folders { get; set; } = new();
    public bool PopDeleteAfterDownload { get; set; }
    public bool PopDeleteConsented { get; set; }
    public bool PopUidlMissingDisclosed { get; set; }
}

public sealed class IncomingSettings
{
    public MailProtocol Protocol { get; set; } = MailProtocol.Imap;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public TransportSecurity Security { get; set; } = TransportSecurity.ImplicitTls;
    public string Username { get; set; } = "";
    public AuthenticationKind Authentication { get; set; } = AuthenticationKind.Password;
}

public sealed class OutgoingSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public TransportSecurity Security { get; set; } = TransportSecurity.RequiredStartTls;
    public string Username { get; set; } = "";
    public AuthenticationKind Authentication { get; set; } = AuthenticationKind.Password;
}

public sealed class SyncPreferences
{
    public List<string> FullBodyFolders { get; set; } = ["Inbox", "Sent"];
    public List<string> SelectedFolders { get; set; } = ["Inbox", "Sent"];
    public int? HistoryLimit { get; set; }
    public List<string> PinnedFolders { get; set; } = [];
}

public sealed class FolderMap
{
    /// <summary>The server's inbox id (IMAP "INBOX"); null until the first sync. POP3 always uses "Inbox".</summary>
    public string? Inbox { get; set; }
    public string? Sent { get; set; }
    public string? Drafts { get; set; }
    public string? Trash { get; set; }
    public string? Archive { get; set; }
}

public static class ProviderPresets
{
    public static void ApplyMicrosoft(AccountConfig account, MailProtocol protocol)
    {
        account.Kind = AccountKind.Microsoft365;
        account.Incoming.Protocol = protocol;
        account.Incoming.Host = "outlook.office365.com";
        account.Incoming.Port = protocol == MailProtocol.Imap ? 993 : 995;
        account.Incoming.Security = TransportSecurity.ImplicitTls;
        account.Incoming.Authentication = AuthenticationKind.OAuth;
        account.Outgoing.Host = "smtp.office365.com";
        account.Outgoing.Port = 587;
        account.Outgoing.Security = TransportSecurity.RequiredStartTls;
        account.Outgoing.Authentication = AuthenticationKind.OAuth;
    }

    public static void ApplyGmail(AccountConfig account, MailProtocol protocol)
    {
        account.Kind = AccountKind.Gmail;
        account.Incoming.Protocol = protocol;
        account.Incoming.Authentication = AuthenticationKind.OAuth;
        account.Incoming.Security = TransportSecurity.ImplicitTls;
        account.Outgoing.Host = "smtp.gmail.com";
        account.Outgoing.Port = 587;
        account.Outgoing.Security = TransportSecurity.RequiredStartTls;
        account.Outgoing.Authentication = AuthenticationKind.OAuth;
        if (protocol == MailProtocol.Imap)
        {
            account.Incoming.Host = "imap.gmail.com";
            account.Incoming.Port = 993;
        }
        else
        {
            account.Incoming.Host = "pop.gmail.com";
            account.Incoming.Port = 995;
        }
    }
}

public sealed record ConfigValidation(bool Ok, IReadOnlyList<string> Errors);

public sealed record ImportPlan(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Additions,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    AppConfig? Merged,
    AppConfig? Replaced);

public static class ConfigWorkflow
{
    public static AppConfig Clone(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        return JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options) ?? new AppConfig();
    }

    public static AppConfig Migrate(AppConfig config)
    {
        if (config.SchemaVersion > ConfigSchema.Current)
        {
            throw new ConfigFormatException(
                $"This file uses configuration version {config.SchemaVersion}. Alondra1 supports version {ConfigSchema.Current}. The current setup was not changed.");
        }

        if (config.SchemaVersion < ConfigSchema.Minimum)
        {
            throw new ConfigFormatException(
                $"Configuration version {config.SchemaVersion} is not a supported format.");
        }

        return config;
    }

    public static AppConfig Parse(string json)
    {
        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigFormatException("The configuration file could not be read. " + ex.Message);
        }

        if (config is null)
            throw new ConfigFormatException("The configuration file is empty.");
        return Migrate(config);
    }

    public static string ExportJson(AppConfig config)
    {
        var copy = Clone(config);
        foreach (var account in copy.Accounts)
        {
            if (UsesPlaintext(account))
                account.PlaintextConfirmed = false;
        }

        return JsonSerializer.Serialize(copy, ConfigJson.Options);
    }

    public static IReadOnlyList<string> FindSecretMaterial(string json)
    {
        var hits = new List<string>();
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, hits);
        return hits;
    }

    public static ConfigValidation Validate(AppConfig config)
    {
        var errors = new List<string>();
        try
        {
            Migrate(config);
        }
        catch (ConfigFormatException ex)
        {
            errors.Add(ex.Message);
            return new ConfigValidation(false, errors);
        }

        if (config.Layout.FolderBreadthSide is < 40 or > 2000
            || config.Layout.FolderBreadthBand is < 40 or > 2000
            || config.Layout.ReadingBreadthSide is < 40 or > 2000
            || config.Layout.ReadingBreadthBand is < 40 or > 2000)
        {
            errors.Add("Pane sizes must be between 40 and 2000.");
        }

        config.Reading ??= new ReadingSettings();
        config.Reading.TrustedSenders ??= [];
        if (config.Reading.TrustedSenders.Count > 1000)
            errors.Add("At most 1000 trusted senders are allowed.");
        foreach (var sender in config.Reading.TrustedSenders)
        {
            if (string.IsNullOrWhiteSpace(sender) || !sender.Contains('@') || sender.Contains(' ') || sender.Length > 320)
                errors.Add($"Trusted sender '{sender}' is not a valid address.");
        }

        if (!IsTenant(config.OAuth.MicrosoftTenant))
            errors.Add("Microsoft tenant must be common, organizations, consumers, or a directory id.");
        if (!IsClientId(config.OAuth.MicrosoftClientId, microsoft: true))
            errors.Add("Microsoft client id must be a public application id.");
        if (!IsClientId(config.OAuth.GoogleClientId, microsoft: false))
            errors.Add("Google client id must be a public client id.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in config.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id))
                errors.Add("An account is missing an id.");
            else if (!ids.Add(account.Id))
                errors.Add($"Duplicate account id {account.Id}.");

            if (string.IsNullOrWhiteSpace(account.Address) || !account.Address.Contains('@') || account.Address.Contains(' '))
                errors.Add($"Account address '{account.Address}' is not valid.");
            else if (!addresses.Add(account.Kind + "|" + account.Address.Trim()))
                errors.Add($"Duplicate {account.Kind} account {account.Address}.");

            if (string.IsNullOrWhiteSpace(account.DisplayName))
                errors.Add($"Account {account.Address} needs a display name.");

            ValidateEndpoint(errors, account.DisplayName + " incoming", account.Incoming.Host, account.Incoming.Port);
            ValidateEndpoint(errors, account.DisplayName + " outgoing", account.Outgoing.Host, account.Outgoing.Port);
            if (PortAdvisor.IncomingProblem(account.Incoming) is { } incomingProblem)
                errors.Add($"{account.DisplayName}: {incomingProblem}");
            if (PortAdvisor.OutgoingProblem(account.Outgoing) is { } outgoingProblem)
                errors.Add($"{account.DisplayName}: {outgoingProblem}");

            if (account.Kind is AccountKind.Microsoft365 or AccountKind.Gmail)
            {
                if (account.Incoming.Authentication != AuthenticationKind.OAuth || account.Outgoing.Authentication != AuthenticationKind.OAuth)
                    errors.Add($"{account.DisplayName} must use OAuth for Microsoft 365 or Gmail.");
            }

            if (UsesPlaintext(account) && UsesOAuth(account))
                errors.Add($"{account.DisplayName} cannot use OAuth over an unencrypted connection.");

            if (account.Sync.HistoryLimit is < 0)
                errors.Add($"{account.DisplayName} history limit cannot be negative.");
        }

        return new ConfigValidation(errors.Count == 0, errors);
    }

    public static ImportPlan Preview(AppConfig current, AppConfig incoming)
    {
        var validation = Validate(incoming);
        if (!validation.Ok)
            return new ImportPlan(false, validation.Errors, [], [], [], null, null);

        var additions = new List<string>();
        var changes = new List<string>();
        var warnings = new List<string>();
        var merged = Clone(current);
        var replacedAccounts = new List<AccountConfig>();

        foreach (var source in incoming.Accounts)
        {
            var match = FindMatch(merged.Accounts, source);
            if (match is null)
            {
                var added = CloneAccount(source);
                added.PlaintextConfirmed = false;
                merged.Accounts.Add(added);
                replacedAccounts.Add(CloneAccount(added));
                additions.Add($"Add {added.DisplayName} ({added.Address})");
                if (UsesPlaintext(added))
                    warnings.Add($"{added.DisplayName} uses an unencrypted server. Confirm that before connecting.");
                continue;
            }

            var beforeMode = match.Mode;
            var beforeId = match.Id;
            var plaintextAgain = UsesPlaintext(source);
            var endpointBecamePlain = BecamePlaintext(match, source);
            ApplyMutableFields(match, source);
            match.Id = beforeId;
            match.Mode = beforeMode;
            if (plaintextAgain || endpointBecamePlain)
                match.PlaintextConfirmed = false;
            changes.Add($"Update {match.DisplayName} ({match.Address})");
            if (source.Mode != beforeMode)
                warnings.Add($"Kept {match.DisplayName} in {beforeMode} mode. Import does not change an existing account mode or delete its saved mail.");
            if (plaintextAgain)
                warnings.Add($"Confirm the unencrypted connection for {match.DisplayName} again before connecting.");
            if (!string.Equals(source.Id, beforeId, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Kept the existing id for {match.Address} so saved mail stays with this identity.");

            var replaced = CloneAccount(match);
            replacedAccounts.Add(replaced);
        }

        var incomingKeys = incoming.Accounts.Select(MatchKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in current.Accounts)
        {
            if (!incomingKeys.Contains(MatchKey(existing)) && incoming.Accounts.All(item => !string.Equals(item.Id, existing.Id, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{existing.DisplayName} is not in the file. Replacement removes it from settings and leaves its saved mail on disk.");
        }

        if (incoming.Appearance is not null)
            merged.Appearance = Clone(incoming).Appearance;
        if (incoming.Layout is not null)
            merged.Layout = Clone(incoming).Layout;
        if (incoming.Reading is not null)
            merged.Reading = Clone(incoming).Reading;
        merged.OAuth = Clone(incoming).OAuth;
        merged.SchemaVersion = ConfigSchema.Current;

        var replacedConfig = Clone(incoming);
        replacedConfig.Accounts = replacedAccounts;
        replacedConfig.SchemaVersion = ConfigSchema.Current;
        foreach (var account in replacedConfig.Accounts)
        {
            var existing = FindMatch(current.Accounts, account);
            if (existing is not null)
            {
                account.Id = existing.Id;
                account.Mode = existing.Mode;
                if (UsesPlaintext(account))
                    account.PlaintextConfirmed = false;
            }
        }

        return new ImportPlan(true, [], additions, changes, warnings, merged, replacedConfig);
    }

    public static void ApplyTransportEdit(AccountConfig account, IncomingSettings incoming, OutgoingSettings outgoing)
    {
        var became = (account.Incoming.Security != TransportSecurity.None && incoming.Security == TransportSecurity.None)
            || (account.Outgoing.Security != TransportSecurity.None && outgoing.Security == TransportSecurity.None);
        account.Incoming = incoming;
        account.Outgoing = outgoing;
        if (became || UsesPlaintext(account))
        {
            if (became)
                account.PlaintextConfirmed = false;
        }
    }

    public static bool UsesPlaintext(AccountConfig account) =>
        account.Incoming.Security == TransportSecurity.None || account.Outgoing.Security == TransportSecurity.None;

    public static bool UsesOAuth(AccountConfig account) =>
        account.Incoming.Authentication == AuthenticationKind.OAuth || account.Outgoing.Authentication == AuthenticationKind.OAuth;

    private static void ApplyMutableFields(AccountConfig target, AccountConfig source)
    {
        target.Kind = source.Kind;
        target.DisplayName = source.DisplayName;
        target.Address = source.Address;
        target.Incoming = CloneAccount(source).Incoming;
        target.Outgoing = CloneAccount(source).Outgoing;
        target.Sync = CloneAccount(source).Sync;
        target.Folders = CloneAccount(source).Folders;
        target.PopDeleteAfterDownload = source.PopDeleteAfterDownload;
        target.PopDeleteConsented = source.PopDeleteConsented;
        target.PopUidlMissingDisclosed = source.PopUidlMissingDisclosed;
    }

    private static bool BecamePlaintext(AccountConfig before, AccountConfig after) =>
        (before.Incoming.Security != TransportSecurity.None && after.Incoming.Security == TransportSecurity.None)
        || (before.Outgoing.Security != TransportSecurity.None && after.Outgoing.Security == TransportSecurity.None)
        || !string.Equals(before.Incoming.Host, after.Incoming.Host, StringComparison.OrdinalIgnoreCase) && after.Incoming.Security == TransportSecurity.None
        || !string.Equals(before.Outgoing.Host, after.Outgoing.Host, StringComparison.OrdinalIgnoreCase) && after.Outgoing.Security == TransportSecurity.None;

    private static AccountConfig? FindMatch(IEnumerable<AccountConfig> accounts, AccountConfig source)
    {
        var byId = accounts.FirstOrDefault(item => string.Equals(item.Id, source.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;
        return accounts.FirstOrDefault(item =>
            item.Kind == source.Kind && string.Equals(item.Address, source.Address, StringComparison.OrdinalIgnoreCase));
    }

    private static string MatchKey(AccountConfig account) => account.Kind + "|" + account.Address.Trim();

    private static AccountConfig CloneAccount(AccountConfig account)
    {
        var json = JsonSerializer.Serialize(account, ConfigJson.Options);
        return JsonSerializer.Deserialize<AccountConfig>(json, ConfigJson.Options) ?? new AccountConfig();
    }

    private static void ValidateEndpoint(List<string> errors, string label, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains(' ') || host.Length > 253)
            errors.Add($"{label} host is not valid.");
        if (port is < 1 or > 65535)
            errors.Add($"{label} port is not valid.");
    }

    private static bool IsTenant(string tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant))
            return false;
        if (tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
            || tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)
            || tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase))
            return true;
        return Guid.TryParse(tenant, out _);
    }

    private static bool IsClientId(string value, bool microsoft)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (value.Contains("secret", StringComparison.OrdinalIgnoreCase) || value.Contains("private_key", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Length > 200 || value.Any(char.IsWhiteSpace))
            return false;
        if (microsoft)
            return Guid.TryParse(value, out _);
        return true;
    }

    private static void Walk(JsonElement element, List<string> hits)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsSecretName(property.Name))
                    hits.Add(property.Name);
                Walk(property.Value, hits);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Walk(item, hits);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? "";
            if (text.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal) || text.StartsWith("ya29.", StringComparison.Ordinal))
                hits.Add("embedded-secret");
        }
    }

    private static bool IsSecretName(string name)
    {
        var key = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("refreshToken", StringComparison.OrdinalIgnoreCase)
            || key.Contains("accessToken", StringComparison.OrdinalIgnoreCase)
            || key.Contains("clientSecret", StringComparison.OrdinalIgnoreCase);
    }
}
