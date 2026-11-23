using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Validation;

namespace ProxyDock.Infrastructure.Proxy;

public sealed class ProxyProfileService : IProxyProfileService
{
    private readonly IProxyProfileRepository _repository;
    private readonly IApplicationProfileRepository _appRepository;
    private readonly ICredentialStore _credentialStore;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProxyProfileService(
        IProxyProfileRepository repository,
        IApplicationProfileRepository appRepository,
        ICredentialStore credentialStore,
        ISettingsService settingsService)
    {
        _repository = repository;
        _appRepository = appRepository;
        _credentialStore = credentialStore;
        _settingsService = settingsService;
    }

    public event EventHandler? ProxiesChanged;

    public async Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ProxyProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var list = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(p => p.Id == id);
    }

    public async Task<ProxyProfile?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var list = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);

        if (settings.DefaultProxyId is Guid defaultId)
        {
            var match = list.FirstOrDefault(p => p.Id == defaultId);
            if (match is not null)
            {
                return match;
            }
        }

        return list.FirstOrDefault(p => p.IsDefault);
    }

    public async Task<ProxyProfile> SaveAsync(
        ProxyProfile profile,
        string? newPassword = null,
        bool clearPassword = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ProxyProfileValidator.Validate(profile, newPassword, clearPassword);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Errors[0].Message, nameof(profile));
        }

        Guid? defaultIdToSync = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var now = DateTimeOffset.UtcNow;
            if (profile.Id == Guid.Empty)
            {
                profile.Id = Guid.NewGuid();
            }

            profile.Name = profile.Name.Trim();
            profile.Host = profile.Host.Trim();
            profile.Username = string.IsNullOrWhiteSpace(profile.Username) ? null : profile.Username.Trim();
            profile.NoProxy = string.IsNullOrWhiteSpace(profile.NoProxy) ? string.Empty : profile.NoProxy.Trim();
            profile.DiagnosticEndpointUrl = profile.DiagnosticEndpointUrl.Trim();

            var existingIndex = list.FindIndex(p => p.Id == profile.Id);
            var existingHasCreds = existingIndex >= 0 && list[existingIndex].HasCredentials;

            if (clearPassword || string.IsNullOrEmpty(profile.Username))
            {
                await _credentialStore.DeleteAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                profile.HasCredentials = false;
            }
            else if (!string.IsNullOrEmpty(newPassword))
            {
                await _credentialStore.SaveAsync(profile.Id, new ProxyCredentials(profile.Username, newPassword), cancellationToken).ConfigureAwait(false);
                profile.HasCredentials = true;
            }
            else if (existingHasCreds)
            {
                var existingCreds = await _credentialStore.LoadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                if (existingCreds is not null)
                {
                    if (!string.Equals(existingCreds.Username, profile.Username, StringComparison.Ordinal))
                    {
                        await _credentialStore.SaveAsync(profile.Id, existingCreds with { Username = profile.Username }, cancellationToken).ConfigureAwait(false);
                    }

                    profile.HasCredentials = true;
                }
                else
                {
                    profile.HasCredentials = false;
                }
            }
            else
            {
                profile.HasCredentials = false;
            }

            // Если это первый профиль в системе, автоматически делаем его профилем по умолчанию.
            if (list.Count == 0)
            {
                profile.IsDefault = true;
            }

            if (profile.IsDefault)
            {
                foreach (var other in list)
                {
                    if (other.Id != profile.Id)
                    {
                        other.IsDefault = false;
                    }
                }

                defaultIdToSync = profile.Id;
            }

            if (existingIndex >= 0)
            {
                profile.CreatedAtUtc = list[existingIndex].CreatedAtUtc;
                profile.UpdatedAtUtc = now;
                list[existingIndex] = profile;
            }
            else
            {
                profile.CreatedAtUtc = now;
                profile.UpdatedAtUtc = now;
                list.Add(profile);
            }

            await _repository.SaveAllAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (defaultIdToSync is Guid syncId)
        {
            var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
            if (settings.DefaultProxyId != syncId)
            {
                settings.DefaultProxyId = syncId;
                await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }
        }

        ProxiesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public async Task<ProxyProfile> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Прокси-профиль {id} не найден.");

        string? copiedPassword = null;
        if (source.HasCredentials)
        {
            var creds = await _credentialStore.LoadAsync(source.Id, cancellationToken).ConfigureAwait(false);
            copiedPassword = creds?.Password;
        }

        var copy = new ProxyProfile
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} (копия)",
            Protocol = source.Protocol,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            HasCredentials = false,
            NoProxy = source.NoProxy,
            IsDefault = false,
            CheckTimeoutSeconds = source.CheckTimeoutSeconds,
            DiagnosticEndpointUrl = source.DiagnosticEndpointUrl,
            LastCheckStatus = ProxyCheckStatus.Unknown
        };

        return await SaveAsync(copy, newPassword: copiedPassword, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (!list.Any(p => p.Id == id))
            {
                throw new KeyNotFoundException($"Прокси-профиль {id} не найден.");
            }

            foreach (var item in list)
            {
                item.IsDefault = item.Id == id;
            }

            await _repository.SaveAllAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        settings.DefaultProxyId = id;
        await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        ProxiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Guid? nextDefaultId = null;
        var deleted = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var target = list.FirstOrDefault(p => p.Id == id);
            if (target is null)
            {
                return;
            }

            var wasDefault = target.IsDefault;
            list.Remove(target);
            deleted = true;

            if (wasDefault && list.Count > 0)
            {
                list[0].IsDefault = true;
                nextDefaultId = list[0].Id;
            }

            await _repository.SaveAllAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!deleted)
        {
            return;
        }

        await _credentialStore.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        // Очищаем ссылки у приложений, использовавших удалённый прокси
        var apps = (await _appRepository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var appsModified = false;
        foreach (var app in apps)
        {
            if (app.ProxyProfileId == id)
            {
                app.ProxyProfileId = null;
                if (app.ProxyMode == ProxyAssignmentMode.Specific)
                {
                    app.ProxyMode = ProxyAssignmentMode.InheritDefault;
                }

                appsModified = true;
            }
        }

        if (appsModified)
        {
            await _appRepository.SaveAllAsync(apps, cancellationToken).ConfigureAwait(false);
        }

        var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.DefaultProxyId == id)
        {
            settings.DefaultProxyId = nextDefaultId;
            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        ProxiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateCheckResultAsync(Guid id, ProxyTestResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var updated = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var item = list.FirstOrDefault(p => p.Id == id);
            if (item is not null)
            {
                item.LastCheckStatus = result.Status;
                item.LastCheckedAtUtc = result.CheckedAtUtc;
                item.LastLatencyMs = result.HttpsLatencyMs ?? result.TcpLatencyMs;
                item.LastExternalIp = result.ExternalIp;
                item.LastCheckMessage = result.Message;
                await _repository.SaveAllAsync(list, cancellationToken).ConfigureAwait(false);
                updated = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (updated)
        {
            ProxiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
