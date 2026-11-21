using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using Alondra.Core.Tasks;
using Alondra.Persistence;
using Microsoft.Win32;

namespace Alondra.App.Tasks;

/// <summary>Import, export, backup, and restore. Every operation reads and writes local files only.</summary>
public static class TodoDataActions
{
    public const int KeepAutomaticBackups = 7;

    public static string BackupFolder => Path.Combine(Path.GetDirectoryName(App.Services.TaskStore?.DatabasePath ?? "") ?? "", "backups");

    public static void ExportJson(Window owner, TodoViewModel model)
    {
        var path = Save(owner, "JSON|*.json", "alondra-tasks.json");
        if (path is null)
            return;
        Write(model, path, () => TodoTransfer.ExportJson(model.Board!.Snapshot(), model.Board.Now), "Exported tasks and lists as JSON");
    }

    public static void ExportCsv(Window owner, TodoViewModel model)
    {
        var path = Save(owner, "CSV|*.csv", "alondra-tasks.csv");
        if (path is null)
            return;
        Write(model, path, () => TodoTransfer.ExportCsv(model.Board!.Snapshot(), CultureInfo.CurrentCulture), "Exported tasks as CSV", bom: true);
    }

    public static void ExportIcs(Window owner, TodoViewModel model)
    {
        var path = Save(owner, "iCalendar|*.ics", "alondra-tasks.ics");
        if (path is null)
            return;
        Write(model, path, () => TodoTransfer.ExportIcs(model.Board!.Snapshot(), model.Board.Now), "Exported tasks as iCalendar");
    }

    public static async Task ImportJsonAsync(Window owner, TodoViewModel model)
    {
        var dialog = new OpenFileDialog { Filter = "Alondra to-do export|*.json|All files|*.*", Title = "Import tasks" };
        if (dialog.ShowDialog(owner) != true)
            return;
        string json;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 200L * 1024 * 1024)
            {
                model.Error("The file is larger than 200 MB and was not imported.");
                return;
            }

            json = await File.ReadAllTextAsync(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            model.Error("The file could not be read: " + ex.Message);
            return;
        }

        var plan = TodoTransfer.PrepareImport(json, model.Board!.Snapshot());
        if (!plan.Ok)
        {
            Dialogs.Alert(owner, "Nothing was imported.\n\n" + string.Join("\n", plan.Errors), "Import tasks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = new StringBuilder($"Add {plan.Summary} from this file?\n\nImported items get new identifiers, so your current tasks and lists are not changed.");
        if (plan.Warnings.Count > 0)
            message.Append("\n\n").Append(string.Join("\n", plan.Warnings));
        if (!TodoDialogs.Confirm(owner, "Import tasks", message.ToString(), "Import"))
            return;
        try
        {
            await model.Board.ImportAsync(plan.Data!);
            model.Toast($"Imported {plan.Summary}", canUndo: true);
        }
        catch (Exception ex) when (ex is TodoStorageException or InvalidOperationException)
        {
            model.Error(ex.Message);
        }
    }

    public static async Task BackupAsync(Window owner, TodoViewModel model)
    {
        Directory.CreateDirectory(BackupFolder);
        var dialog = new SaveFileDialog
        {
            Filter = "Alondra to-do backup|*.atodo|All files|*.*",
            FileName = $"alondra-tasks-{DateTime.Now:yyyyMMdd-HHmm}.atodo",
            InitialDirectory = BackupFolder,
        };
        if (dialog.ShowDialog(owner) != true)
            return;
        try
        {
            await App.Services.TaskStore!.BackupToAsync(dialog.FileName);
            model.Toast("Backup saved to " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            model.Error("The backup could not be written: " + ex.Message);
        }
    }

    public static async Task RestoreAsync(Window owner, TodoViewModel model)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Alondra to-do backup|*.atodo;*.db|All files|*.*",
            InitialDirectory = Directory.Exists(BackupFolder) ? BackupFolder : null,
            Title = "Restore tasks from a backup",
        };
        if (dialog.ShowDialog(owner) != true)
            return;
        TodoData data;
        try
        {
            data = await SqliteTaskStore.ReadBackupAsync(dialog.FileName);
        }
        catch (Exception ex) when (ex is TodoStorageException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Alert(owner, "Nothing was restored.\n\n" + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var current = model.Board!.Tasks.Count;
        var message = $"Replace your current {current} task{(current == 1 ? "" : "s")} and {model.Board.Lists.Count} lists with the backup's {data.Tasks.Count} tasks and {data.Lists.Count} lists?\n\n"
                      + "A safety copy of your current tasks is saved in the backup folder first, so you can switch back.";
        if (!TodoDialogs.Confirm(owner, "Restore from backup", message, "Replace with backup"))
            return;
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var safety = Path.Combine(BackupFolder, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.atodo");
            await App.Services.TaskStore!.BackupToAsync(safety);
            await model.Board.ReplaceAllAsync(data);
            model.Select("smart:All");
            model.Toast($"Restored {data.Tasks.Count} tasks. Your previous tasks were saved as {Path.GetFileName(safety)}.");
        }
        catch (Exception ex) when (ex is TodoStorageException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            model.Error("Restore stopped and your tasks were not changed: " + ex.Message);
        }
    }

    public static void OpenBackupFolder(TodoViewModel model)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            Process.Start(new ProcessStartInfo(BackupFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            model.Error(ex.Message);
        }
    }

    /// <summary>Keeps one automatic backup per day for the last week. Runs once at startup.</summary>
    public static async Task AutomaticBackupAsync(SqliteTaskStore store)
    {
        try
        {
            if (await store.CountTasksAsync() == 0)
                return;
            var folder = Path.Combine(Path.GetDirectoryName(store.DatabasePath) ?? "", "backups");
            Directory.CreateDirectory(folder);
            var today = Path.Combine(folder, $"auto-{DateTime.Now:yyyyMMdd}.atodo");
            if (!File.Exists(today))
                await store.BackupToAsync(today);
            foreach (var old in Directory.EnumerateFiles(folder, "auto-*.atodo").OrderByDescending(file => file, StringComparer.Ordinal).Skip(KeepAutomaticBackups))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Debug.WriteLine("Automatic to-do backup skipped: " + ex.Message);
        }
    }

    private static string? Save(Window owner, string filter, string name)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static void Write(TodoViewModel model, string path, Func<string> content, string done, bool bom = false)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content(), new UTF8Encoding(bom));
            File.Move(temporary, path, overwrite: true);
            model.Toast(done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            model.Error("The file could not be written: " + ex.Message);
        }
    }
}
