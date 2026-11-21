using Alondra.Core.Tasks;

namespace Alondra.Persistence;

/// <summary>
/// Copies files attached to tasks into Alondra's data folder (task-files\&lt;task id&gt;\&lt;random name&gt;) so a task never
/// depends on the original file staying where it was. Uses the same naming as mail attachment files.
/// </summary>
public sealed class TaskFileStore
{
    public const long MaxFileBytes = 512L * 1024 * 1024;
    private readonly AttachmentFiles _files;

    public TaskFileStore(string root) => _files = new AttachmentFiles(root);

    public string Root => _files.Root;

    public TaskAttachment Import(string taskId, string sourcePath, DateTimeOffset nowUtc)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The file no longer exists.", sourcePath);
        if (info.Length > MaxFileBytes)
            throw new IOException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        var (relative, full) = _files.Reserve(SafeSegment(taskId), info.Name);
        File.Copy(info.FullName, full, overwrite: false);
        return new TaskAttachment { FileName = info.Name, RelativePath = relative, Size = info.Length, AddedUtc = nowUtc };
    }

    /// <summary>Returns the full path for a stored reference, or null when the reference points outside the folder or is missing.</summary>
    public string? Resolve(TaskAttachment attachment)
    {
        if (!TodoTransfer.IsSafeRelativePath(attachment.RelativePath))
            return null;
        var root = Path.GetFullPath(_files.Root);
        var full = Path.GetFullPath(Path.Combine(root, attachment.RelativePath));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Deletes stored files that no task refers to any more. Run once at startup, not on every change, so undo can restore attachments.</summary>
    public int RemoveOrphans(IEnumerable<TodoTask> tasks)
    {
        if (!Directory.Exists(_files.Root))
            return 0;
        var root = Path.GetFullPath(_files.Root);
        var referenced = tasks.SelectMany(task => task.Attachments)
            .Where(item => TodoTransfer.IsSafeRelativePath(item.RelativePath))
            .Select(item => Path.GetFullPath(Path.Combine(root, item.RelativePath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (referenced.Contains(Path.GetFullPath(file)))
                continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        return removed;
    }

    private static string SafeSegment(string value)
    {
        var clean = new string(value.Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length == 0 ? "task" : clean;
    }
}
