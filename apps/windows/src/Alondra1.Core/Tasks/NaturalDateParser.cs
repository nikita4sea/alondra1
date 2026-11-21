using System.Globalization;
using System.Text.RegularExpressions;

namespace Alondra1.Core.Tasks;

public sealed class ParsedTaskInput
{
    public string Title { get; init; } = "";
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    public bool Important { get; init; }

    public bool HasSchedule => DueDate is not null || DueTime is not null || Recurrence is not null;
}

/// <summary>
/// Offline, English-keyword parser for quick add: "Pay rent every month on the 1st", "Call Sam tomorrow at 3pm",
/// "Report next friday !". It never contacts a service.
/// </summary>
public static partial class NaturalDateParser
{
    private static readonly string[] DayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private static readonly string[] MonthNames = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    public static ParsedTaskInput Parse(string input, DateTime now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var today = DateOnly.FromDateTime(now);
        var text = " " + (input ?? "").Trim() + " ";
        DateOnly? date = null;
        TimeOnly? time = null;
        RecurrenceRule? rule = null;
        var important = false;

        if (Important().Match(text) is { Success: true } bang)
        {
            important = true;
            text = Cut(text, bang);
        }

        if (Every().Match(text) is { Success: true } every)
        {
            rule = ReadRecurrence(every);
            text = Cut(text, every);
        }

        if (Time().Match(text) is { Success: true } clock && ReadTime(clock) is { } parsedTime)
        {
            time = parsedTime;
            text = Cut(text, clock);
        }

        foreach (var reader in DateReaders)
        {
            var match = reader.Pattern.Match(text);
            if (!match.Success)
                continue;
            var value = reader.Read(match, today, culture);
            if (value is null)
                continue;
            date = value.Value.Date;
            time ??= value.Value.Time;
            text = Cut(text, match);
            break;
        }

        var title = Spaces().Replace(text, " ").Trim().Trim(',', ';', '-').Trim();
        if (title.Length == 0)
            return new ParsedTaskInput { Title = (input ?? "").Trim() };

        if (rule is not null)
        {
            date ??= rule.Frequency == RecurrenceFrequency.Weekly && rule.DaysOfWeek.Count > 0
                ? NextOnOrAfter(today, rule.DaysOfWeek)
                : today;
            rule.Anchor = date;
        }
        else if (time is not null && date is null)
        {
            date = TimeOnly.FromDateTime(now) < time ? today : today.AddDays(1);
        }

        return new ParsedTaskInput { Title = title, DueDate = date, DueTime = time, Recurrence = rule, Important = important };
    }

    private static string Cut(string text, Match match) => text.Remove(match.Index, match.Length).Insert(match.Index, " ");

    private static RecurrenceRule ReadRecurrence(Match match)
    {
        var word = match.Groups["word"].Value.ToLowerInvariant();
        var interval = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
        if (match.Groups["other"].Success)
            interval = 2;
        interval = Math.Clamp(interval, 1, 999);
        var rule = new RecurrenceRule { Interval = interval };
        switch (word)
        {
            case "daily" or "day" or "days":
                rule.Frequency = RecurrenceFrequency.Daily;
                break;
            case "weekly" or "week" or "weeks":
                rule.Frequency = RecurrenceFrequency.Weekly;
                break;
            case "monthly" or "month" or "months":
                rule.Frequency = RecurrenceFrequency.Monthly;
                break;
            case "yearly" or "annually" or "year" or "years":
                rule.Frequency = RecurrenceFrequency.Yearly;
                break;
            case "weekday" or "weekdays":
                rule.Frequency = RecurrenceFrequency.Weekly;
                rule.DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
                break;
            default:
                rule.Frequency = RecurrenceFrequency.Weekly;
                if (DayIndex(word) is { } day)
                    rule.DaysOfWeek = [day];
                break;
        }

        return rule;
    }

    private static TimeOnly? ReadTime(Match match)
    {
        if (match.Groups["noon"].Success)
            return match.Groups["noon"].Value.Equals("noon", StringComparison.OrdinalIgnoreCase) ? new TimeOnly(12, 0) : new TimeOnly(0, 0);
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["ap"].Value.ToLowerInvariant();
        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12)
                return null;
            hour = hour % 12 + (meridiem.StartsWith('p') ? 12 : 0);
        }
        else if (!match.Groups["m"].Success)
        {
            return null;
        }

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    private sealed record DateReader(Regex Pattern, Func<Match, DateOnly, CultureInfo, (DateOnly Date, TimeOnly? Time)?> Read);

    private static readonly DateReader[] DateReaders =
    [
        new(new Regex(@"\s(?:due\s+|by\s+)?(?:the\s+)?day\s+after\s+tomorrow\s", RegexOptions.IgnoreCase), (_, today, _) => (today.AddDays(2), null)),
        new(new Regex(@"\s(?:due\s+|by\s+)?(?<w>today|tonight|tomorrow|tmrw|tmr)\s", RegexOptions.IgnoreCase), (m, today, _) =>
            m.Groups["w"].Value.ToLowerInvariant() switch
            {
                "today" => (today, null),
                "tonight" => (today, new TimeOnly(20, 0)),
                _ => (today.AddDays(1), null),
            }),
        new(new Regex(@"\s(?:due\s+|by\s+)?in\s+(?<n>\d{1,3})\s+(?<u>days?|weeks?|months?|years?)\s", RegexOptions.IgnoreCase), (m, today, _) =>
        {
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            return m.Groups["u"].Value.ToLowerInvariant()[0] switch
            {
                'd' => (today.AddDays(n), null),
                'w' => (today.AddDays(7 * n), null),
                'm' => (today.AddMonths(n), null),
                _ => (today.AddYears(n), null),
            };
        }),
        new(new Regex(@"\s(?:due\s+|by\s+)?next\s+(?<u>week|month|year)\s", RegexOptions.IgnoreCase), (m, today, _) =>
            m.Groups["u"].Value.ToLowerInvariant() switch
            {
                "week" => (Recurrence.WeekStart(today).AddDays(7), null),
                "month" => (new DateOnly(today.Year, today.Month, 1).AddMonths(1), null),
                _ => (new DateOnly(today.Year + 1, 1, 1), null),
            }),
        new(new Regex(@"\s(?:due\s+|by\s+)?(?:this\s+)?weekend\s", RegexOptions.IgnoreCase), (_, today, _) =>
            (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? today : NextOnOrAfter(today, [DayOfWeek.Saturday]), null)),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+)?(?<next>next\s+|this\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s", RegexOptions.IgnoreCase), ReadWeekday),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+|(?<next>next\s+|this\s+))(?<d>mon|tues?|wed|thur?s?|fri|sat|sun)\s", RegexOptions.IgnoreCase), ReadWeekday),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+)?(?<y>\d{4})-(?<mo>\d{1,2})-(?<d>\d{1,2})\s", RegexOptions.IgnoreCase), (m, _, _) =>
            Build(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture)) is { } date ? (date, null) : null),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+)?(?<mn>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y>\d{4}))?\s", RegexOptions.IgnoreCase), (m, today, _) =>
            FromParts(today, MonthIndex(m.Groups["mn"].Value), m.Groups["d"].Value, m.Groups["y"].Value)),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+)?(?:the\s+)?(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<mn>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?(?:,?\s+(?<y>\d{4}))?\s", RegexOptions.IgnoreCase), (m, today, _) =>
            FromParts(today, MonthIndex(m.Groups["mn"].Value), m.Groups["d"].Value, m.Groups["y"].Value)),
        new(new Regex(@"\s(?:due\s+|by\s+|on\s+)(?<a>\d{1,2})[/.](?<b>\d{1,2})(?:[/.](?<y>\d{2,4}))?\s", RegexOptions.IgnoreCase), (m, today, culture) =>
        {
            var dayFirst = culture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('d');
            var a = m.Groups["a"].Value;
            var b = m.Groups["b"].Value;
            var month = int.Parse(dayFirst ? b : a, CultureInfo.InvariantCulture);
            var year = m.Groups["y"].Value;
            if (year.Length == 2)
                year = "20" + year;
            return FromParts(today, month, dayFirst ? a : b, year);
        }),
        new(new Regex(@"\s(?:due\s+|by\s+)?on\s+the\s+(?<d>\d{1,2})(?:st|nd|rd|th)\s", RegexOptions.IgnoreCase), (m, today, _) =>
        {
            var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            var candidate = Build(today.Year, today.Month, day);
            if (candidate is { } date && date >= today)
                return (date, null);
            var next = today.AddMonths(1);
            return Build(next.Year, next.Month, day) is { } later ? (later, null) : null;
        }),
    ];

    private static (DateOnly, TimeOnly?)? ReadWeekday(Match m, DateOnly today, CultureInfo culture)
    {
        if (DayIndex(m.Groups["d"].Value) is not { } day)
            return null;
        if (m.Groups["next"].Value.Trim().Equals("next", StringComparison.OrdinalIgnoreCase))
            return (NextOnOrAfter(Recurrence.WeekStart(today).AddDays(7), [day]), null);
        return (NextOnOrAfter(today.AddDays(1), [day]), null);
    }

    private static (DateOnly, TimeOnly?)? FromParts(DateOnly today, int month, string dayText, string yearText)
    {
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        if (yearText.Length > 0)
            return Build(int.Parse(yearText, CultureInfo.InvariantCulture), month, day) is { } fixedDate ? (fixedDate, null) : null;
        if (Build(today.Year, month, day) is not { } date)
            return null;
        if (date < today)
            date = Build(today.Year + 1, month, day) ?? date;
        return (date, null);
    }

    private static DateOnly? Build(int year, int month, int day) =>
        year is >= 1900 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    private static DateOnly NextOnOrAfter(DateOnly start, IReadOnlyCollection<DayOfWeek> days)
    {
        for (var offset = 0; offset < 7; offset++)
        {
            var candidate = start.AddDays(offset);
            if (days.Contains(candidate.DayOfWeek))
                return candidate;
        }

        return start;
    }

    private static DayOfWeek? DayIndex(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Length < 3)
            return null;
        for (var i = 0; i < DayNames.Length; i++)
        {
            if (DayNames[i].StartsWith(lower[..3], StringComparison.Ordinal))
                return (DayOfWeek)i;
        }

        return null;
    }

    private static int MonthIndex(string text)
    {
        var lower = text.ToLowerInvariant();
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (MonthNames[i].StartsWith(lower[..3], StringComparison.Ordinal))
                return i + 1;
        }

        return 1;
    }

    [GeneratedRegex(@"\s(?:!|!important|!!)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Important();

    [GeneratedRegex(@"\s(?:(?:repeat(?:s|ing)?\s+)?every\s+(?:(?<other>other)\s+|(?<n>\d{1,3})\s+)?(?<word>days?|weeks?|months?|years?|weekdays?|mon(?:day)?|tue(?:sday)?|wed(?:nesday)?|thu(?:rsday)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?)|(?<word>daily|weekly|monthly|yearly|annually))\s", RegexOptions.IgnoreCase)]
    private static partial Regex Every();

    [GeneratedRegex(@"\s(?:at\s+)?(?:(?<noon>noon|midnight)|(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)|(?:at\s+)(?<h>\d{1,2}):(?<m>\d{2})|(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d))\s", RegexOptions.IgnoreCase)]
    private static partial Regex Time();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
