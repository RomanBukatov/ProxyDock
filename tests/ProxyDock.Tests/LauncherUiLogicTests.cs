using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class LauncherUiLogicTests
{
    [Fact]
    public void AppSettings_Defaults_DisableTrayAndStartupByDefault()
    {
        var settings = new AppSettings();

        Assert.False(settings.MinimizeToTray);
        Assert.False(settings.StartMinimized);
        Assert.False(settings.RunAtWindowsStartup);
    }

    [Fact]
    public void TrayIconService_IsDisabledBeforeSetEnabled()
    {
        using var scope = new TempTestScope();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();
        var loc = new LocalizationService();

        using var tray = new TrayIconService(appService, launcher, loc);

        Assert.False(tray.IsEnabled);
    }

    [Fact]
    public void FilterUnaddedDetectedTemplates_SkipsUndetectedAndAlreadyAddedApps_AndHidesWhenAllAdded()
    {
        var antigravityPath = @"C:\Apps\Antigravity\Antigravity.exe";
        var claudePath = @"C:\Users\demo\AppData\Local\Microsoft\WindowsApps\Claude_123\claude-desktop.exe";

        ExecutableDiscoveryResult Discover(string key) => key switch
        {
            BuiltInAppTemplates.GoogleAntigravityKey => new ExecutableDiscoveryResult
            {
                Found = true,
                ResolvedPath = antigravityPath
            },
            BuiltInAppTemplates.ClaudeDesktopKey => new ExecutableDiscoveryResult
            {
                Found = true,
                ResolvedPath = claudePath
            },
            _ => new ExecutableDiscoveryResult { Found = false }
        };

        // 1. Ничего не добавлено -> показываем только реально найденные (Google Antigravity и Claude Desktop), Codex скрыт
        var initial = ApplicationsViewModel.FilterUnaddedDetectedTemplates([], Discover);
        Assert.Equal(2, initial.Count);
        Assert.Contains(initial, x => x.Template.Key == BuiltInAppTemplates.GoogleAntigravityKey && x.Template.DisplayName == "Google Antigravity");
        Assert.Contains(initial, x => x.Template.Key == BuiltInAppTemplates.ClaudeDesktopKey && x.Template.DisplayName == "Claude Desktop");
        Assert.DoesNotContain(initial, x => x.Template.Key == BuiltInAppTemplates.CodexDesktopKey);

        // 2. Добавлен Google Antigravity по TemplateKey -> не дублируется в обнаруженных
        var withAntigravity = new[]
        {
            BuiltInAppTemplates.CreateGoogleAntigravity(antigravityPath)
        };
        var afterOne = ApplicationsViewModel.FilterUnaddedDetectedTemplates(withAntigravity, Discover);
        var remaining = Assert.Single(afterOne);
        Assert.Equal(BuiltInAppTemplates.ClaudeDesktopKey, remaining.Template.Key);

        // 3. Claude Desktop добавлен вручную по пути без TemplateKey -> также не дублируется
        var withBoth = new[]
        {
            BuiltInAppTemplates.CreateGoogleAntigravity(antigravityPath),
            new ApplicationProfile
            {
                Name = "Claude Manual",
                LaunchKind = LaunchKind.Desktop,
                ExecutableOrCommand = claudePath
            }
        };
        var afterAll = ApplicationsViewModel.FilterUnaddedDetectedTemplates(withBoth, Discover);
        Assert.Empty(afterAll);
    }

    [Fact]
    public void TryParseDataDirArgument_ParsesSupportedFormsAndIgnoresMissingValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ProxyDockDataDirTest");

        Assert.Null(Program.TryParseDataDirArgument(null));
        Assert.Null(Program.TryParseDataDirArgument([]));
        Assert.Null(Program.TryParseDataDirArgument(["--verify-startup"]));
        Assert.Null(Program.TryParseDataDirArgument(["--data-dir"]));
        Assert.Null(Program.TryParseDataDirArgument(["--data-dir", "--verify-startup"]));
        Assert.Null(Program.TryParseDataDirArgument(["--data-dir="]));

        Assert.Equal(
            Path.GetFullPath(tempDir),
            Program.TryParseDataDirArgument(["--data-dir", tempDir]));
        Assert.Equal(
            Path.GetFullPath(tempDir),
            Program.TryParseDataDirArgument([$"--data-dir={tempDir}"]));
    }
}
