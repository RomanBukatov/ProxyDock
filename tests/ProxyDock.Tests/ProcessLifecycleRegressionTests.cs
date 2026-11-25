using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Platform;
using Xunit;

namespace ProxyDock.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProcessLifecycleRegressionTests
{
    private static void WaitUi(Task task, int timeoutSeconds = 25)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, $"UI task timed out after {timeoutSeconds}s.");
        task.GetAwaiter().GetResult();
    }

    private static string CopyTestChildToDirectory(string targetDir, string? exeFileName = null)
    {
        var source = TestPaths.LocateTestChildExecutable();
        var sourceDir = Path.GetDirectoryName(source)!;
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir, "ProxyDock.TestChild.*"))
        {
            var destName = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(exeFileName)
                && destName.Equals("ProxyDock.TestChild.exe", StringComparison.OrdinalIgnoreCase))
            {
                destName = exeFileName;
            }

            File.Copy(file, Path.Combine(targetDir, destName), overwrite: true);
        }

        return Path.Combine(targetDir, string.IsNullOrWhiteSpace(exeFileName) ? Path.GetFileName(source) : exeFileName);
    }

    private static async Task<T> WaitForJsonFileAsync<T>(string path, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var parsed = JsonSerializer.Deserialize<T>(text);
                        if (parsed is not null)
                        {
                            return parsed;
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"Timed out waiting for JSON file {path}");
    }

    private static ApplicationsViewModel CreateApplicationsViewModel(
        TempTestScope scope,
        IDialogService dialogs,
        AppNotificationService notifications,
        IProcessTracker? trackerOverride = null)
    {
        var loc = new LocalizationService();
        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        var proxies = scope.Services.GetRequiredService<IProxyProfileService>();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
        var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();
        var iconCache = new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery);
        var coordinator = new AppLaunchCoordinator(launcher, apps, discovery, dialogs, notifications, loc);
        var tracker = trackerOverride ?? scope.Services.GetRequiredService<IProcessTracker>();

        return new ApplicationsViewModel(loc, apps, proxies, discovery, iconCache, dialogs, coordinator, notifications, tracker);
    }

    private static void TryKillProcessTree(params int[] pids)
    {
        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task ForceQuit_TerminatesMainProcessAndMultipleChildren_AndUpdatesCardToStopped()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var childrenFile = Path.Combine(scope.RootPath, "children.json");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "MultiChild App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe
        });

        using var parent = Process.Start(new ProcessStartInfo(exe, $"--test-window --spawn-children 3 --children-output \"{childrenFile}\" --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        int[] childPids = [];
        try
        {
            childPids = await WaitForJsonFileAsync<int[]>(childrenFile);
            Assert.Equal(3, childPids.Length);

            for (var i = 0; i < 80 && parent.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                parent.Refresh();
            }

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => true);
                var notifications = new AppNotificationService();
                var raised = new List<AppNotification>();
                notifications.NotificationRaised += (_, n) => raised.Add(n);
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                WaitUi(card.ForceCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
                Assert.DoesNotContain(raised, n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Warning);
            });

            Assert.True(parent.WaitForExit(3000));
            foreach (var childPid in childPids)
            {
                Assert.False(IsPidAlive(childPid), $"Child PID {childPid} survived force quit.");
            }
        }
        finally
        {
            TryKillProcessTree([parent.Id, .. childPids]);
        }
    }

    [Fact]
    public async Task SurvivingOrphanChild_ShowsBackgroundState_AndIsKilledByForceQuit()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var childrenFile = Path.Combine(scope.RootPath, "orphan-children.json");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Orphan App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe
        });

        using var parent = Process.Start(new ProcessStartInfo(exe, $"--spawn-children 1 --survive-parent --children-output \"{childrenFile}\" --sleep-ms 800")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        int[] childPids = [];
        try
        {
            childPids = await WaitForJsonFileAsync<int[]>(childrenFile);
            var orphanPid = Assert.Single(childPids);

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => true);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);

                // Ждём естественного завершения родителя, пока потомок продолжает жить
                Assert.True(parent.WaitForExit(5000));
                Assert.True(IsPidAlive(orphanPid));

                // После смерти родителя состояние ДОЛЖНО оставаться Background («Работает в фоне»), а не «Не запущено»!
                WaitUi(vm.RefreshStatesAsync());
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);
                Assert.True(card.ShowStop);

                // Принудительное завершение обязано убрать осиротевшего потомка
                WaitUi(card.ForceCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
            });

            Assert.False(IsPidAlive(orphanPid));
        }
        finally
        {
            TryKillProcessTree([parent.Id, .. childPids]);
        }
    }

    [Fact]
    public async Task GracefulClose_OnCloseToTrayElectronProfile_UsesSessionEnd_AndExitsCleanly()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var markerFile = Path.Combine(scope.RootPath, "graceful-saved.txt");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Tray Electron Sim",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            TemplateKey = BuiltInAppTemplates.ClaudeDesktopKey,
            UseChromiumProxyFlag = true
        });

        using var child = Process.Start(new ProcessStartInfo(exe, $"--tray-window honor-endsession --graceful-marker \"{markerFile}\" --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            for (var i = 0; i < 80 && child.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                child.Refresh();
            }
            Assert.NotEqual(IntPtr.Zero, child.MainWindowHandle);

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                // Не соглашаемся на принудительное завершение — должен сработать именно штатный выход!
                var dialogs = new ScriptedDialogService(_ => false);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                WaitUi(card.CloseCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
                Assert.Empty(dialogs.Prompts);
            });

            Assert.True(child.WaitForExit(3000));
            Assert.Equal(0, child.ExitCode);
            Assert.True(File.Exists(markerFile));
        }
        finally
        {
            TryKillProcessTree(child.Id);
        }
    }

    [Fact]
    public async Task CloseToTrayApp_ThatIgnoresEndSession_NeverForceKillsWithoutConsent_AndAllowsConfirmedForceQuit()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Stubborn Tray App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            UseChromiumProxyFlag = true
        });

        using var child = Process.Start(new ProcessStartInfo(exe, "--tray-window ignore-endsession --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            for (var i = 0; i < 80 && child.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                child.Refresh();
            }

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var allowForce = false;
                var dialogs = new ScriptedDialogService(_ => allowForce);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                // 1. Пользователь отклоняет принудительное завершение -> окно скрылось в трей (Background), процесс НЕ убит!
                WaitUi(card.CloseCommand.ExecuteAsync(null));
                Assert.Single(dialogs.Prompts);
                Assert.False(child.HasExited);
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);

                // 2. Пользователь соглашается -> выполняется принудительное завершение, карточка становится Stopped
                allowForce = true;
                WaitUi(card.CloseCommand.ExecuteAsync(null));
                Assert.Equal(2, dialogs.Prompts.Count);
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
            });

            Assert.True(child.WaitForExit(3000));
        }
        finally
        {
            TryKillProcessTree(child.Id);
        }
    }

    [Fact]
    public async Task CloseToTrayApp_ThatVetoesEndSession_RespectsVeto_AndOffersForceQuitWithConsent()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var markerFile = Path.Combine(scope.RootPath, "veto-should-not-exit.txt");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Veto Tray App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            TemplateKey = BuiltInAppTemplates.CodexDesktopKey,
            UseChromiumProxyFlag = true
        });

        using var child = Process.Start(new ProcessStartInfo(exe, $"--tray-window veto-endsession --graceful-marker \"{markerFile}\" --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            for (var i = 0; i < 80 && child.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                child.Refresh();
            }
            Assert.NotEqual(IntPtr.Zero, child.MainWindowHandle);

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => false);
                var notifications = new AppNotificationService();
                var raised = new List<AppNotification>();
                notifications.NotificationRaised += (_, n) => raised.Add(n);
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                WaitUi(card.CloseCommand.ExecuteAsync(null));
                var prompt = Assert.Single(dialogs.Prompts);
                Assert.Contains("отменило", prompt.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(child.HasExited);
                Assert.False(File.Exists(markerFile));
                Assert.Contains(raised, n => n.Severity == NotificationSeverity.Warning);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);
            });
        }
        finally
        {
            TryKillProcessTree(child.Id);
        }
    }

    [Fact]
    public async Task AutoRespawningHelperProcess_IsFullyTerminatedWithParentFirstMultiPassForceStop()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var respawnLog = Path.Combine(scope.RootPath, "respawn-pids.json");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Watchdog App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe
        });

        using var parent = Process.Start(new ProcessStartInfo(exe, $"--test-window --respawn-child \"{respawnLog}\" --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        int[] helperPids = [];
        try
        {
            helperPids = await WaitForJsonFileAsync<int[]>(respawnLog);
            Assert.NotEmpty(helperPids);

            for (var i = 0; i < 80 && parent.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                parent.Refresh();
            }

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => true);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                WaitUi(card.ForceCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
            });

            helperPids = await WaitForJsonFileAsync<int[]>(respawnLog);
            Assert.True(parent.WaitForExit(3000));
            foreach (var hPid in helperPids)
            {
                Assert.False(IsPidAlive(hPid), $"Respawned helper PID {hPid} survived.");
            }
        }
        finally
        {
            TryKillProcessTree([parent.Id, .. helperPids]);
        }
    }

    [Fact]
    public async Task MultipleIndependentInstances_StoppingTrackedProfileDoesNotKillExternalInstance()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var outTracked = Path.Combine(scope.RootPath, "tracked.json");

        var proxies = scope.Services.GetRequiredService<IProxyProfileService>();
        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        var proxy = await proxies.SaveAsync(new ProxyProfile { Name = "P1", Host = "127.0.0.1", Port = 8081, IsDefault = true });

        var profile = await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Profile Instance A",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            Arguments = $"--output \"{outTracked}\" --sleep-ms 25000",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = proxy.Id
        });

        // Независимый внешний экземпляр того же .exe
        using var externalInstance = Process.Start(new ProcessStartInfo(exe, "--sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        int? trackedPid = null;
        try
        {
            var launcher = scope.Services.GetRequiredService<IApplicationLauncher>();
            var launch = await launcher.LaunchAsync(new LaunchRequest { Application = profile, IgnoreAlreadyRunningWarning = true });
            trackedPid = launch.ProcessId;
            Assert.NotNull(trackedPid);
            await WaitForJsonFileAsync<JsonElement>(outTracked);
            var diagTrack = scope.Services.GetRequiredService<IProcessTracker>();
            var diagSnap = diagTrack.InspectApplication(profile, exe);
            Assert.True(diagSnap.HasActiveTrackedRootForTarget);
            Assert.Equal([trackedPid.Value], diagSnap.ConfirmedProcesses.Select(p => p.ProcessId).ToArray());

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => true);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);

                WaitUi(card.ForceCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);
            });

            // Отслеживаемый экземпляр завершён, а независимый внешний экземпляр остался жив!
            Assert.False(IsPidAlive(trackedPid.Value));
            Assert.True(IsPidAlive(externalInstance.Id));
        }
        finally
        {
            TryKillProcessTree(externalInstance.Id, trackedPid ?? 0);
        }
    }

    [Fact]
    public async Task SameNamedUnrelatedProcessInOtherFolder_IsIgnoredAndNeverTerminated()
    {
        using var scope = new TempTestScope();
        var targetExe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "target-app"), "SameNameApp.exe");
        var unrelatedExe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "other-vendor"), "SameNameApp.exe");

        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Target SameNameApp",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = targetExe
        });

        using var unrelated = Process.Start(new ProcessStartInfo(unrelatedExe, "--sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        using var target = Process.Start(new ProcessStartInfo(targetExe, "--sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => true);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);

                WaitUi(card.ForceCommand.ExecuteAsync(null));
                Assert.Equal(AppCardViewModel.ProcessState.Stopped, card.State);
            });

            Assert.True(target.WaitForExit(3000));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            TryKillProcessTree(unrelated.Id, target.Id);
        }
    }

    [Fact]
    public async Task AccessDeniedOnForceTermination_SurfacesError_BlocksRestart_AndIgnoresTerminatingProcessError()
    {
        var baseTime = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        const string exePath = @"C:\Apps\Demo\Demo.exe";
        var app = new ApplicationProfile { Id = Guid.NewGuid(), Name = "Demo App", ExecutableOrCommand = exePath };

        // 1. Ошибка ERROR_ACCESS_DENIED (5) на живом helper-процессе НЕ прерывает завершение остальных процессов и блокирует перезапуск
        var platform = new FakePlatformOperations
        {
            Entries =
            [
                new(100, 1, "Demo.exe", exePath, "\"Demo.exe\"", baseTime, true, "Demo", TerminateAttemptStatus.Terminated),
                new(101, 100, "Demo.exe", exePath, "\"Demo.exe\" --type=renderer", baseTime.AddSeconds(1), false, null, TerminateAttemptStatus.Failed, ErrorCode: 5, ErrorMessage: "Access is denied."),
                new(102, 100, "Demo.exe", exePath, "\"Demo.exe\" --type=gpu-process", baseTime.AddSeconds(1), false, null, TerminateAttemptStatus.Terminated)
            ]
        };

        using var tracker = new WindowsProcessTracker(platform);
        var initial = tracker.InspectApplication(app, exePath);
        Assert.Equal(3, initial.ConfirmedProcesses.Count);

        var stopResult = await tracker.StopApplicationAsync(app, initial, force: true);
        Assert.Equal(ProcessStopOutcome.PartialOrFailedTermination, stopResult.Outcome);
        Assert.False(stopResult.AllTargetProcessesStopped);
        var blockedProc = Assert.Single(stopResult.RemainingConfirmedProcesses);
        Assert.Equal(101, blockedProc.ProcessId);
        var err = Assert.Single(stopResult.Errors);
        Assert.Equal(101, err.ProcessId);
        Assert.Equal(5, err.NativeErrorCode);
        Assert.DoesNotContain(100, stopResult.RemainingConfirmedProcesses.Select(p => p.ProcessId));
        Assert.DoesNotContain(102, stopResult.RemainingConfirmedProcesses.Select(p => p.ProcessId));

        // Проверка в UI: ошибка видна, карточка остаётся Background (не Stopped!), а Restart не запускает новый экземпляр!
        using var scope = new TempTestScope();
        await scope.Services.GetRequiredService<IApplicationProfileService>().SaveAsync(new ApplicationProfile
        {
            Id = app.Id,
            Name = app.Name,
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exePath
        });

        var launcherCalled = false;
        var stubLauncher = new SpyLauncher(_ => { launcherCalled = true; });
        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            var loc = new LocalizationService();
            var dialogs = new ScriptedDialogService(_ => true);
            var notifications = new AppNotificationService();
            var raised = new List<AppNotification>();
            notifications.NotificationRaised += (_, n) => raised.Add(n);
            var discovery = new StubDiscoveryService(exePath);
            var coordinator = new AppLaunchCoordinator(stubLauncher, scope.Services.GetRequiredService<IApplicationProfileService>(), discovery, dialogs, notifications, loc);
            var vm = new ApplicationsViewModel(loc, scope.Services.GetRequiredService<IApplicationProfileService>(), scope.Services.GetRequiredService<IProxyProfileService>(),
                discovery, new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery), dialogs, coordinator, notifications, tracker);

            WaitUi(vm.RefreshAsync());
            WaitUi(vm.RefreshStatesAsync());

            var card = Assert.Single(vm.FilteredApps);
            Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);

            WaitUi(card.RestartCommand.ExecuteAsync(null));
            Assert.False(launcherCalled);
            Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);
            Assert.Contains(raised, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("101"));
        });

        // 2. Ошибка ERROR_ACCESS_DENIED (5) на процессе в стадии STATUS_PROCESS_IS_TERMINATING (уже завершён к моменту проверки)
        var platformAlreadyExiting = new FakePlatformOperations
        {
            Entries =
            [
                new(200, 1, "Demo.exe", exePath, "\"Demo.exe\"", baseTime, true, "Demo", TerminateAttemptStatus.AlreadyExited)
            ]
        };
        using var tracker2 = new WindowsProcessTracker(platformAlreadyExiting);
        var snap2 = tracker2.InspectApplication(app, exePath);
        var res2 = await tracker2.StopApplicationAsync(app, snap2, force: true);
        Assert.True(res2.AllTargetProcessesStopped);
        Assert.Empty(res2.Errors);
    }

    [Fact]
    public async Task PidReuse_PreventsKillingReusedProcess_AndRejectsFutureTimestampParent()
    {
        var baseTime = new DateTime(2026, 11, 25, 11, 0, 0, DateTimeKind.Utc);
        const string exePath = @"C:\Apps\Demo\Demo.exe";
        var app = new ApplicationProfile { Id = Guid.NewGuid(), Name = "PidReuse App", ExecutableOrCommand = exePath };

        var platform = new FakePlatformOperations
        {
            Entries =
            [
                new(500, 1, "Demo.exe", exePath, "\"Demo.exe\"", baseTime, true, "Demo", TerminateAttemptStatus.CreationTimeMismatch)
            ]
        };

        using var tracker = new WindowsProcessTracker(platform);
        var initial = tracker.InspectApplication(app, exePath);
        var target = Assert.Single(initial.ConfirmedProcesses);

        // При несовпадении времени создания процесс НЕ убивается (имитация переиспользования PID другим процессом)
        platform.SimulateProcessReplaced(500, baseTime.AddMinutes(2));
        var result = await tracker.StopApplicationAsync(app, initial, force: true);
        Assert.Equal(0, platform.KillSucceededCount);
        Assert.DoesNotContain(target.StartedUtc, result.FinalInspection.ConfirmedProcesses.Select(p => p.StartedUtc));

        // Классификатор также отсекает дочернюю цепочку при более позднем старте родителя (PID reuse в PPID)
        var reusedParentCheck = ProcessInstanceClassifier.Classify(
        [
            new(600, null, "Demo", exePath, null, null, null, true, "Demo", true, StartedUtc: baseTime.AddMinutes(5)),
            new(601, 600, "companion", @"C:\Apps\Demo\resources\companion.exe", null, null, null, false, null, false, StartedUtc: baseTime)
        ], new ProcessMatchContext(exePath));
        Assert.Equal([600], reusedParentCheck.ConfirmedProcesses.Select(p => p.ProcessId));
        Assert.Equal([601], reusedParentCheck.AmbiguousProcesses.Select(p => p.ProcessId));
    }

    [Fact]
    public async Task RestartWithNewProxy_FullyTerminatesCloseToTrayTree_AndLaunchesWithUpdatedProxy()
    {
        using var scope = new TempTestScope();
        var exe = CopyTestChildToDirectory(Path.Combine(scope.RootPath, "app"));
        var markerFile = Path.Combine(scope.RootPath, "graceful-restart.txt");
        var launchOut = Path.Combine(scope.RootPath, "relaunched.json");

        var proxies = scope.Services.GetRequiredService<IProxyProfileService>();
        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        var newProxy = await proxies.SaveAsync(new ProxyProfile { Name = "NewProxy", Host = "127.0.0.1", Port = 8899, IsDefault = true });
        await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Restart Tray App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exe,
            TemplateKey = BuiltInAppTemplates.CodexDesktopKey,
            Arguments = $"--output \"{launchOut}\" --sleep-ms 5000",
            ProxyMode = ProxyAssignmentMode.Specific,
            ProxyProfileId = newProxy.Id,
            UseChromiumProxyFlag = true,
            UseNodeEnvProxy = true
        });

        using var runningTrayApp = Process.Start(new ProcessStartInfo(exe, $"--tray-window honor-endsession --graceful-marker \"{markerFile}\" --sleep-ms 25000")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            for (var i = 0; i < 80 && runningTrayApp.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                runningTrayApp.Refresh();
            }

            AvaloniaTestRuntime.RunOnUiThread(() =>
            {
                var dialogs = new ScriptedDialogService(_ => false);
                var notifications = new AppNotificationService();
                var vm = CreateApplicationsViewModel(scope, dialogs, notifications);

                WaitUi(vm.RefreshAsync());
                WaitUi(vm.RefreshStatesAsync());

                var card = Assert.Single(vm.FilteredApps);
                Assert.Equal(AppCardViewModel.ProcessState.Running, card.State);

                WaitUi(card.RestartCommand.ExecuteAsync(null));
                Assert.True(runningTrayApp.HasExited);
            });

            Assert.True(File.Exists(markerFile));
            using var report = await WaitForJsonFileAsync<JsonDocument>(launchOut);
            var newPid = report.RootElement.GetProperty("ProcessId").GetInt32();
            Assert.NotEqual(runningTrayApp.Id, newPid);
            Assert.Equal("http://127.0.0.1:8899", report.RootElement.GetProperty("Environment").GetProperty("HTTPS_PROXY").GetString());
            Assert.Contains("--proxy-server=http://127.0.0.1:8899", report.RootElement.GetProperty("Args").EnumerateArray().Select(e => e.GetString()));
            TryKillProcessTree(newPid);
        }
        finally
        {
            TryKillProcessTree(runningTrayApp.Id);
        }
    }

    [Fact]
    public async Task AmbiguousProcesses_ShowBackgroundState_AndAppearInDiagnosticsWithOwnership()
    {
        var baseTime = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        const string exePath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe";
        const string helperPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\resources\codex.exe";

        var platform = new FakePlatformOperations
        {
            Entries =
            [
                new(700, 1, "codex.exe", helperPath, "codex.exe app-server", baseTime, false, null, TerminateAttemptStatus.Terminated, PkgFamily: "OpenAI.Codex_2p2nqsd0c76g0")
            ]
        };

        using var tracker = new WindowsProcessTracker(platform);
        using var scope = new TempTestScope();
        var apps = scope.Services.GetRequiredService<IApplicationProfileService>();
        var profile = await apps.SaveAsync(new ApplicationProfile
        {
            Name = "Codex Sim",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = exePath,
            RelatedProcessNames = ["ChatGPT", "Codex"]
        });

        var discovery = new StubDiscoveryService(exePath);
        var diagVm = new AppDiagnosticsViewModel(
            profile,
            new LocalizationService(),
            discovery,
            tracker,
            scope.Services.GetRequiredService<ILaunchHistoryRepository>(),
            scope.Services.GetRequiredService<IProxyProfileService>());

        await diagVm.RefreshAsync();
        var row = Assert.Single(diagVm.DetectedProcesses);
        Assert.False(row.IsConfirmedOwnership);
        Assert.Contains("неоднозначно", row.DetailText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PPID 1", row.PidText, StringComparison.Ordinal);

        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            var loc = new LocalizationService();
            var dialogs = new ScriptedDialogService(_ => false);
            var notifications = new AppNotificationService();
            var raised = new List<AppNotification>();
            notifications.NotificationRaised += (_, n) => raised.Add(n);
            var coordinator = new AppLaunchCoordinator(new SpyLauncher(_ => { }), apps, discovery, dialogs, notifications, loc);
            var vm = new ApplicationsViewModel(loc, apps, scope.Services.GetRequiredService<IProxyProfileService>(),
                discovery, new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery), dialogs, coordinator, notifications, tracker);

            WaitUi(vm.RefreshAsync());
            WaitUi(vm.RefreshStatesAsync());

            var card = Assert.Single(vm.FilteredApps);
            // Неоднозначный оставшийся процесс НЕ должен маскироваться под «Не запущено»
            Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);
            WaitUi(card.CloseCommand.ExecuteAsync(null));
            Assert.Contains(raised, n => n.Severity == NotificationSeverity.Warning);
            Assert.Equal(AppCardViewModel.ProcessState.Background, card.State);
        });
    }

    private sealed class ScriptedDialogService(Func<string, bool> onConfirm) : IDialogService
    {
        public List<(string Title, string Message)> Prompts { get; } = [];
        public Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile) => Task.FromResult<ApplicationProfile?>(null);
        public Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile) => Task.FromResult<ProxyProfile?>(null);
        public Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null) => Task.CompletedTask;
        public Task ShowUpdateDialogAsync(UpdateReleaseInfo release) => Task.CompletedTask;
        public Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Продолжить", string? cancelText = "Отмена", bool isDanger = false)
        {
            Prompts.Add((title, message));
            return Task.FromResult(onConfirm(title));
        }
        public Task<string?> PickExecutableFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string defaultFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
    }

    private sealed class SpyLauncher(Action<LaunchRequest> onLaunch) : IApplicationLauncher
    {
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
        {
            onLaunch(request);
            return Task.FromResult(new LaunchResult { Status = LaunchStatus.Started });
        }
    }

    private sealed class StubDiscoveryService(string resolvedPath) : IExecutableDiscoveryService
    {
        public ExecutableDiscoveryResult Resolve(ApplicationProfile profile) => new() { Found = true, ResolvedPath = resolvedPath };
        public ExecutableDiscoveryResult ResolveTarget(LaunchKind kind, string executableOrCommand, string? templateKey = null) => new() { Found = true, ResolvedPath = resolvedPath };
        public ExecutableDiscoveryResult DiscoverTemplate(string templateKey) => new() { Found = false };
        public DesktopAppStackInfo InspectExecutable(string path, string? templateKey = null) => new() { Summary = "Stub" };
        public string? ResolveTerminalExecutable(TerminalPreference preference) => null;
        public IReadOnlyList<TerminalPreference> GetAvailableTerminals() => [];
    }

    private sealed class FakePlatformOperations : IProcessPlatformOperations
    {
        public sealed record FakeEntry(
            int Pid,
            int ParentPid,
            string ExeFile,
            string ExePath,
            string? CommandLine,
            DateTime StartedUtc,
            bool HasVisibleWindow,
            string? WindowTitle,
            TerminateAttemptStatus TerminateBehavior,
            int ErrorCode = 0,
            string? ErrorMessage = null,
            string? PkgFamily = null);

        private readonly HashSet<int> _exited = [];
        private readonly Dictionary<int, DateTime> _overriddenStartTimes = [];

        public List<FakeEntry> Entries { get; init; } = [];
        public int KillSucceededCount { get; private set; }
        public int CurrentProcessId => 999999;
        public int CurrentSessionId => 1;

        public void SimulateProcessReplaced(int pid, DateTime newStartUtc)
            => _overriddenStartTimes[pid] = newStartUtc;

        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses()
            => Entries.Where(e => !_exited.Contains(e.Pid))
                .Select(e => new ProcessSnapshotEntry(e.Pid, e.ParentPid, e.ExeFile))
                .ToList();

        public IReadOnlyList<TopLevelWindowEntry> EnumerateTopLevelWindows(IReadOnlySet<int>? filterPids = null)
            => Entries.Where(e => !_exited.Contains(e.Pid) && e.HasVisibleWindow && (filterPids is null || filterPids.Contains(e.Pid)))
                .Select(e => new TopLevelWindowEntry(new IntPtr(e.Pid), e.Pid, "Chrome_WidgetWin_1", e.WindowTitle, true))
                .ToList();

        public int? TryGetSessionId(int pid) => _exited.Contains(pid) ? null : 1;

        public DateTime? TryGetProcessCreationTimeUtc(int pid)
        {
            if (_exited.Contains(pid)) return null;
            if (_overriddenStartTimes.TryGetValue(pid, out var overridden)) return overridden;
            return Entries.FirstOrDefault(e => e.Pid == pid)?.StartedUtc;
        }

        public string? TryQueryExecutablePath(int pid)
            => _exited.Contains(pid) ? null : Entries.FirstOrDefault(e => e.Pid == pid)?.ExePath;

        public (string? PackageFullName, string? PackageFamilyName) TryGetPackageIdentity(int pid)
        {
            var e = Entries.FirstOrDefault(x => x.Pid == pid);
            return e?.PkgFamily is null ? (null, null) : (e.PkgFamily + "_pkg", e.PkgFamily);
        }

        public string? TryGetCommandLine(int pid)
            => _exited.Contains(pid) ? null : Entries.FirstOrDefault(e => e.Pid == pid)?.CommandLine;

        public bool IsProcessStillActive(int pid, DateTime? expectedStartedUtc)
            => !_exited.Contains(pid) && TryGetProcessCreationTimeUtc(pid) == expectedStartedUtc;

        public bool TryPostCloseToWindow(IntPtr hWnd) => true;

        public SessionEndSignalStatus SendGracefulSessionEnd(IReadOnlyList<TopLevelWindowEntry> windows)
            => SessionEndSignalStatus.NoWindows;

        public TerminateAttemptResult TryTerminateProcess(int pid, DateTime? expectedStartedUtc)
        {
            var entry = Entries.FirstOrDefault(e => e.Pid == pid);
            if (entry is null || _exited.Contains(pid))
            {
                return new TerminateAttemptResult(TerminateAttemptStatus.AlreadyExited);
            }

            if (TryGetProcessCreationTimeUtc(pid) != expectedStartedUtc)
            {
                return new TerminateAttemptResult(TerminateAttemptStatus.CreationTimeMismatch);
            }

            if (entry.TerminateBehavior == TerminateAttemptStatus.Terminated)
            {
                _exited.Add(pid);
                KillSucceededCount++;
                return new TerminateAttemptResult(TerminateAttemptStatus.Terminated);
            }

            if (entry.TerminateBehavior == TerminateAttemptStatus.AlreadyExited)
            {
                _exited.Add(pid);
                return new TerminateAttemptResult(TerminateAttemptStatus.AlreadyExited);
            }

            if (entry.TerminateBehavior == TerminateAttemptStatus.CreationTimeMismatch)
            {
                return new TerminateAttemptResult(TerminateAttemptStatus.CreationTimeMismatch);
            }

            return new TerminateAttemptResult(
                TerminateAttemptStatus.Failed,
                NativeErrorCode: entry.ErrorCode,
                ErrorMessage: entry.ErrorMessage);
        }

        public bool WaitForExit(SafeProcessHandle handle, int timeoutMs) => true;
    }
}
