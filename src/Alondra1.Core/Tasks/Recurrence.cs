using System.Globalization;

namespace Alondra1.Core.Tasks;

/// <summary>Deterministic recurrence on calendar dates. The same rule and date always give the same next date.</summary>
public static class Recurrence
{
    /// <summary>Returns the first occurrence strictly after <paramref name="current"/>, or null when the series has ended.</summary>
    public static DateOnly? Next(RecurrenceRule rule, DateOnly current)
    {
        var interval = Math.Max(1, rule.Interval);
        var anchor = rule.Anchor ?? current;
        DateOnly next;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Daily:
                next = current.AddDays(interval);
                break;
            case RecurrenceFrequency.Weekly when rule.DaysOfWeek.Count > 0:
                next = NextWeekly(rule.DaysOfWeek, interval, anchor, current);
                break;
            case RecurrenceFrequency.Weekly:
                next = current.AddDays(7 * interval);
                break;
            case RecurrenceFrequency.Monthly:
                next = NextByMonths(anchor, current, interval);
                break;
            case RecurrenceFrequency.Yearly:
                next = NextByMonths(anchor, current, interval * 12);
                break;
            default:
                return null;
        }

        return rule.Until is { } until && next > until ? null : next;
    }

    /// <summary>Lists occurrences in [from, to], starting with <paramref name="first"/>.</summary>
    public static IEnumerable<DateOnly> Occurrences(RecurrenceRule rule, DateOnly first, DateOnly from, DateOnly to, int max = 500)
    {
        DateOnly? cursor = first;
        var count = 0;
        while (cursor is { } date && date <= to && count < max)
        {
            if (date >= from)
            {
                yield return date;
                count++;
            }

            cursor = Next(rule, date);
        }
    }

    public static string Describe(RecurrenceRule? rule)
    {
        if (rule is null)
            return "";
        var every = Math.Max(1, rule.Interval);
        var unit = rule.Frequency switch
        {
            RecurrenceFrequency.Daily => every == 1 ? "Daily" : $"Every {every} days",
            RecurrenceFrequency.Weekly => every == 1 ? "Weekly" : $"Every {every} weeks",
            RecurrenceFrequency.Monthly => every == 1 ? "Monthly" : $"Every {every} months",
            _ => every == 1 ? "Yearly" : $"Every {every} years",
        };
        if (rule.Frequency == RecurrenceFrequency.Weekly && rule.DaysOfWeek.Count > 0)
        {
            var names = Ordered(rule.DaysOfWeek).Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day));
            unit += " on " + string.Join(", ", names);
        }

        if (rule.Until is { } until)
            unit += " until " + until.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return unit;
    }

    private static DateOnly NextWeekly(List<DayOfWeek> days, int interval, DateOnly anchor, DateOnly current)
    {
        var anchorWeek = WeekStart(anchor);
        for (var offset = 1; offset <= 7 * interval + 7; offset++)
        {
            var candidate = current.AddDays(offset);
            if (!days.Contains(candidate.DayOfWeek))
                continue;
            var weeks = (WeekStart(candidate).DayNumber - anchorWeek.DayNumber) / 7;
            if (weeks % interval == 0)
                return candidate;
        }

        return current.AddDays(7 * interval);
    }

    private static DateOnly NextByMonths(DateOnly anchor, DateOnly current, int step)
    {
        var months = (current.Year - anchor.Year) * 12 + current.Month - anchor.Month;
        var k = Math.Max(0, months - months % step);
        for (var guard = 0; guard < 4; guard++, k += step)
        {
            var candidate = anchor.AddMonths(k);
            if (candidate > current)
                return candidate;
        }

        return current.AddMonths(step);
    }

    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static IEnumerable<DayOfWeek> Ordered(IEnumerable<DayOfWeek> days) => days.Distinct().OrderBy(day => ((int)day + 6) % 7);
}
