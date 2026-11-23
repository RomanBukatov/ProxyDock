using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProcessLaunchIntegrationTests
{
    private sealed record ChildOutput(
        int ProcessId,
        string WorkingDirectory,
        string[] Args,
        Dictionary<string, string?> Environment);

    [Fact]
    public async Task LaunchAsync_PassesProxyEnvAndChromiumFlagToChild_WithoutMutatingParentOrWindowsProxy()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();

        var parentHttpProxyBefore = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var (winProxyEnableBefore, winProxyServerBefore) = ReadWindowsInternetSettings();

        var testChildExe = TestPaths.LocateTestChildExecutable();
        var outFile = Path.Combine(scope.RootPath, "antigravity-sim-out.json");

        var proxy = await proxyService.SaveAsync(new ProxyProfile
        {
            Name = "Proxy A",
            Host = "127.0.0.1",
            Port = 8888,
            NoProxy = "localhost,127.0.0.1",
            IsDefault = true
        });

        var app = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "Antigravity Regression Sim",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = testChildExe,
            WorkingDirectory = scope.RootPath,
            Arguments = $"--output \"{outFile}\" --custom \"value with spaces\"",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = proxy.Id,
            UseChromiumProxyFlag = true,
            CustomEnvironmentVariables =
            [
                new EnvironmentVariableEntry { Key = "PROXYDOCK_TEST_APP", Value = "antigravity" }
            ]
        });

        var result = await launcher.LaunchAsync(new LaunchRequest { Application = app });

        Assert.True(result.Status is LaunchStatus.Started or LaunchStatus.StartedWithWarning);
        Assert.Equal(LaunchRoutingStrategy.EnvironmentAndChromium, result.Strategy);
        Assert.NotNull(result.ProcessId);

        var output = await WaitForChildOutputAsync(outFile);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["ALL_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["http_proxy"]);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["https_proxy"]);
        Assert.Equal("http://127.0.0.1:8888", output.Environment["all_proxy"]);
        Assert.Equal("localhost,127.0.0.1", output.Environment["NO_PROXY"]);
        Assert.Equal("antigravity", output.Environment["PROXYDOCK_TEST_APP"]);
        Assert.Contains("--proxy-server=http://127.0.0.1:8888", output.Args);
        Assert.Contains("value with spaces", output.Args);

        // Родительский процесс и системный прокси Windows абсолютно не затронуты
        Assert.Equal(parentHttpProxyBefore, Environment.GetEnvironmentVariable("HTTP_PROXY"));
        var (winProxyEnableAfter, winProxyServerAfter) = ReadWindowsInternetSettings();
        Assert.Equal(winProxyEnableBefore, winProxyEnableAfter);
        Assert.Equal(winProxyServerBefore, winProxyServerAfter);
    }

    [Fact]
    public async Task LaunchAsync_IsolatesConcurrentAppsWithDifferentProxiesAndDirectMode()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();

        var testChildExe = TestPaths.LocateTestChildExecutable();
        var proxyA = await proxyService.SaveAsync(new ProxyProfile { Name = "Proxy A", Host = "127.0.0.1", Port = 8081 });
        var proxyB = await proxyService.SaveAsync(new ProxyProfile { Name = "Proxy B", Host = "127.0.0.1", Port = 8082 });

        var outA = Path.Combine(scope.RootPath, "out-a.json");
        var outB = Path.Combine(scope.RootPath, "out-b.json");
        var outDirect = Path.Combine(scope.RootPath, "out-direct.json");

        var appA = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "App A",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = testChildExe,
            Arguments = $"--output \"{outA}\"",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = proxyA.Id
        });

        var appB = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "App B",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = testChildExe,
            Arguments = $"--output \"{outB}\"",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = proxyB.Id
        });

        var appDirect = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "App Direct",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = testChildExe,
            Arguments = $"--output \"{outDirect}\"",
            ProxyMode = ProxyAssignmentMode.DirectNoProxy
        });

        var r1 = launcher.LaunchAsync(new LaunchRequest { Application = appA, IgnoreAlreadyRunningWarning = true });
        var r2 = launcher.LaunchAsync(new LaunchRequest { Application = appB, IgnoreAlreadyRunningWarning = true });
        var r3 = launcher.LaunchAsync(new LaunchRequest { Application = appDirect, IgnoreAlreadyRunningWarning = true });
        await Task.WhenAll(r1, r2, r3);

        var dataA = await WaitForChildOutputAsync(outA);
        var dataB = await WaitForChildOutputAsync(outB);
        var dataDirect = await WaitForChildOutputAsync(outDirect);

        Assert.Equal("http://127.0.0.1:8081", dataA.Environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:8082", dataB.Environment["HTTPS_PROXY"]);
        Assert.Null(dataDirect.Environment["HTTP_PROXY"]);
        Assert.Null(dataDirect.Environment["HTTPS_PROXY"]);
    }

    [Fact]
    public async Task LaunchAsync_ReturnsStructuredFailure_WhenExecutableIsMissing()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();

        await proxyService.SaveAsync(new ProxyProfile { Name = "P", Host = "127.0.0.1", Port = 8080, IsDefault = true });
        var app = new ApplicationProfile
        {
            Name = "Missing Exe",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = Path.Combine(scope.RootPath, "DoesNotExist.exe")
        };

        var result = await launcher.LaunchAsync(new LaunchRequest { Application = app });

        Assert.Equal(LaunchStatus.Failed, result.Status);
        Assert.Contains("не найден", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_DetectsAlreadyRunningDesktopProcess_WithoutKillingIt()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();

        await proxyService.SaveAsync(new ProxyProfile { Name = "P", Host = "127.0.0.1", Port = 8080, IsDefault = true });
        var testChildExe = TestPaths.LocateTestChildExecutable();
        var sleepOut = Path.Combine(scope.RootPath, "sleep-out.json");

        var app = new ApplicationProfile
        {
            Name = "Sleeping App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = testChildExe,
            Arguments = $"--output \"{sleepOut}\" --sleep-ms 10000"
        };

        var first = await launcher.LaunchAsync(new LaunchRequest { Application = app, IgnoreAlreadyRunningWarning = true });
        Assert.NotNull(first.ProcessId);

        Process? runningProc = null;
        try
        {
            await WaitForChildOutputAsync(sleepOut);
            runningProc = Process.GetProcessById(first.ProcessId!.Value);

            var tracker = scope.Services.GetRequiredService<IProcessTracker>();
            var relatedPids = tracker.FindRunningInstances(@"C:\Fake\claude-desktop.exe", ["ProxyDock.TestChild"]);
            Assert.Contains(first.ProcessId.Value, relatedPids);

            var second = await launcher.LaunchAsync(new LaunchRequest { Application = app, IgnoreAlreadyRunningWarning = false });
            Assert.Equal(LaunchStatus.Cancelled, second.Status);
            Assert.True(second.RequiresExistingProcessConfirmation);
            Assert.Contains(first.ProcessId.Value, second.AlreadyRunningProcessIds);
            Assert.False(runningProc.HasExited); // ProxyDock не убивает процесс!
        }
        finally
        {
            if (runningProc is not null)
            {
                try
                {
                    if (!runningProc.HasExited)
                    {
                        runningProc.Kill();
                    }
                }
                catch
                {
                }

                runningProc.Dispose();
            }
        }
    }

    private static async Task<ChildOutput> WaitForChildOutputAsync(string filePath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(filePath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var parsed = JsonSerializer.Deserialize<ChildOutput>(text);
                        if (parsed is not null)
                        {
                            return parsed;
                        }
                    }
                }
                catch (IOException)
                {
                    // Дочерний процесс ещё дописывает файл
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Файл отчёта дочернего процесса не создан вовремя: {filePath}");
    }

    private static (object? Enable, object? Server) ReadWindowsInternetSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
        return (key?.GetValue("ProxyEnable"), key?.GetValue("ProxyServer"));
    }
}
