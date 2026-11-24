using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Platform;
using Xunit;

namespace ProxyDock.Tests;

public sealed class AppLifecycleTests
{
    [Fact]
    public async Task RestartFromCardClosesTestWindowAndLaunchesWithSelectedProxy()
    {
        using var scope = new TempTestScope();
        var source = TestPaths.LocateTestChildExecutable();
        var appDir = Directory.CreateDirectory(Path.Combine(scope.RootPath, "app")).FullName;
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source)!, "ProxyDock.TestChild.*"))
            File.Copy(file, Path.Combine(appDir, Path.GetFileName(file)));
        var exe = Path.Combine(appDir, Path.GetFileName(source));
        var output = Path.Combine(scope.RootPath, "restart.json");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        var proxies = scope.Services.GetRequiredService<IProxyProfileService>();
        var proxy = await proxies.SaveAsync(new ProxyProfile { Name = "Test", Host = "127.0.0.1", Port = 8081 });
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Lifecycle test", ExecutableOrCommand = exe, Arguments = $"--output \"{output}\"",
            ProxyMode = Core.Enums.ProxyAssignmentMode.Specific, ProxyProfileId = proxy.Id,
            UseChromiumProxyFlag = true
        });
        using var child = Process.Start(new ProcessStartInfo(exe, "--test-window")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        })!;
        try
        {
            for (var i = 0; i < 100 && child.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                child.Refresh();
            }
            Assert.NotEqual(IntPtr.Zero, child.MainWindowHandle);
            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var loc = new LocalizationService();
                var dialogs = new UiFixesAndProcessDetectionTests.NoOpDialogService();
                var notifications = new AppNotificationService();
                var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
                var coordinator = new AppLaunchCoordinator(scope.Services.GetRequiredService<IApplicationLauncher>(),
                    apps, discovery, dialogs, notifications, loc);
                var vm = new ApplicationsViewModel(loc, apps, proxies, discovery,
                    new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery),
                    dialogs, coordinator, notifications, scope.Services.GetRequiredService<IProcessTracker>());
                void Wait(Task task)
                {
                    var timeout = Stopwatch.StartNew();
                    while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(10);
                    }
                    Assert.True(task.IsCompleted);
                    task.GetAwaiter().GetResult();
                }
                Wait(vm.RefreshAsync());
                Wait(vm.RefreshStatesAsync());
                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(loc["StateRunning"], card.RuntimeState);
                Wait(card.ManageAsync!(card, AppCardViewModel.LifecycleAction.Force));
                Assert.False(child.HasExited); // The confirmation dialog declined.
                Wait(card.ManageAsync!(card, AppCardViewModel.LifecycleAction.Restart));
            });
            Assert.True(child.HasExited);
            Assert.Equal(0, child.ExitCode);
            for (var i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(50);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.NotEqual(child.Id, report.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.Equal("http://127.0.0.1:8081",
                report.RootElement.GetProperty("Environment").GetProperty("HTTPS_PROXY").GetString());
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public void OwnershipRequiresExactImageOrConfirmedParent()
    {
        const string image = @"C:\Package\ChatGPT.exe";
        var now = DateTime.UtcNow;
        RawProcessCandidate Candidate(int pid, int? parent, string path, int seconds) =>
            new(pid, parent, Path.GetFileNameWithoutExtension(path), path, null, null, "Family",
                false, null, true, StartedUtc: now.AddSeconds(seconds));

        var result = ProcessInstanceClassifier.Classify([
            Candidate(100, null, image, 2),
            Candidate(101, 100, @"C:\Package\resources\codex.exe", 3),
            Candidate(102, 100, @"C:\Package\resources\codex.exe", 1),
            Candidate(103, null, @"C:\Package\resources\codex.exe", 4),
            Candidate(104, null, @"C:\Other\ChatGPT.exe", 4)
        ], new ProcessMatchContext(image, ExpectedPackageFamilyName: "Family"));

        Assert.Equal(new[] { 100, 101 }, result.ConfirmedProcesses.Select(p => p.ProcessId).Order());
    }

    [Fact]
    public async Task FreshTrackerRecoversProcess_AndForceRequiresOriginalStartTime()
    {
        var path = TestPaths.LocateTestChildExecutable();
        using var child = Process.Start(new ProcessStartInfo(path, "--sleep-ms 15000")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!;
        try
        {
            using var tracker = new WindowsProcessTracker();
            var snapshot = tracker.InspectRunningInstances(path);
            var target = Assert.Single(snapshot.ConfirmedProcesses, p => p.ProcessId == child.Id);
            Assert.NotNull(target.StartedUtc);
            RunningAppInspection Only(DetectedProcessInfo info) => new()
            {
                ExpectedExecutablePath = path, Processes = [info]
            };
            tracker.RequestStop(Only(new DetectedProcessInfo
            {
                ProcessId = child.Id, StartedUtc = target.StartedUtc.Value.AddMinutes(-1),
                MatchedByExecutablePath = true, ExecutablePath = path
            }), force: true);
            Assert.False(child.HasExited);
            tracker.RequestStop(Only(target), force: false);
            Assert.False(child.HasExited);
            tracker.RequestStop(Only(target), force: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
