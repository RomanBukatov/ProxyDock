using ProxyDock.Core.Models;

namespace ProxyDock.Core.Interfaces;

public interface IPathProvider
{
    string RootDirectory { get; }
    string ConfigDirectory { get; }
    string LogsDirectory { get; }
    string BackupsDirectory { get; }
    string SecretsDirectory { get; }
    string RuntimeDirectory { get; }

    string ApplicationsFilePath { get; }
    string ProxiesFilePath { get; }
    string SettingsFilePath { get; }
    string HistoryFilePath { get; }
}

public interface IConfigDiagnosticsStore
{
    event EventHandler? WarningsChanged;
    IReadOnlyList<ConfigLoadWarning> Warnings { get; }
    void AddWarning(ConfigLoadWarning warning);
    void ClearWarnings();
}

public interface ICredentialStore
{
    Task SaveAsync(Guid proxyId, ProxyCredentials credentials, CancellationToken cancellationToken = default);
    Task<ProxyCredentials?> LoadAsync(Guid proxyId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid proxyId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid proxyId, CancellationToken cancellationToken = default);
    string? ProtectForPowerShell(string plaintext);
}
