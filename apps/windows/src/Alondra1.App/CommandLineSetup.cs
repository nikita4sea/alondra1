using System.IO;
using Alondra1.App.ViewModels;
using Alondra1.Core.Config;

namespace Alondra1.App;

/// <summary>
/// Unattended setup used by the milestone reinstall check (apps/windows/scripts/reinstall-local.ps1):
///   --import-config=&lt;file&gt;     imports a configuration in Replace mode through the same validation and preview as
///                                Settings → Import configuration. Secrets are never imported this way.
///   --import-passwords=&lt;file&gt;  reads mail passwords from a local test file (USER1=… PASSWORD1=… lines, the format of
///                                ALONDRA1_LIVE_MAIL_FILE) and saves them with DPAPI for the accounts whose address matches.
/// Results go to logs\setup.log. Passwords are never written to config.json, logs, or the console.
/// </summary>
public static class CommandLineSetup
{
    public static bool Requested(string[] args) =>
        args.Any(arg => arg.StartsWith("--import-config=", StringComparison.Ordinal) || arg.StartsWith("--import-passwords=", StringComparison.Ordinal));

    /// <summary>Runs the requested steps. Returns 0 on success, 2 for an invalid configuration, 3 for a password problem.</summary>
    public static async Task<int> RunAsync(AppServices services, string[] args)
    {
        var log = new List<string>();
        var code = 0;
        try
        {
            if (Value(args, "--import-config=") is { } configPath)
                code = ImportConfig(services, configPath, log);
            if (code == 0 && Value(args, "--import-passwords=") is { } passwordPath)
                code = await ImportPasswordsAsync(services, passwordPath, log).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigFormatException)
        {
            log.Add("error: " + ex.Message);
            code = code == 0 ? 2 : code;
        }

        Write(services, log, code);
        return code;
    }

    private static int ImportConfig(AppServices services, string path, List<string> log)
    {
        var json = File.ReadAllText(path);
        var secrets = ConfigWorkflow.FindSecretMaterial(json);
        if (secrets.Count > 0)
        {
            log.Add("import-config: refused, the file contains secret fields: " + string.Join(", ", secrets));
            return 2;
        }

        var incoming = ConfigWorkflow.Parse(json);
        var plan = ConfigWorkflow.Preview(services.Config, incoming);
        if (!plan.Ok || plan.Replaced is null)
        {
            log.Add("import-config: invalid, nothing changed");
            log.AddRange(plan.Errors.Select(error => "  error: " + error));
            return 2;
        }

        services.ApplyImported(plan.Replaced);
        log.Add($"import-config: replaced; {services.Config.Accounts.Count} account(s)");
        log.AddRange(plan.Additions.Select(item => "  " + item));
        log.AddRange(plan.Changes.Select(item => "  " + item));
        log.AddRange(plan.Warnings.Select(item => "  warning: " + item));
        return 0;
    }

    private static async Task<int> ImportPasswordsAsync(AppServices services, string path, List<string> log)
    {
        var pairs = TestAccountFile.ParsePasswords(File.ReadAllLines(path));
        var saved = 0;
        foreach (var (address, password) in pairs)
        {
            var matches = services.Config.Accounts
                .Where(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(account.Incoming.Username, address, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                log.Add($"import-passwords: no account for {address}");
                continue;
            }

            foreach (var account in matches)
            {
                var bundle = await services.Secrets.LoadAsync(account.Id, CancellationToken.None).ConfigureAwait(true);
                bundle.Password = password;
                await services.Secrets.SaveAsync(account.Id, bundle, CancellationToken.None).ConfigureAwait(true);
                saved++;
                log.Add($"import-passwords: saved for {account.DisplayName}");
            }
        }

        var missing = services.Config.Accounts.Count(account => account.Kind == AccountKind.Manual
            && !pairs.Any(pair => string.Equals(pair.Address, account.Address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Address, account.Incoming.Username, StringComparison.OrdinalIgnoreCase)));
        if (missing > 0)
            log.Add($"import-passwords: {missing} manual account(s) have no password in the file");
        return saved > 0 ? 0 : 3;
    }

    private static string? Value(string[] args, string prefix) =>
        args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim('"');

    private static void Write(AppServices services, List<string> log, int code)
    {
        try
        {
            var directory = Path.Combine(services.Home, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllLines(Path.Combine(directory, "setup.log"),
                log.Prepend($"{DateTimeOffset.Now:O} setup").Append($"exit {code}"));
        }
        catch (IOException)
        {
        }
    }
}
