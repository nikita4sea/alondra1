using Alondra.Core.Tasks;

namespace Alondra.App.Tasks;

/// <summary>Sample lists and tasks for --demo, which runs in a throwaway data folder.</summary>
public static class TodoSamples
{
    public static async Task SeedAsync(TaskBoard board)
    {
        var today = board.Today;
        var inbox = board.DefaultList.Id;
        var work = await board.AddGroupAsync("Work");
        var launch = await board.AddListAsync("🚀 Launch", work.Id, "#2F6FDE");
        var reviews = await board.AddListAsync("Reviews", work.Id, "#7A4FD6");
        var home = await board.AddListAsync("🛒 Groceries", null, "#2E8B3E");

        await board.AddTaskAsync(inbox, "Confirm the handoff owner", task => { task.MyDayDate = today; task.Important = true; task.DueDate = today; task.DueTime = new TimeOnly(16, 0); });
        await board.AddTaskAsync(inbox, "Review the November invoice", task => { task.DueDate = today.AddDays(-2); task.Notes = "Check the hosting line item against the contract."; });
        await board.AddTaskAsync(inbox, "Water the plants", task => { task.DueDate = today; task.Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly }; });
        await board.AddTaskAsync(launch.Id, "Send the workshop agenda", task =>
        {
            task.DueDate = today.AddDays(2);
            task.MyDayDate = today;
            SubtaskEdits.Add(task, "Draft agenda");
            SubtaskEdits.Add(task, "Book the room");
            SubtaskEdits.Add(task, "Share with the team");
            task.Subtasks[0].Completed = true;
        });
        await board.AddTaskAsync(launch.Id, "Update release notes", task => task.DueDate = today.AddDays(5));
        await board.AddTaskAsync(launch.Id, "Pick launch date", task => task.Important = true);
        await board.AddTaskAsync(reviews.Id, "Quarterly review prep", task => { task.StartDate = today.AddDays(1); task.DueDate = today.AddDays(9); });
        await board.AddTaskAsync(home.Id, "Oat milk");
        await board.AddTaskAsync(home.Id, "Coffee beans");
        var done = await board.AddTaskAsync(home.Id, "Bread");
        await board.SetCompletedAsync([done.Id], true);
    }
}
