using Microsoft.Extensions.DependencyInjection;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Launching;
using Xunit;
using Xunit.Abstractions;

namespace ProxyDock.Tests;

public sealed class BuiltInTemplatesAndStackInspectionTests(ITestOutputHelper output)
{
    [Fact]
    public void BuiltInTemplates_PrioritizeDesktopAppsWithAccurateDefaults()
    {
        Assert.Equal(3, BuiltInAppTemplates.DesktopTemplates.Count);

        var antigravity = BuiltInAppTemplates.CreateGoogleAntigravity();
        Assert.Equal(LaunchKind.Desktop, antigravity.LaunchKind);
        Assert.True(antigravity.UseChromiumProxyFlag);
        Assert.False(antigravity.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.Confirmed, antigravity.ApplicabilityHint);
        Assert.Contains("Antigravity", antigravity.RelatedProcessNames);

        var claude = BuiltInAppTemplates.CreateClaudeDesktop();
        Assert.Equal(LaunchKind.Desktop, claude.LaunchKind);
        Assert.True(claude.UseChromiumProxyFlag);
        Assert.True(claude.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.NeedsVerification, claude.ApplicabilityHint);
        Assert.Contains("Claude", claude.RelatedProcessNames);

        var codex = BuiltInAppTemplates.CreateCodexDesktop();
        Assert.Equal(LaunchKind.Desktop, codex.LaunchKind);
        Assert.True(codex.UseChromiumProxyFlag);
        Assert.True(codex.UseNodeEnvProxy);
        Assert.Equal(ProxyApplicabilityLevel.Confirmed, codex.ApplicabilityHint);
        Assert.Contains("ChatGPT", codex.RelatedProcessNames);
        Assert.Contains("Codex", codex.RelatedProcessNames);

        Assert.Equal(2, BuiltInAppTemplates.CliTemplates.Count);
        Assert.All(BuiltInAppTemplates.CliTemplates, t => Assert.Equal(LaunchKind.Cli, t.LaunchKind));
    }

    [Fact]
    public void StackInspector_DetectsElectronChromiumWebView2AndNativeAccurately()
    {
        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();

        // 1. Electron (Win32)
        var electronDir = Path.Combine(scope.RootPath, "FakeElectron");
        Directory.CreateDirectory(electronDir);
        var electronExe = Path.Combine(electronDir, "MyApp.exe");
        File.WriteAllBytes(electronExe, [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(electronDir, "LICENSE.electron.txt"), "MIT");

        var electronInfo = discovery.InspectExecutable(electronExe);
        Assert.Equal(AppRuntimeStack.Electron, electronInfo.RuntimeStack);
        Assert.True(electronInfo.SupportsChromiumProxyFlag);
        Assert.Equal(ProxyApplicabilityLevel.LikelyCompatible, electronInfo.ApplicabilityLevel);

        // 2. WebView2
        var wvDir = Path.Combine(scope.RootPath, "FakeWebView2");
        Directory.CreateDirectory(wvDir);
        var wvExe = Path.Combine(wvDir, "WebApp.exe");
        File.WriteAllBytes(wvExe, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(wvDir, "WebView2Loader.dll"), [1, 2]);

        var wvInfo = discovery.InspectExecutable(wvExe);
        Assert.Equal(AppRuntimeStack.WebView2, wvInfo.RuntimeStack);
        Assert.False(wvInfo.SupportsChromiumProxyFlag);
        Assert.Equal(ProxyApplicabilityLevel.Unverified, wvInfo.ApplicabilityLevel);

        // 3. Обычное нативное приложение без маркеров
        var nativeDir = Path.Combine(scope.RootPath, "FakeNative");
        Directory.CreateDirectory(nativeDir);
        var nativeExe = Path.Combine(nativeDir, "PlainApp.exe");
        File.WriteAllBytes(nativeExe, [0x4D, 0x5A]);

        var nativeInfo = discovery.InspectExecutable(nativeExe);
        Assert.Equal(AppRuntimeStack.NativeOrUnknown, nativeInfo.RuntimeStack);
        Assert.False(nativeInfo.SupportsChromiumProxyFlag);
        Assert.Equal(ProxyApplicabilityLevel.Unverified, nativeInfo.ApplicabilityLevel);
    }

    [Fact]
    public void StackInspector_ClassifiesMsixWindowsAppsAlias_AsNeedsVerification()
    {
        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();

        var fakeAlias = @"C:\Users\demo\AppData\Local\Microsoft\WindowsApps\Claude_nonexistent\claude-desktop.exe";
        var info = discovery.InspectExecutable(fakeAlias, BuiltInAppTemplates.ClaudeDesktopKey);

        Assert.Equal(AppPackagingKind.MsixExecutionAlias, info.PackagingKind);
        Assert.Equal(ProxyApplicabilityLevel.NeedsVerification, info.ApplicabilityLevel);
        Assert.NotEmpty(info.Caveats);
    }

    [Fact]
    public void Discovery_DesktopTemplatesProbeDoesNotThrow_OnWindowsHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();

        foreach (var tpl in BuiltInAppTemplates.DesktopTemplates)
        {
            var result = discovery.DiscoverTemplate(tpl.Key);
            if (result.Found)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.ResolvedPath));
                Assert.NotNull(result.StackInfo);
                output.WriteLine($"[{tpl.Key}] FOUND path={result.ResolvedPath} source={result.DisplaySource} pkg={result.StackInfo.PackagingKind} stack={result.StackInfo.RuntimeStack} applicability={result.StackInfo.ApplicabilityLevel} icon={result.StackInfo.IconSourcePath}");
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
                output.WriteLine($"[{tpl.Key}] NOT_FOUND reason={result.ErrorMessage}");
            }
        }
    }

    [Fact]
    public async Task DesktopLaunchStrategy_AddsChromiumFlagWithoutCredentials_AndWarnsOnAuthOrNonChromium()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();
        var desktopStrategy = strategies.OfType<DesktopLaunchStrategy>().Single();

        var fakeNativeDir = Path.Combine(scope.RootPath, "NativeWithFlag");
        Directory.CreateDirectory(fakeNativeDir);
        var exe = Path.Combine(fakeNativeDir, "Tool.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        var app = new ApplicationProfile
        {
            Name = "Native App With Flag",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            UseChromiumProxyFlag = true,
            Arguments = "--mode fast"
        };

        var proxy = new ProxyProfile
        {
            Name = "Auth Proxy",
            Host = "127.0.0.1",
            Port = 8888,
            Username = "alice",
            HasCredentials = true
        };

        var plan = await desktopStrategy.BuildPlanAsync(
            app,
            proxy,
            new ProxyCredentials("alice", "s3cr3t"),
            new AppSettings(),
            isDirectNoProxy: false);

        Assert.Equal(LaunchRoutingStrategy.EnvironmentAndChromium, plan.Strategy);
        Assert.Contains("--proxy-server=http://127.0.0.1:8888", plan.Arguments);
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("s3cr3t", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("alice", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("авториз", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("Electron/Chromium", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LaunchStrategySelection_ChoosesMatchingStrategyByLaunchKind()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();

        var desktopApp = BuiltInAppTemplates.CreateGoogleAntigravity();
        var cliApp = BuiltInAppTemplates.CreateClaudeCodeCli();

        Assert.IsType<DesktopLaunchStrategy>(strategies.Single(s => s.CanHandle(desktopApp)));
        Assert.IsType<CliTerminalLaunchStrategy>(strategies.Single(s => s.CanHandle(cliApp)));
    }

    [Fact]
    public async Task DesktopLaunchStrategy_BuildsConfirmedPlanForCodexAndCustomApp_WithChromiumAndNodeEnvProxy()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();
        var desktopStrategy = strategies.OfType<DesktopLaunchStrategy>().Single();

        var appDir = Path.Combine(scope.RootPath, "FakeOwlElectron");
        Directory.CreateDirectory(appDir);
        var exe = Path.Combine(appDir, "CustomOwlApp.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(appDir, "owl-shell-runtime.json"), "{}");

        var proxy = new ProxyProfile
        {
            Name = "Local Test Proxy",
            Host = "127.0.0.1",
            Port = 8888,
            NoProxy = "localhost,127.0.0.1,::1"
        };

        var app = new ApplicationProfile
        {
            Name = "Custom Owl App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            UseChromiumProxyFlag = true,
            UseNodeEnvProxy = true
        };

        var plan = await desktopStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: false);

        Assert.Equal(LaunchRoutingStrategy.EnvironmentAndChromium, plan.Strategy);
        Assert.Equal("http://127.0.0.1:8888", plan.EnvironmentSet["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", plan.EnvironmentSet["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", plan.EnvironmentSet["ALL_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,::1", plan.EnvironmentSet["NO_PROXY"]);
        Assert.Equal("1", plan.EnvironmentSet["NODE_USE_ENV_PROXY"]);
        Assert.Contains("--proxy-server=http://127.0.0.1:8888", plan.Arguments);

        // Явный --no-proxy-server не должен дублироваться автоматическим --proxy-server
        app.Arguments = "--no-proxy-server";
        var manualPlan = await desktopStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: false);
        Assert.Contains("--no-proxy-server", manualPlan.Arguments);
        Assert.DoesNotContain(manualPlan.Arguments, a => a.StartsWith("--proxy-server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DesktopLaunchStrategy_KeepsClaudeDesktopAsNeedsVerification_WithChromiumAndNodeEnvProxy()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();
        var desktopStrategy = strategies.OfType<DesktopLaunchStrategy>().Single();

        var claudeDir = Path.Combine(scope.RootPath, "Claude_test");
        Directory.CreateDirectory(claudeDir);
        var exe = Path.Combine(claudeDir, "claude-desktop.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(claudeDir, "LICENSE.electron.txt"), "MIT");

        var app = BuiltInAppTemplates.CreateClaudeDesktop(exe);
        var proxy = new ProxyProfile { Name = "Loopback", Host = "127.0.0.1", Port = 8888 };

        var plan = await desktopStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: false);

        Assert.Equal(LaunchRoutingStrategy.EnvironmentAndChromium, plan.Strategy);
        Assert.Equal(ProxyApplicabilityLevel.NeedsVerification, plan.ApplicabilityLevel);
        Assert.Equal("1", plan.EnvironmentSet["NODE_USE_ENV_PROXY"]);
        Assert.Contains("--proxy-server=http://127.0.0.1:8888", plan.Arguments);
    }

    [Fact]
    public async Task CliTerminalLaunchStrategy_PropagatesNodeUseEnvProxyInScriptAndDirectRemoval()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();
        var cliStrategy = strategies.OfType<CliTerminalLaunchStrategy>().Single();

        var cmdPath = Path.Combine(scope.RootPath, "node-cli.cmd");
        await File.WriteAllTextAsync(cmdPath, "@echo off\r\necho ok\r\n");

        var app = new ApplicationProfile
        {
            Name = "Node CLI",
            LaunchKind = LaunchKind.Cli,
            ExecutableOrCommand = cmdPath,
            WorkingDirectory = scope.RootPath,
            LaunchInTerminal = true,
            UseNodeEnvProxy = true
        };

        var proxy = new ProxyProfile { Name = "Loopback", Host = "127.0.0.1", Port = 8899 };

        var proxiedPlan = await cliStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: false);
        Assert.Equal("1", proxiedPlan.EnvironmentSet["NODE_USE_ENV_PROXY"]);
        Assert.False(string.IsNullOrWhiteSpace(proxiedPlan.GeneratedScriptPath));
        var proxiedScript = await File.ReadAllTextAsync(proxiedPlan.GeneratedScriptPath!);
        Assert.Contains("'NODE_USE_ENV_PROXY', '1'", proxiedScript);

        var directPlan = await cliStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: true);
        Assert.Contains("NODE_USE_ENV_PROXY", directPlan.EnvironmentRemove);
        var directScript = await File.ReadAllTextAsync(directPlan.GeneratedScriptPath!);
        Assert.Contains("'NODE_USE_ENV_PROXY', $null", directScript);
    }
}
