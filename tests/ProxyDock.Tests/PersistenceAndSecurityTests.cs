using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using Xunit;

namespace ProxyDock.Tests;

public sealed class PersistenceAndSecurityTests
{
    [Fact]
    public async Task ProxyAndAppAndSettings_PersistAcrossReads_WithoutPlaintextPasswordsInJson()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var settingsService = scope.Services.GetRequiredService<ISettingsService>();
        var credentialStore = scope.Services.GetRequiredService<ICredentialStore>();
        var paths = scope.Services.GetRequiredService<IPathProvider>();

        var proxy = await proxyService.SaveAsync(new ProxyProfile
        {
            Name = "Secure Proxy",
            Host = "127.0.0.1",
            Port = 8080,
            Username = "proxyuser",
            IsDefault = true
        }, newPassword: "TopSecretPassword#2026");

        var app = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "Antigravity",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\Apps\Antigravity.exe",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = proxy.Id,
            UseChromiumProxyFlag = true
        });

        var defaultSettings = new AppSettings();
        Assert.False(defaultSettings.MinimizeToTray);
        Assert.False(defaultSettings.StartMinimized);
        Assert.False(defaultSettings.RunAtWindowsStartup);

        var settings = await settingsService.GetAsync();
        Assert.False(settings.MinimizeToTray);
        settings.Theme = AppThemeMode.Light;
        settings.Language = "en";
        settings.MinimizeToTray = true;
        settings.DefaultProxyId = proxy.Id;
        await settingsService.SaveAsync(settings);

        // Проверяем отсутствие открытого пароля на диске
        var proxiesJson = await File.ReadAllTextAsync(paths.ProxiesFilePath);
        Assert.DoesNotContain("TopSecretPassword#2026", proxiesJson, StringComparison.Ordinal);

        var loadedCreds = await credentialStore.LoadAsync(proxy.Id);
        Assert.NotNull(loadedCreds);
        Assert.Equal("proxyuser", loadedCreds.Username);
        Assert.Equal("TopSecretPassword#2026", loadedCreds.Password);

        var secretFile = Directory.GetFiles(paths.SecretsDirectory, "*.bin").Single();
        var rawSecretBytes = await File.ReadAllBytesAsync(secretFile);
        Assert.DoesNotContain("TopSecretPassword", System.Text.Encoding.UTF8.GetString(rawSecretBytes), StringComparison.Ordinal);

        // Проверяем чтение настроек и профилей
        var loadedProxies = await proxyService.GetAllAsync();
        var loadedApps = await appService.GetAllAsync();
        Assert.Single(loadedProxies);
        Assert.Single(loadedApps);
        Assert.Equal(app.Name, loadedApps[0].Name);

        var loadedSettings = await settingsService.GetAsync();
        Assert.Equal(AppThemeMode.Light, loadedSettings.Theme);
        Assert.Equal("en", loadedSettings.Language);
        Assert.True(loadedSettings.MinimizeToTray);

        loadedSettings.MinimizeToTray = false;
        await settingsService.SaveAsync(loadedSettings);
        Assert.False((await settingsService.GetAsync()).MinimizeToTray);

        // Проверяем удаление секрета
        await proxyService.DeleteAsync(proxy.Id);
        Assert.False(await credentialStore.ExistsAsync(proxy.Id));
    }

    [Fact]
    public async Task CorruptJson_IsBackedUpAndProducesDiagnosticWarningWithoutCrashing()
    {
        using var scope = new TempTestScope();
        var paths = scope.Services.GetRequiredService<IPathProvider>();
        var appRepo = scope.Services.GetRequiredService<IApplicationProfileRepository>();
        var diagnostics = scope.Services.GetRequiredService<IConfigDiagnosticsStore>();

        await File.WriteAllTextAsync(paths.ApplicationsFilePath, "{ broken json ::: ");

        var apps = await appRepo.GetAllAsync();

        Assert.Empty(apps);
        Assert.Single(diagnostics.Warnings);
        Assert.Equal("applications.json", diagnostics.Warnings[0].FileName);
        Assert.NotEmpty(Directory.GetFiles(paths.BackupsDirectory, "applications.corrupt.*.json"));
    }

    [Fact]
    public async Task LegacyUnenvelopedV0Json_MigratesCleanlyToSchemaV1()
    {
        using var scope = new TempTestScope();
        var paths = scope.Services.GetRequiredService<IPathProvider>();
        var proxyRepo = scope.Services.GetRequiredService<IProxyProfileRepository>();

        var legacyList = new[]
        {
            new ProxyProfile { Name = "Legacy Proxy", Host = "127.0.0.1", Port = 8118, IsDefault = true }
        };
        await File.WriteAllTextAsync(paths.ProxiesFilePath, JsonSerializer.Serialize(legacyList));

        var loaded = await proxyRepo.GetAllAsync();
        Assert.Single(loaded);
        Assert.Equal("Legacy Proxy", loaded[0].Name);

        var updatedJson = await File.ReadAllTextAsync(paths.ProxiesFilePath);
        Assert.Contains("schemaVersion", updatedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigExportAndImport_StripsPasswordsAndRestoresProfiles()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var exportService = scope.Services.GetRequiredService<IConfigExportService>();

        var p = await proxyService.SaveAsync(new ProxyProfile
        {
            Name = "Work Proxy",
            Host = "10.0.0.2",
            Port = 3128,
            Username = "workuser"
        }, newPassword: "ExportMustNotLeakThisPassword");

        var exportPath = Path.Combine(scope.RootPath, "export.json");
        await exportService.ExportAsync(exportPath);

        var exportedContent = await File.ReadAllTextAsync(exportPath);
        Assert.DoesNotContain("ExportMustNotLeakThisPassword", exportedContent, StringComparison.Ordinal);

        await proxyService.DeleteAsync(p.Id);
        Assert.Empty(await proxyService.GetAllAsync());

        var warnings = await exportService.ImportAsync(exportPath, replaceExisting: false);
        var imported = await proxyService.GetAllAsync();

        Assert.Single(imported);
        Assert.False(imported[0].HasCredentials);
        Assert.NotEmpty(warnings);
    }
}
