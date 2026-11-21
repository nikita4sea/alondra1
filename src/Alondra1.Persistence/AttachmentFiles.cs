namespace Alondra1.Persistence;

public sealed class AttachmentFiles
{
    private readonly string _root;

    public AttachmentFiles(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string Root => _root;

    public (string Relative, string Full) Reserve(string accountId, string originalName)
    {
        var extension = Path.GetExtension(originalName);
        if (extension.Length > 8 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.'))
            extension = "";
        var name = Guid.NewGuid().ToString("n") + extension.ToLowerInvariant();
        var relative = Path.Combine(accountId, name);
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return (relative, full);
    }

    public string Resolve(string relative) => Path.Combine(_root, relative);

    public long TotalBytes()
    {
        if (!Directory.Exists(_root))
            return 0;
        return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    }

    public void DeleteAccount(string accountId)
    {
        var path = Path.Combine(_root, accountId);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
