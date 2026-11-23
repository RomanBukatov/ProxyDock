using ProxyDock.Core.Models;

namespace ProxyDock.Core.Interfaces;

public interface IProxyProfileRepository
{
    Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(IEnumerable<ProxyProfile> profiles, CancellationToken cancellationToken = default);
}

public interface IApplicationProfileRepository
{
    Task<IReadOnlyList<ApplicationProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(IEnumerable<ApplicationProfile> applications, CancellationToken cancellationToken = default);
}

public interface ILaunchHistoryRepository
{
    Task<IReadOnlyList<LaunchHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(LaunchHistoryEntry entry, int maxEntries = 200, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
