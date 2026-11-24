using System.Text.Json;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Persistence;

public sealed class ProxyProfileRepository : IProxyProfileRepository
{
    private readonly JsonConfigStore _store;
    private readonly IPathProvider _paths;

    public ProxyProfileRepository(JsonConfigStore store, IPathProvider paths)
    {
        _store = store;
        _paths = paths;
    }

    public async Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadAsync(
            _paths.ProxiesFilePath,
            () => new List<ProxyProfile>(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return items;
    }

    public Task SaveAllAsync(IEnumerable<ProxyProfile> profiles, CancellationToken cancellationToken = default)
        => _store.SaveAsync(_paths.ProxiesFilePath, profiles.ToList(), cancellationToken);
}

public sealed class ApplicationProfileRepository : IApplicationProfileRepository
{
    private readonly JsonConfigStore _store;
    private readonly IPathProvider _paths;

    public ApplicationProfileRepository(JsonConfigStore store, IPathProvider paths)
    {
        _store = store;
        _paths = paths;
    }

    public async Task<IReadOnlyList<ApplicationProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadAsync(
            _paths.ApplicationsFilePath,
            () => new List<ApplicationProfile>(),
            migrateFromElement: MigrateApplications,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return items;
    }

    public Task SaveAllAsync(IEnumerable<ApplicationProfile> applications, CancellationToken cancellationToken = default)
        => _store.SaveAsync(_paths.ApplicationsFilePath, applications.ToList(), cancellationToken);

    private static List<ApplicationProfile> MigrateApplications(int fromVersion, JsonElement dataElement)
    {
        var items = dataElement.Deserialize<List<ApplicationProfile>>(JsonConfigStore.DefaultSerializerOptions) ?? [];
        var rawElements = dataElement.ValueKind == JsonValueKind.Array
            ? dataElement.EnumerateArray().ToList()
            : null;
        ApplicationProfileMigrator.MigrateFromSchemaVersion(fromVersion, items, rawElements);
        return items;
    }
}

public sealed class SettingsService : ISettingsService
{
    private readonly JsonConfigStore _store;
    private readonly IPathProvider _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cached;

    public SettingsService(JsonConfigStore store, IPathProvider paths)
    {
        _store = store;
        _paths = paths;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }

            _cached = await _store.LoadAsync(
                _paths.SettingsFilePath,
                () => new AppSettings(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = Clone(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(_paths.SettingsFilePath, copy, cancellationToken).ConfigureAwait(false);
            _cached = copy;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, Clone(copy));
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        Theme = s.Theme,
        Language = s.Language,
        MinimizeToTray = s.MinimizeToTray,
        StartMinimized = s.StartMinimized,
        RunAtWindowsStartup = s.RunAtWindowsStartup,
        DefaultProxyId = s.DefaultProxyId,
        DefaultTerminal = s.DefaultTerminal,
        DefaultCliWorkingDirectory = s.DefaultCliWorkingDirectory,
        MinimumLogLevel = s.MinimumLogLevel,
        MaxHistoryEntries = s.MaxHistoryEntries,
        ConfirmBeforeRelaunchingRunningApp = s.ConfirmBeforeRelaunchingRunningApp
    };
}

public sealed class LaunchHistoryRepository : ILaunchHistoryRepository
{
    private readonly JsonConfigStore _store;
    private readonly IPathProvider _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LaunchHistoryRepository(JsonConfigStore store, IPathProvider paths)
    {
        _store = store;
        _paths = paths;
    }

    public async Task<IReadOnlyList<LaunchHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.LoadAsync(
                _paths.HistoryFilePath,
                () => new List<LaunchHistoryEntry>(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(LaunchHistoryEntry entry, int maxEntries = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await _store.LoadAsync(
                _paths.HistoryFilePath,
                () => new List<LaunchHistoryEntry>(),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            list.Insert(0, entry);
            var limit = Math.Clamp(maxEntries, 10, 2000);
            if (list.Count > limit)
            {
                list.RemoveRange(limit, list.Count - limit);
            }

            await _store.SaveAsync(_paths.HistoryFilePath, list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(_paths.HistoryFilePath, new List<LaunchHistoryEntry>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
