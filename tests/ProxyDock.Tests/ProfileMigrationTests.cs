using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProfileMigrationTests
{
    private sealed class StubDialogService : IDialogService
    {
        public Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile)
            => Task.FromResult<ApplicationProfile?>(null);

        public Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile)
            => Task.FromResult<ProxyProfile?>(null);

        public Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null)
            => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Продолжить", string? cancelText = "Отмена", bool isDanger = false)
            => Task.FromResult(false);

        public Task<string?> PickExecutableFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string defaultFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task ApplicationsV1_MigratesCodexAndClaude_WithoutTouchingAntigravityOrCustomApps()
    {
        using var scope = new TempTestScope();
        var paths = scope.Services.GetRequiredService<IPathProvider>();
        var appRepo = scope.Services.GetRequiredService<IApplicationProfileRepository>();

        var v1Json = """
        {
          "schemaVersion": 1,
          "updatedAtUtc": "2026-11-24T05:37:52.0152667+00:00",
          "data": [
            {
              "id": "cf94859f-44ca-44f9-a209-8be9a50d672f",
              "name": "Codex Desktop",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Microsoft\\WindowsApps\\OpenAI.Codex_2p2nqsd0c76g0\\chatgpt-desktop.exe",
              "proxyMode": "InheritDefault",
              "useChromiumProxyFlag": false,
              "launchInTerminal": false,
              "keepTerminalOpen": true,
              "customEnvironmentVariables": [],
              "relatedProcessNames": ["ChatGPT", "Codex"],
              "templateKey": "codex-desktop",
              "applicabilityHint": "NeedsVerification",
              "compatibilityNote": "MSIX Chromium / Owl оболочка (переменные окружения; применимость к фоновым компонентам требует проверки).",
              "notes": "Если в интерфейсе чаты зависают при выключенном системном прокси, проверьте поведение с включённым --proxy-server или настройкой %USERPROFILE%\\.codex\\.env."
            },
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Claude Desktop",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Microsoft\\WindowsApps\\Claude_pzs8sxrjxfjjc\\claude-desktop.exe",
              "proxyMode": "InheritDefault",
              "useChromiumProxyFlag": false,
              "launchInTerminal": false,
              "keepTerminalOpen": true,
              "customEnvironmentVariables": [],
              "relatedProcessNames": ["Claude"],
              "templateKey": "claude-desktop",
              "applicabilityHint": "NeedsVerification",
              "compatibilityNote": "MSIX Electron + служба cowork-svc.exe: запускается с переменными окружения."
            },
            {
              "id": "4788d4fc-08f9-4adc-83fe-737505290eb2",
              "name": "Google Antigravity",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Programs\\antigravity\\Antigravity.exe",
              "proxyMode": "InheritDefault",
              "useChromiumProxyFlag": true,
              "launchInTerminal": false,
              "keepTerminalOpen": true,
              "customEnvironmentVariables": [],
              "relatedProcessNames": ["Antigravity"],
              "templateKey": "google-antigravity",
              "applicabilityHint": "Confirmed"
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "Custom Win32 App",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Apps\\Custom\\Tool.exe",
              "proxyMode": "InheritDefault",
              "useChromiumProxyFlag": false,
              "launchInTerminal": false,
              "keepTerminalOpen": true,
              "customEnvironmentVariables": []
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(paths.ApplicationsFilePath, v1Json);

        var loaded = await appRepo.GetAllAsync();
        Assert.Equal(4, loaded.Count);

        var codex = loaded.Single(a => a.TemplateKey == BuiltInAppTemplates.CodexDesktopKey);
        Assert.True(codex.UseChromiumProxyFlag);
        Assert.True(codex.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.Confirmed, codex.ApplicabilityHint);
        Assert.Contains("NODE_USE_ENV_PROXY=1", codex.CompatibilityNote);

        var claude = loaded.Single(a => a.TemplateKey == BuiltInAppTemplates.ClaudeDesktopKey);
        Assert.True(claude.UseChromiumProxyFlag);
        Assert.True(claude.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.NeedsVerification, claude.ApplicabilityHint);

        var antigravity = loaded.Single(a => a.TemplateKey == BuiltInAppTemplates.GoogleAntigravityKey);
        Assert.True(antigravity.UseChromiumProxyFlag);
        Assert.False(antigravity.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.Confirmed, antigravity.ApplicabilityHint);

        var custom = loaded.Single(a => a.Name == "Custom Win32 App");
        Assert.False(custom.UseChromiumProxyFlag);
        Assert.False(custom.UseNodeEnvProxy);

        var persistedJson = await File.ReadAllTextAsync(paths.ApplicationsFilePath);
        using var doc = JsonDocument.Parse(persistedJson);
        Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(File.Exists(Path.Combine(paths.BackupsDirectory, "applications.bak.json")));
    }

    [Fact]
    public async Task ApplicationsV1_Migration_RespectsExplicitUserOverrides()
    {
        using var scope = new TempTestScope();
        var paths = scope.Services.GetRequiredService<IPathProvider>();
        var appRepo = scope.Services.GetRequiredService<IApplicationProfileRepository>();

        var v1Json = """
        {
          "schemaVersion": 1,
          "data": [
            {
              "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "name": "Codex Manual Proxy Arg",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Microsoft\\WindowsApps\\OpenAI.Codex_123\\chatgpt-desktop.exe",
              "arguments": "--proxy-server=http://127.0.0.1:9999",
              "useChromiumProxyFlag": false,
              "customEnvironmentVariables": [
                { "key": "NODE_USE_ENV_PROXY", "value": "0", "isSecret": false }
              ],
              "templateKey": "codex-desktop",
              "applicabilityHint": "NeedsVerification",
              "compatibilityNote": "My custom note",
              "notes": "My custom user notes"
            },
            {
              "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "name": "Codex No Proxy Arg",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Microsoft\\WindowsApps\\OpenAI.Codex_123\\chatgpt-desktop.exe",
              "arguments": "--no-proxy-server",
              "useChromiumProxyFlag": false,
              "templateKey": "codex-desktop"
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(paths.ApplicationsFilePath, v1Json);

        var loaded = await appRepo.GetAllAsync();
        var manual = loaded.Single(a => a.Name == "Codex Manual Proxy Arg");
        Assert.False(manual.UseChromiumProxyFlag);
        Assert.False(manual.UseNodeEnvProxy);
        Assert.Equal("0", manual.CustomEnvironmentVariables.Single(e => e.Key == "NODE_USE_ENV_PROXY").Value);
        Assert.Equal("My custom note", manual.CompatibilityNote);
        Assert.Equal("My custom user notes", manual.Notes);
        Assert.Equal(ProxyApplicabilityLevel.NeedsVerification, manual.ApplicabilityHint);

        var noProxyArg = loaded.Single(a => a.Name == "Codex No Proxy Arg");
        Assert.False(noProxyArg.UseChromiumProxyFlag);
        Assert.True(noProxyArg.UseNodeEnvProxy);
    }

    [Fact]
    public async Task ApplicationsV2_DoesNotReMigrate_WhenUserExplicitlyDisablesFlags()
    {
        using var scope = new TempTestScope();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var appRepo = scope.Services.GetRequiredService<IApplicationProfileRepository>();

        var codex = BuiltInAppTemplates.CreateCodexDesktop(@"C:\Users\demo\AppData\Local\Microsoft\WindowsApps\OpenAI.Codex_123\chatgpt-desktop.exe");
        codex.UseChromiumProxyFlag = false;
        codex.UseNodeEnvProxy = false;
        await appService.SaveAsync(codex);

        var reloaded = await appRepo.GetAllAsync();
        var item = Assert.Single(reloaded);
        Assert.False(item.UseChromiumProxyFlag);
        Assert.False(item.UseNodeEnvProxy);
    }

    [Fact]
    public async Task DuplicateAndExportImport_PreserveUseNodeEnvProxy_AndMigrateV1Bundle()
    {
        using var scope = new TempTestScope();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var exportService = scope.Services.GetRequiredService<IConfigExportService>();

        var custom = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "Custom Node App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\Apps\NodeApp.exe",
            UseChromiumProxyFlag = true,
            UseNodeEnvProxy = true
        });

        var copy = await appService.DuplicateAsync(custom.Id);
        Assert.True(copy.UseChromiumProxyFlag);
        Assert.True(copy.UseNodeEnvProxy);

        var exportPath = Path.Combine(scope.RootPath, "bundle-v2.json");
        await exportService.ExportAsync(exportPath);

        await appService.DeleteAsync(custom.Id);
        await appService.DeleteAsync(copy.Id);
        Assert.Empty(await appService.GetAllAsync());

        await exportService.ImportAsync(exportPath, replaceExisting: false);
        var imported = await appService.GetAllAsync();
        Assert.Equal(2, imported.Count);
        Assert.All(imported, a =>
        {
            Assert.True(a.UseChromiumProxyFlag);
            Assert.True(a.UseNodeEnvProxy);
        });

        // Проверяем импорт legacy v1 export bundle
        var legacyBundlePath = Path.Combine(scope.RootPath, "bundle-v1.json");
        var legacyBundleJson = """
        {
          "schemaVersion": 1,
          "applications": [
            {
              "id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "name": "Codex Desktop",
              "launchKind": "Desktop",
              "executableOrCommand": "C:\\Users\\demo\\AppData\\Local\\Microsoft\\WindowsApps\\OpenAI.Codex_123\\chatgpt-desktop.exe",
              "useChromiumProxyFlag": false,
              "templateKey": "codex-desktop"
            }
          ],
          "proxies": [],
          "settings": {}
        }
        """;
        await File.WriteAllTextAsync(legacyBundlePath, legacyBundleJson);

        await exportService.ImportAsync(legacyBundlePath, replaceExisting: true);
        var migratedImported = Assert.Single(await appService.GetAllAsync());
        Assert.True(migratedImported.UseChromiumProxyFlag);
        Assert.True(migratedImported.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.Confirmed, migratedImported.ApplicabilityHint);
    }

    [Fact]
    public void ApplicationEditorViewModel_PersistsChromiumAndNodeEnvProxy_ForCustomDesktopAndCliApps()
    {
        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
        var loc = new LocalizationService();
        var dialogs = new StubDialogService();

        var desktopVm = new ApplicationEditorViewModel(loc, discovery, dialogs, [])
        {
            Name = "Custom Electron",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\Apps\Custom.exe",
            UseChromiumProxyFlag = true,
            UseNodeEnvProxy = true
        };
        desktopVm.SaveCommand.Execute(null);
        Assert.NotNull(desktopVm.SavedProfile);
        Assert.True(desktopVm.SavedProfile.UseChromiumProxyFlag);
        Assert.True(desktopVm.SavedProfile.UseNodeEnvProxy);

        var cliVm = new ApplicationEditorViewModel(loc, discovery, dialogs, [])
        {
            Name = "Custom Node CLI",
            LaunchKind = LaunchKind.Cli,
            ExecutableOrCommand = "node",
            UseNodeEnvProxy = true
        };
        cliVm.SaveCommand.Execute(null);
        Assert.NotNull(cliVm.SavedProfile);
        Assert.False(cliVm.SavedProfile.UseChromiumProxyFlag);
        Assert.True(cliVm.SavedProfile.UseNodeEnvProxy);
    }
}
