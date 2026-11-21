using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Alondra1.Core.Tasks;

namespace Alondra1.App.Tasks;

/// <summary>Prints a task view as a plain black-on-white checklist, independent of the screen theme.</summary>
public static class TodoPrinter
{
    public static void Print(Window owner, string title, IReadOnlyList<TodoTask> tasks, TaskBoard board, DateOnly today)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;
        var document = Build(title, tasks, board, today);
        document.PageWidth = dialog.PrintableAreaWidth;
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PagePadding = new Thickness(56);
        document.ColumnWidth = double.PositiveInfinity;
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "Alondra1 – " + title);
        _ = owner;
    }

    public static FlowDocument Build(string title, IReadOnlyList<TodoTask> tasks, TaskBoard board, DateOnly today)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = Brushes.Black, Background = Brushes.White };
        document.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        var open = tasks.Count(task => !task.Completed);
        document.Blocks.Add(new Paragraph(new Run($"{open} open · {tasks.Count - open} completed · printed {DateTime.Now:g}")) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12) });
        if (tasks.Count == 0)
            document.Blocks.Add(new Paragraph(new Run("No tasks.")));
        foreach (var task in tasks)
        {
            var line = new Paragraph { Margin = new Thickness(0, 4, 0, 0), KeepTogether = true };
            line.Inlines.Add(new Run(task.Completed ? "☑  " : "☐  ") { FontSize = 13 });
            var name = new Run(task.Title) { FontWeight = task.Important ? FontWeights.SemiBold : FontWeights.Normal };
            if (task.Completed)
                name.TextDecorations = TextDecorations.Strikethrough;
            line.Inlines.Add(name);
            if (task.Important)
                line.Inlines.Add(new Run("  ★"));
            var meta = new List<string>();
            if (task.DueDate is { } due)
                meta.Add((task.IsOverdue(today) ? "overdue, " : "due ") + TodoFormat.Date(due, today) + (task.DueTime is { } time ? " " + TodoFormat.Time(time) : ""));
            if (task.Recurrence is { } rule)
                meta.Add(Recurrence.Describe(rule).ToLowerInvariant());
            var list = board.ListName(task.ListId);
            if (list.Length > 0)
                meta.Add(list);
            if (meta.Count > 0)
                line.Inlines.Add(new Run("   " + string.Join(" · ", meta)) { Foreground = Brushes.DimGray, FontSize = 10 });
            document.Blocks.Add(line);
            foreach (var step in task.Subtasks)
                document.Blocks.Add(new Paragraph(new Run((step.Completed ? "☑  " : "☐  ") + step.Title)) { Margin = new Thickness(26, 1, 0, 0), Foreground = Brushes.DimGray });
            if (!string.IsNullOrWhiteSpace(task.Notes))
                document.Blocks.Add(new Paragraph(new Run(task.Notes.Trim())) { Margin = new Thickness(26, 2, 0, 0), FontStyle = FontStyles.Italic, Foreground = Brushes.DimGray });
        }

        return document;
    }
}
