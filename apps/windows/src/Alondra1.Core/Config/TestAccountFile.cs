namespace Alondra1.Core.Config;

/// <summary>
/// The local, git-ignored test-account file (the format of ALONDRA1_LIVE_MAIL_FILE):
/// <c>USER1=… PASSWORD1=… USER2=… PASSWORD2=… IMAP=host:port POP3=host:port SMTP=host:port</c>, one per line.
/// Used only by the milestone reinstall check; values are never logged.
/// </summary>
public static class TestAccountFile
{
    /// <summary>The USERn / PASSWORDn pairs, in file order. Pairs with an empty address or password are skipped.</summary>
    public static IReadOnlyList<(string Address, string Password)> ParsePasswords(IEnumerable<string> lines)
    {
        var values = Parse(lines);
        var result = new List<(string, string)>();
        foreach (var (key, address) in values)
        {
            if (!key.StartsWith("USER", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = key["USER".Length..];
            if (values.TryGetValue("PASSWORD" + suffix, out var password) && !string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(password))
                result.Add((address, password));
        }

        return result;
    }

    /// <summary>KEY=value lines (case-insensitive keys; the last value wins; other lines are ignored).</summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
                continue;
            values[parts[0].Trim()] = parts[1].Trim();
        }

        return values;
    }
}
