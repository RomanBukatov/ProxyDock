using System.Text;
using System.Text.Json;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Validation;

namespace ProxyDock.Infrastructure.Persistence;

/// <summary>
/// Экспортирует и импортирует конфигурацию ProxyDock без раскрытия паролей и DPAPI-секретов.
/// </summary>
public sealed class ConfigExportService : IConfigExportService
{
    private readonly IApplicationProfileService _applications;
    private readonly IProxyProfileService _proxies;
    private readonly ISettingsService _settings;

    public ConfigExportService(
        IApplicationProfileService applications,
        IProxyProfileService proxies,
        ISettingsService settings)
    {
        _applications = applications;
        _proxies = proxies;
        _settings = settings;
    }

    public async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var apps = await _applications.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var proxies = await _proxies.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        var sanitizedProxies = proxies.Select(p => new ProxyProfile
        {
            Id = p.Id,
            Name = p.Name,
            Protocol = p.Protocol,
            Host = p.Host,
            Port = p.Port,
            Username = p.Username,
            HasCredentials = false, // Секреты не экспортируются
            NoProxy = p.NoProxy,
            IsDefault = p.IsDefault,
            CheckTimeoutSeconds = p.CheckTimeoutSeconds,
            DiagnosticEndpointUrl = p.DiagnosticEndpointUrl,
            CreatedAtUtc = p.CreatedAtUtc,
            UpdatedAtUtc = p.UpdatedAtUtc
        }).ToList();

        var sanitizedApps = apps.Select(a => new ApplicationProfile
        {
            Id = a.Id,
            Name = a.Name,
            LaunchKind = a.LaunchKind,
            ExecutableOrCommand = a.ExecutableOrCommand,
            WorkingDirectory = a.WorkingDirectory,
            Arguments = a.Arguments,
            ProxyMode = a.ProxyMode,
            ProxyProfileId = a.ProxyProfileId,
            UseChromiumProxyFlag = a.UseChromiumProxyFlag,
            LaunchInTerminal = a.LaunchInTerminal,
            KeepTerminalOpen = a.KeepTerminalOpen,
            TerminalOverride = a.TerminalOverride,
            NoProxyOverride = a.NoProxyOverride,
            CustomEnvironmentVariables = a.CustomEnvironmentVariables
                .Select(e => new EnvironmentVariableEntry
                {
                    Key = e.Key,
                    Value = e.IsSecret ? string.Empty : e.Value,
                    IsSecret = e.IsSecret
                }).ToList(),
            RelatedProcessNames = [.. a.RelatedProcessNames],
            TemplateKey = a.TemplateKey,
            ApplicabilityHint = a.ApplicabilityHint,
            CompatibilityNote = a.CompatibilityNote,
            Notes = a.Notes,
            CreatedAtUtc = a.CreatedAtUtc,
            UpdatedAtUtc = a.UpdatedAtUtc
        }).ToList();

        var bundle = new ExportBundle
        {
            SchemaVersion = JsonConfigStore.CurrentSchemaVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Applications = sanitizedApps,
            Proxies = sanitizedProxies,
            Settings = settings
        };

        var json = JsonSerializer.Serialize(bundle, JsonConfigStore.DefaultSerializerOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ImportAsync(
        string filePath,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var bundle = JsonSerializer.Deserialize<ExportBundle>(json, JsonConfigStore.DefaultSerializerOptions)
            ?? throw new InvalidDataException("Файл экспорта не содержит корректных данных ProxyDock.");

        var warnings = new List<string>();

        if (replaceExisting)
        {
            foreach (var existingApp in await _applications.GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _applications.DeleteAsync(existingApp.Id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var existingProxy in await _proxies.GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _proxies.DeleteAsync(existingProxy.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var proxy in bundle.Proxies)
        {
            proxy.HasCredentials = false;
            var validation = ProxyProfileValidator.Validate(proxy);
            if (!validation.IsValid)
            {
                warnings.Add($"Пропущен прокси «{proxy.Name}»: {validation.Errors[0].Message}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(proxy.Username))
            {
                warnings.Add($"Прокси «{proxy.Name}» импортирован без пароля — при необходимости введите пароль заново.");
            }

            await _proxies.SaveAsync(proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var app in bundle.Applications)
        {
            var validation = ApplicationProfileValidator.Validate(app);
            if (!validation.IsValid)
            {
                warnings.Add($"Пропущено приложение «{app.Name}»: {validation.Errors[0].Message}");
                continue;
            }

            await _applications.SaveAsync(app, cancellationToken).ConfigureAwait(false);
        }

        await _settings.SaveAsync(bundle.Settings, cancellationToken).ConfigureAwait(false);
        return warnings;
    }
}
