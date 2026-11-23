using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Validation;

namespace ProxyDock.Infrastructure.Proxy;

public sealed class ApplicationProfileService : IApplicationProfileService
{
    private readonly IApplicationProfileRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApplicationProfileService(IApplicationProfileRepository repository)
    {
        _repository = repository;
    }

    public event EventHandler? ApplicationsChanged;

    public async Task<IReadOnlyList<ApplicationProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ApplicationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var list = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(a => a.Id == id);
    }

    public async Task<ApplicationProfile> SaveAsync(ApplicationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ApplicationProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Errors[0].Message, nameof(profile));
        }

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
            profile.ExecutableOrCommand = profile.ExecutableOrCommand.Trim();
            profile.WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? null : profile.WorkingDirectory.Trim();
            profile.Arguments = string.IsNullOrWhiteSpace(profile.Arguments) ? null : profile.Arguments.Trim();
            profile.NoProxyOverride = string.IsNullOrWhiteSpace(profile.NoProxyOverride) ? null : profile.NoProxyOverride.Trim();
            profile.CustomEnvironmentVariables = profile.CustomEnvironmentVariables
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => new EnvironmentVariableEntry
                {
                    Key = e.Key.Trim(),
                    Value = e.Value ?? string.Empty,
                    IsSecret = e.IsSecret
                })
                .ToList();
            profile.RelatedProcessNames = profile.RelatedProcessNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var index = list.FindIndex(a => a.Id == profile.Id);
            if (index >= 0)
            {
                profile.CreatedAtUtc = list[index].CreatedAtUtc;
                profile.LastLaunchedAtUtc = profile.LastLaunchedAtUtc ?? list[index].LastLaunchedAtUtc;
                profile.UpdatedAtUtc = now;
                list[index] = profile;
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

        ApplicationsChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public async Task<ApplicationProfile> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Профиль приложения {id} не найден.");

        var copy = new ApplicationProfile
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} (копия)",
            LaunchKind = source.LaunchKind,
            ExecutableOrCommand = source.ExecutableOrCommand,
            WorkingDirectory = source.WorkingDirectory,
            Arguments = source.Arguments,
            ProxyMode = source.ProxyMode,
            ProxyProfileId = source.ProxyProfileId,
            UseChromiumProxyFlag = source.UseChromiumProxyFlag,
            LaunchInTerminal = source.LaunchInTerminal,
            KeepTerminalOpen = source.KeepTerminalOpen,
            TerminalOverride = source.TerminalOverride,
            NoProxyOverride = source.NoProxyOverride,
            CustomEnvironmentVariables = source.CustomEnvironmentVariables
                .Select(e => new EnvironmentVariableEntry
                {
                    Key = e.Key,
                    Value = e.Value,
                    IsSecret = e.IsSecret
                }).ToList(),
            RelatedProcessNames = [.. source.RelatedProcessNames],
            TemplateKey = source.TemplateKey,
            ApplicabilityHint = source.ApplicabilityHint,
            CompatibilityNote = source.CompatibilityNote,
            Notes = source.Notes
        };

        return await SaveAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            removed = list.RemoveAll(a => a.Id == id) > 0;
            if (removed)
            {
                await _repository.SaveAllAsync(list, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (removed)
        {
            ApplicationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task MarkLaunchedAsync(Guid id, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        var updated = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var item = list.FirstOrDefault(a => a.Id == id);
            if (item is not null)
            {
                item.LastLaunchedAtUtc = timestampUtc;
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
            ApplicationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
