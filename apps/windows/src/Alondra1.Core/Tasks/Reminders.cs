namespace Alondra1.Core.Tasks;

public sealed record DueReminder(TodoTask Task, TaskReminder Reminder, DateTimeOffset Instant);

/// <summary>
/// Decides which reminders are due. Reminders are local wall-clock times, converted with the given time zone at
/// evaluation time, so they follow the system time zone and daylight-saving changes.
/// </summary>
public static class ReminderSchedule
{
    /// <summary>
    /// Converts a wall-clock time to an instant. A time inside a spring-forward gap moves to the first valid minute after it;
    /// an ambiguous fall-back time uses its first occurrence.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 60)
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, offsets.Max());
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The moment this reminder should next be shown, or null when it never should.</summary>
    public static DateTimeOffset? NextShow(TodoTask task, TaskReminder reminder, TimeZoneInfo zone)
    {
        if (task.Completed || reminder.Dismissed)
            return null;
        if (reminder.SnoozedUntilUtc is { } snoozed)
            return snoozed;
        if (reminder.DeliveredFor == reminder.At)
            return null;
        return ToInstant(reminder.At, zone);
    }

    public static IReadOnlyList<DueReminder> Due(IEnumerable<TodoTask> tasks, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var due = new List<DueReminder>();
        foreach (var task in tasks)
        {
            foreach (var reminder in task.Reminders)
            {
                if (NextShow(task, reminder, zone) is { } when && when <= nowUtc)
                    due.Add(new DueReminder(task, reminder, when));
            }
        }

        return due.OrderBy(item => item.Instant).ToList();
    }

    public static DateTimeOffset? Earliest(IEnumerable<TodoTask> tasks, TimeZoneInfo zone) =>
        tasks.SelectMany(task => task.Reminders.Select(reminder => NextShow(task, reminder, zone)))
            .Where(value => value is not null)
            .Min();

    /// <summary>Moves reminders along with a recurring task's due date, keeping each reminder's wall-clock time.</summary>
    public static void ShiftForRecurrence(TodoTask task, DateOnly previousDue, DateOnly nextDue)
    {
        var days = nextDue.DayNumber - previousDue.DayNumber;
        foreach (var reminder in task.Reminders)
        {
            reminder.At = reminder.At.AddDays(days);
            reminder.DeliveredFor = null;
            reminder.Dismissed = false;
            reminder.SnoozedUntilUtc = null;
        }
    }
}
