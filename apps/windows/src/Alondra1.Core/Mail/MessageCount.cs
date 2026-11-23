namespace Alondra1.Core.Mail;

/// <summary>
/// The message count in the list header. It describes the folder and account scope and any search filter, never how
/// many rows fit on screen, so it always matches the list.
/// </summary>
public static class MessageCount
{
    /// <param name="shown">Rows in the list.</param>
    /// <param name="total">Messages in the folder and account scope before the search filter; null while unknown.</param>
    /// <param name="loading">True while the list is being loaded.</param>
    /// <param name="filtered">True when a search filter applies.</param>
    /// <param name="selected">Selected rows; a count of 2 or more is shown.</param>
    public static string Describe(int shown, int? total, bool loading, bool filtered, int selected = 0)
    {
        if (loading)
            return shown > 0 ? $"{shown} loaded" : "Loading…";
        string text;
        if (filtered)
        {
            var all = Math.Max(total ?? shown, shown);
            text = shown == 0 ? "No matches" : $"{shown} of {all} {Noun(all)}";
        }
        else
            text = shown == 0 ? "No messages" : $"{shown} {Noun(shown)}";

        return selected >= 2 ? $"{selected} selected · {text}" : text;
    }

    private static string Noun(int count) => count == 1 ? "message" : "messages";
}
