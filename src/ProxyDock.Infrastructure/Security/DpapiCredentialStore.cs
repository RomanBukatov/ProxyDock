using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.Infrastructure.Security;

/// <summary>
/// Защищённое хранение паролей прокси через Windows DPAPI (ProtectedData / CurrentUser).
/// Секреты никогда не записываются в JSON-конфигурацию открытым текстом.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProxyDock.DPAPI.v1");
    private readonly IPathProvider _paths;

    public DpapiCredentialStore(IPathProvider paths)
    {
        _paths = paths;
    }

    public async Task SaveAsync(Guid proxyId, ProxyCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Directory.CreateDirectory(_paths.SecretsDirectory);

        var json = JsonSerializer.Serialize(credentials);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        Array.Clear(plainBytes);

        var targetPath = GetSecretPath(proxyId);
        var tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllBytesAsync(tempPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    public async Task<ProxyCredentials?> LoadAsync(Guid proxyId, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(proxyId);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<ProxyCredentials>(json);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }

    public Task DeleteAsync(Guid proxyId, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(proxyId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(Guid proxyId, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(GetSecretPath(proxyId)));

    public string? ProtectForPowerShell(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        // ConvertTo-SecureString в Windows PowerShell ожидает hex без дефисов от ProtectedData(Unicode, entropy=null, CurrentUser).
        var plainBytes = Encoding.Unicode.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Convert.ToHexString(protectedBytes);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }

    private string GetSecretPath(Guid proxyId)
        => Path.Combine(_paths.SecretsDirectory, $"{proxyId:N}.bin");
}
