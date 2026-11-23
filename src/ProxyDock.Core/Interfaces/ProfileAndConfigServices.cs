using ProxyDock.Core.Models;

namespace ProxyDock.Core.Interfaces;

public interface IProxyProfileService
{
    event EventHandler? ProxiesChanged;
    Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProxyProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProxyProfile?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<ProxyProfile> SaveAsync(ProxyProfile profile, string? newPassword = null, bool clearPassword = false, CancellationToken cancellationToken = default);
    Task<ProxyProfile> DuplicateAsync(Guid id, CancellationToken cancellationToken = default);
    Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateCheckResultAsync(Guid id, ProxyTestResult result, CancellationToken cancellationToken = default);
}

public interface IApplicationProfileService
{
    event EventHandler? ApplicationsChanged;
    Task<IReadOnlyList<ApplicationProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ApplicationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApplicationProfile> SaveAsync(ApplicationProfile profile, CancellationToken cancellationToken = default);
    Task<ApplicationProfile> DuplicateAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkLaunchedAsync(Guid id, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    event EventHandler<AppSettings>? SettingsChanged;
    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IProxyConnectivityTester
{
    Task<ProxyTestResult> TestAsync(ProxyProfile profile, CancellationToken cancellationToken = default);
}

public interface IConfigExportService
{
    Task ExportAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ImportAsync(string filePath, bool replaceExisting = false, CancellationToken cancellationToken = default);
}
