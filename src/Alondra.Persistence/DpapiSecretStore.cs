using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alondra.Core.Ports;
using Alondra.Core.Security;

namespace Alondra.Persistence;

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("alondra-v1");
    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public Task<SecretBundle> LoadAsync(string accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FilePath(accountId);
        if (!File.Exists(path))
            return Task.FromResult(new SecretBundle());
        var protectedBytes = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var bundle = JsonSerializer.Deserialize<SecretBundle>(plain) ?? new SecretBundle();
        return Task.FromResult(bundle);
    }

    public Task SaveAsync(string accountId, SecretBundle bundle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plain = JsonSerializer.SerializeToUtf8Bytes(bundle);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        var path = FilePath(accountId);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FilePath(accountId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string FilePath(string accountId)
    {
        var safe = string.Concat(accountId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return Path.Combine(_directory, safe + ".bin");
    }
}
