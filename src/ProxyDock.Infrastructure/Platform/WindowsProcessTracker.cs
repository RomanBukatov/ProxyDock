using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Platform;

internal sealed record ProcessSnapshotEntry(
    int ProcessId,
    int ParentProcessId,
    string ExeFileName);

internal sealed record TopLevelWindowEntry(
    IntPtr Handle,
    int ProcessId,
    string ClassName,
    string? Title,
    bool IsVisible);

internal enum TerminateAttemptStatus
{
    Terminated = 0,
    AlreadyExited = 1,
    CreationTimeMismatch = 2,
    Failed = 3
}

internal sealed record TerminateAttemptResult(
    TerminateAttemptStatus Status,
    SafeProcessHandle? WaitHandle = null,
    int NativeErrorCode = 0,
    string? ErrorMessage = null);

internal enum SessionEndSignalStatus
{
    Sent = 0,
    Vetoed = 1,
    NoWindows = 2
}

internal interface IProcessPlatformOperations
{
    int CurrentProcessId { get; }
    int CurrentSessionId { get; }
    IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses();
    IReadOnlyList<TopLevelWindowEntry> EnumerateTopLevelWindows(IReadOnlySet<int>? filterPids = null);
    int? TryGetSessionId(int pid);
    DateTime? TryGetProcessCreationTimeUtc(int pid);
    string? TryQueryExecutablePath(int pid);
    (string? PackageFullName, string? PackageFamilyName) TryGetPackageIdentity(int pid);
    string? TryGetCommandLine(int pid);
    bool IsProcessStillActive(int pid, DateTime? expectedStartedUtc);
    bool TryPostCloseToWindow(IntPtr hWnd);
    SessionEndSignalStatus SendGracefulSessionEnd(IReadOnlyList<TopLevelWindowEntry> windows);
    TerminateAttemptResult TryTerminateProcess(int pid, DateTime? expectedStartedUtc);
    bool WaitForExit(SafeProcessHandle handle, int timeoutMs);
}

/// <summary>
/// Отслеживает запущенные процессы событийно (без фонового опроса) и не завершает их при закрытии ProxyDock.
/// Выполняет полную инспекцию процессов пользовательской сессии (MSIX identity, видимые и скрытые окна,
/// дерево Electron/Chromium и вспомогательных процессов), штатное завершение и многопроходное принудительное завершение.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessTracker : IProcessTracker
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ProcessCommandLineInformation = 60;
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int StillActive = 259;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaCloaked = 14;
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint EndSessionCloseApp = 0x00000001;
    private const uint SmtoAbortIfHung = 0x0002;

    private readonly ConcurrentDictionary<int, (TrackedProcessInfo Info, Process Process, EventHandler ExitHandler)> _tracked = new();
    private readonly IProcessPlatformOperations _platform;

    public WindowsProcessTracker()
        : this(Win32ProcessPlatformOperations.Instance)
    {
    }

    internal WindowsProcessTracker(IProcessPlatformOperations platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public event EventHandler<TrackedProcessInfo>? ProcessExited;

    public IReadOnlyCollection<TrackedProcessInfo> ActiveProcesses
        => _tracked.Values.Select(v => v.Info).Where(i => !i.HasExited).ToList();

    public void Track(Process process, ApplicationProfile application, string resolvedTarget)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(application);

        var pid = process.Id;
        DateTime? creationUtc = _platform.TryGetProcessCreationTimeUtc(pid);
        if (creationUtc is null)
        {
            try
            {
                creationUtc = process.StartTime.ToUniversalTime();
            }
            catch
            {
                // Игнорируем отказ при чтении StartTime
            }
        }

        var info = new TrackedProcessInfo
        {
            ProcessId = pid,
            ApplicationId = application.Id,
            ApplicationName = application.Name,
            ExecutablePath = resolvedTarget,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessCreationTimeUtc = creationUtc
        };

        EventHandler exitHandler = (_, _) =>
        {
            info.HasExited = true;
            if (_tracked.TryRemove(pid, out var removed))
            {
                try
                {
                    removed.Process.EnableRaisingEvents = false;
                    removed.Process.Exited -= removed.ExitHandler;
                    removed.Process.Dispose();
                }
                catch
                {
                    // Игнорируем при освобождении
                }
            }

            ProcessExited?.Invoke(this, info);
        };

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += exitHandler;
            if (process.HasExited)
            {
                process.Exited -= exitHandler;
                info.HasExited = true;
                process.Dispose();
                return;
            }

            _tracked[pid] = (info, process, exitHandler);
        }
        catch
        {
            process.Exited -= exitHandler;
            process.Dispose();
        }
    }

    public IReadOnlyList<int> FindRunningInstances(
        string resolvedExecutablePath,
        IReadOnlyCollection<string>? relatedProcessNames = null)
        => InspectRunningInstances(resolvedExecutablePath, relatedProcessNames).ActiveMainProcessIds;

    public RunningAppInspection InspectRunningInstances(
        string resolvedExecutablePath,
        IReadOnlyCollection<string>? relatedProcessNames = null,
        string? templateKey = null)
        => InspectCore(
            application: null,
            resolvedExecutablePath: resolvedExecutablePath,
            relatedProcessNames: relatedProcessNames,
            templateKey: templateKey,
            previousInspection: null);

    public RunningAppInspection InspectApplication(
        ApplicationProfile application,
        string resolvedExecutablePath,
        RunningAppInspection? previousInspection = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        return InspectCore(
            application: application,
            resolvedExecutablePath: resolvedExecutablePath,
            relatedProcessNames: application.RelatedProcessNames,
            templateKey: application.TemplateKey,
            previousInspection: previousInspection);
    }

    private RunningAppInspection InspectCore(
        ApplicationProfile? application,
        string resolvedExecutablePath,
        IReadOnlyCollection<string>? relatedProcessNames,
        string? templateKey,
        RunningAppInspection? previousInspection)
    {
        var normalizedTargetPath = NormalizePathSafe(resolvedExecutablePath);
        var targetName = Path.GetFileNameWithoutExtension(normalizedTargetPath);
        var isWindowsAppsAlias = MsixPackageResolver.IsWindowsAppsAliasPath(normalizedTargetPath);

        var link = MsixPackageResolver.TryReadAppExecLink(normalizedTargetPath);
        var msixPkg = MsixPackageResolver.TryResolvePackage(normalizedTargetPath, templateKey);

        var expectedFamily = link?.PackageFamilyName ?? msixPkg?.PackageFamilyName;
        var expectedPackageRoot = msixPkg?.PackageRoot;
        var expectedTargetExe = link?.TargetExecutablePath ?? msixPkg?.TargetExecutablePath;
        var hasMsixContext = !string.IsNullOrWhiteSpace(expectedFamily) || !string.IsNullOrWhiteSpace(expectedPackageRoot);

        var expectedMainExe = !string.IsNullOrWhiteSpace(expectedTargetExe)
            ? NormalizePathSafe(expectedTargetExe)
            : normalizedTargetPath;
        var expectedMainDir = GetSafeInstallRoot(expectedMainExe);

        var processNamesToCheck = BuildProcessNameSet(targetName, expectedTargetExe, relatedProcessNames);
        var currentSessionId = _platform.CurrentSessionId;
        var currentPid = _platform.CurrentProcessId;

        // Определяем активные отслеживаемые процессы заранее, чтобы не потерять короткоживущие или отложенно-собираемые записи.
        var trackedActiveSnapshot = _tracked.Values
            .Where(v => !v.Info.HasExited)
            .ToDictionary(v => v.Info.ProcessId, v => v.Info);

        var snapshots = _platform.SnapshotProcesses();
        var servicesPids = snapshots
            .Where(s => string.Equals(Path.GetFileNameWithoutExtension(s.ExeFileName), "services", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ProcessId)
            .ToHashSet();

        var parentByPid = new Dictionary<int, int>();
        var snapByPid = new Dictionary<int, ProcessSnapshotEntry>();
        foreach (var snap in snapshots)
        {
            parentByPid[snap.ProcessId] = snap.ParentProcessId;
            snapByPid[snap.ProcessId] = snap;
        }

        // Сначала выполняем быстрый проход по имени и отслеженным PID (без тяжёлых Win32-вызовов для сотен системных процессов),
        // а полный проход по путям / MSIX / потомкам выполняем без открытия лишних дескрипторов на уже отфильтрованные системные процессы.
        var sessionMeta = new Dictionary<int, (int ParentPid, string ProcName, DateTime? StartedUtc, string? ExePath, string? PkgFull, string? PkgFamily)>();
        foreach (var snap in snapshots)
        {
            if (snap.ProcessId == 0 || snap.ProcessId == currentPid || servicesPids.Contains(snap.ParentProcessId))
            {
                continue;
            }

            var procName = Path.GetFileNameWithoutExtension(snap.ExeFileName);
            var isTracked = trackedActiveSnapshot.ContainsKey(snap.ProcessId);

            // Быстрый фильтр: пропускаем явные посторонние процессы, если они не совпадают по имени, не являются MSIX-кандидатами и не находятся в потомках
            var nameCandidate = processNamesToCheck.Contains(procName);
            var parentIsCandidate = snapByPid.TryGetValue(snap.ParentProcessId, out var pSnap)
                && (processNamesToCheck.Contains(Path.GetFileNameWithoutExtension(pSnap.ExeFileName)) || trackedActiveSnapshot.ContainsKey(pSnap.ProcessId));

            // Дополнительно включаем прямых и косвенных потомков уже найденных по имени/трекингу процессов
            var ancestorIsCandidate = false;
            if (!nameCandidate && !isTracked && !parentIsCandidate && !hasMsixContext)
            {
                var cur = snap.ParentProcessId;
                for (var hop = 0; hop < 6 && cur != 0 && snapByPid.TryGetValue(cur, out var anc); hop++)
                {
                    var ancName = Path.GetFileNameWithoutExtension(anc.ExeFileName);
                    if (processNamesToCheck.Contains(ancName) || trackedActiveSnapshot.ContainsKey(anc.ProcessId))
                    {
                        ancestorIsCandidate = true;
                        break;
                    }

                    cur = anc.ParentProcessId;
                }
            }

            if (!nameCandidate && !isTracked && !parentIsCandidate && !ancestorIsCandidate && !hasMsixContext)
            {
                continue;
            }

            var sid = _platform.TryGetSessionId(snap.ProcessId);
            if (sid is null || sid.Value != currentSessionId)
            {
                continue;
            }

            var startedUtc = _platform.TryGetProcessCreationTimeUtc(snap.ProcessId);
            if (startedUtc is null)
            {
                continue;
            }

            var exePath = _platform.TryQueryExecutablePath(snap.ProcessId);
            string? pkgFull = null;
            string? pkgFamily = null;
            if (hasMsixContext || (!string.IsNullOrWhiteSpace(exePath) && exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
            {
                (pkgFull, pkgFamily) = _platform.TryGetPackageIdentity(snap.ProcessId);
            }

            sessionMeta[snap.ProcessId] = (snap.ParentProcessId, procName, startedUtc, exePath, pkgFull, pkgFamily);
        }

        // Определяем активные корневые процессы, отслеживаемые конкретно для этого ApplicationProfile.
        var activeTrackedTargetPids = new HashSet<int>();
        if (application is not null)
        {
            foreach (var (pid, trackedInfo) in trackedActiveSnapshot)
            {
                if (trackedInfo.ApplicationId != application.Id)
                {
                    continue;
                }

                if (sessionMeta.TryGetValue(pid, out var meta)
                    && (trackedInfo.ProcessCreationTimeUtc is null
                        || meta.StartedUtc is null
                        || Math.Abs((meta.StartedUtc.Value - trackedInfo.ProcessCreationTimeUtc.Value).TotalSeconds) <= 2))
                {
                    activeTrackedTargetPids.Add(pid);
                }
            }
        }

        // Отбираем прямых кандидатов (по MSIX identity, пути, имени или отслеживанию ProxyDock).
        var selectedPids = new HashSet<int>();
        foreach (var (pid, meta) in sessionMeta)
        {
            var normalizedExe = string.IsNullOrWhiteSpace(meta.ExePath) ? null : NormalizePathSafe(meta.ExePath);
            var matchesFamily = !string.IsNullOrWhiteSpace(expectedFamily)
                && !string.IsNullOrWhiteSpace(meta.PkgFamily)
                && string.Equals(meta.PkgFamily, expectedFamily, StringComparison.OrdinalIgnoreCase);

            var matchesPkgRoot = !string.IsNullOrWhiteSpace(expectedPackageRoot)
                && !string.IsNullOrWhiteSpace(normalizedExe)
                && IsPathUnderRoot(normalizedExe, NormalizePathSafe(expectedPackageRoot));

            var matchesExpectedPath = !string.IsNullOrWhiteSpace(normalizedExe)
                && (string.Equals(normalizedExe, expectedMainExe, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(expectedMainDir) && IsPathUnderRoot(normalizedExe, expectedMainDir)));

            var matchesName = processNamesToCheck.Contains(meta.ProcName);
            var matchesTracked = activeTrackedTargetPids.Contains(pid)
                || IsTrackedForOtherApp(pid, meta.StartedUtc, application?.Id);

            if (matchesFamily || matchesPkgRoot || matchesExpectedPath || matchesName || matchesTracked)
            {
                selectedPids.Add(pid);
            }
        }

        // Дополнительно подтягиваем потомков уже выбранных кандидатов, если их бинарник находится в папке пакета/установки
        // или совпадает с процессом-родителем (многоступенчатое дерево процессов, например Electron -> sidecar).
        bool addedAny;
        do
        {
            addedAny = false;
            foreach (var (pid, meta) in sessionMeta)
            {
                if (selectedPids.Contains(pid))
                {
                    continue;
                }

                if (!selectedPids.Contains(meta.ParentPid))
                {
                    continue;
                }

                if (!sessionMeta.TryGetValue(meta.ParentPid, out var parentMeta))
                {
                    continue;
                }

                if (parentMeta.StartedUtc.HasValue && meta.StartedUtc.HasValue && parentMeta.StartedUtc.Value > meta.StartedUtc.Value)
                {
                    continue;
                }

                var normalizedExe = string.IsNullOrWhiteSpace(meta.ExePath) ? null : NormalizePathSafe(meta.ExePath);
                var underAppDir = !string.IsNullOrWhiteSpace(normalizedExe)
                    && ((!string.IsNullOrWhiteSpace(expectedPackageRoot) && IsPathUnderRoot(normalizedExe, NormalizePathSafe(expectedPackageRoot)))
                        || (!string.IsNullOrWhiteSpace(expectedMainDir) && IsPathUnderRoot(normalizedExe, expectedMainDir)));

                var sameFamily = !string.IsNullOrWhiteSpace(expectedFamily)
                    && string.Equals(meta.PkgFamily, expectedFamily, StringComparison.OrdinalIgnoreCase);

                if (underAppDir || sameFamily)
                {
                    selectedPids.Add(pid);
                    addedAny = true;
                }
            }
        }
        while (addedAny);

        // Включаем потомков, переживших удаление/выход промежуточного родителя:
        // проверяем цепочку предков глубиной до 8 шагов с монотонной проверкой времени старта.
        var ancestorByPid = new Dictionary<int, int?>();
        foreach (var pid in selectedPids)
        {
            ancestorByPid[pid] = ResolveNearestSelectedAncestor(pid, selectedPids, sessionMeta, parentByPid);
        }

        var visibleWindows = selectedPids.Count == 0
            ? new Dictionary<int, string?>()
            : BuildVisibleWindowMap(_platform.EnumerateTopLevelWindows(selectedPids));
        var rawCandidates = new List<RawProcessCandidate>(selectedPids.Count);

        foreach (var pid in selectedPids)
        {
            var meta = sessionMeta[pid];
            var commandLine = _platform.TryGetCommandLine(pid);
            visibleWindows.TryGetValue(pid, out var windowTitle);
            var hasVisibleWindow = visibleWindows.ContainsKey(pid);

            var matchedByPrimaryName = string.Equals(meta.ProcName, targetName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(expectedTargetExe)
                    && string.Equals(meta.ProcName, Path.GetFileNameWithoutExtension(expectedTargetExe), StringComparison.OrdinalIgnoreCase));

            var isTrackedForTarget = activeTrackedTargetPids.Contains(pid);
            var isTrackedForOther = IsTrackedForOtherApp(pid, meta.StartedUtc, application?.Id);

            rawCandidates.Add(new RawProcessCandidate(
                ProcessId: pid,
                ParentProcessId: meta.ParentPid,
                ProcessName: meta.ProcName,
                ExecutablePath: meta.ExePath,
                CommandLine: commandLine,
                PackageFullName: meta.PkgFull,
                PackageFamilyName: meta.PkgFamily,
                HasVisibleWindow: hasVisibleWindow,
                MainWindowTitle: windowTitle,
                MatchedByPrimaryName: matchedByPrimaryName,
                IsTrackedByProxyDock: isTrackedForTarget || (application is null && IsTrackedPidMatch(pid, meta.StartedUtc)),
                StartedUtc: meta.StartedUtc,
                IsTrackedForTargetProfile: isTrackedForTarget,
                IsTrackedForOtherProfile: isTrackedForOther,
                AncestorCandidateProcessId: ancestorByPid.GetValueOrDefault(pid),
                IsSystemService: servicesPids.Contains(meta.ParentPid)));
        }

        var expectedUserDataDir = ProcessInstanceClassifier.ExtractChromiumUserDataDir(application?.Arguments);
        // Если хотя бы один процесс выбранного профиля был запущен этим экземпляром ProxyDock (activeTrackedTargetPids.Count > 0),
        // или ещё недавно отслеживался для него (в последние секунды при остановке), не подхватываем внешние независимые экземпляры.
        var hasTrackedForProfile = activeTrackedTargetPids.Count > 0
            || (previousInspection?.HasActiveTrackedRootForTarget == true && previousInspection.ConfirmedProcesses.Count > 0);

        var matchContext = new ProcessMatchContext(
            ExpectedExecutablePath: normalizedTargetPath,
            ExpectedTargetExecutablePath: expectedTargetExe,
            ExpectedPackageFamilyName: expectedFamily,
            ExpectedPackageRoot: expectedPackageRoot,
            IsWindowsAppsAlias: isWindowsAppsAlias,
            ExpectedUserDataDir: expectedUserDataDir,
            HasActiveTrackedRootForTarget: hasTrackedForProfile);

        var inspection = ProcessInstanceClassifier.Classify(rawCandidates, matchContext);
        inspection.RetainVerifiedInstances(previousInspection);
        return inspection;
    }

    public void RequestStop(RunningAppInspection inspection, bool force)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var targets = OrderTargetsForTermination(inspection.ConfirmedProcesses);
        if (!force)
        {
            foreach (var candidate in targets)
            {
                if (candidate.Role is DetectedProcessRole.PrimaryWithVisibleWindow or DetectedProcessRole.PrimaryTrayOrBackground)
                {
                    TryClosePrimaryWindows(candidate.ProcessId);
                }
            }

            return;
        }

        var waitHandles = new List<SafeProcessHandle>();
        try
        {
            foreach (var candidate in targets)
            {
                if (candidate.StartedUtc is null)
                {
                    continue;
                }

                var attempt = _platform.TryTerminateProcess(candidate.ProcessId, candidate.StartedUtc);
                if (attempt.WaitHandle is not null)
                {
                    waitHandles.Add(attempt.WaitHandle);
                }
            }

            WaitAllBounded(waitHandles, totalTimeoutMs: 500);
        }
        finally
        {
            foreach (var h in waitHandles)
            {
                h.Dispose();
            }
        }
    }

    public async Task<ProcessStopResult> StopApplicationAsync(
        ApplicationProfile application,
        RunningAppInspection inspection,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(inspection);

        var targetPath = inspection.ExpectedExecutablePath ?? application.ExecutableOrCommand;
        if (!force)
        {
            return await StopGracefullyAsync(application, targetPath, inspection, cancellationToken).ConfigureAwait(false);
        }

        return await StopForcefullyAsync(application, targetPath, inspection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessStopResult> StopGracefullyAsync(
        ApplicationProfile application,
        string targetPath,
        RunningAppInspection initialInspection,
        CancellationToken cancellationToken)
    {
        var current = initialInspection;
        var confirmed = current.ConfirmedProcesses;
        if (confirmed.Count == 0)
        {
            var ambiguous = current.AmbiguousProcesses;
            return new ProcessStopResult
            {
                Outcome = ambiguous.Count > 0 ? ProcessStopOutcome.AmbiguousRemaining : ProcessStopOutcome.AllStopped,
                FinalInspection = current,
                RemainingConfirmedProcesses = confirmed,
                RemainingAmbiguousProcesses = ambiguous,
                GracefulStrategyUsed = "None"
            };
        }

        var strategyName = ResolveGracefulStrategy(application, current);
        var primaryPids = confirmed
            .Where(p => p.Role is DetectedProcessRole.PrimaryWithVisibleWindow or DetectedProcessRole.PrimaryTrayOrBackground)
            .Select(p => p.ProcessId)
            .ToHashSet();

        if (primaryPids.Count == 0)
        {
            // Остались только дочерние/фоновые процессы без главного окна — для них штатный выход окна недоступен.
            return new ProcessStopResult
            {
                Outcome = ProcessStopOutcome.StillRunningAfterGraceful,
                FinalInspection = current,
                RemainingConfirmedProcesses = confirmed,
                RemainingAmbiguousProcesses = current.AmbiguousProcesses,
                GracefulStrategyUsed = "NoPrimaryWindow"
            };
        }

        if (strategyName == "SessionEndAppExit")
        {
            var status = SendSessionEndToPids(primaryPids);
            if (status == SessionEndSignalStatus.Vetoed)
            {
                var afterVeto = InspectApplication(application, targetPath, current);
                return new ProcessStopResult
                {
                    Outcome = ProcessStopOutcome.GracefulVetoed,
                    FinalInspection = afterVeto,
                    RemainingConfirmedProcesses = afterVeto.ConfirmedProcesses,
                    RemainingAmbiguousProcesses = afterVeto.AmbiguousProcesses,
                    GracefulStrategyUsed = strategyName
                };
            }

            current = await PollUntilStoppedAsync(application, targetPath, current, timeoutMs: 4500, pollIntervalMs: 200, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            TryClosePrimaryWindowsBatch(primaryPids);
            current = await PollUntilStoppedAsync(application, targetPath, current, timeoutMs: 1600, pollIntervalMs: 150, cancellationToken)
                .ConfigureAwait(false);

            var remainingConfirmed = current.ConfirmedProcesses;
            if (remainingConfirmed.Count > 0)
            {
                var stillHasVisible = remainingConfirmed.Any(p => p.Role == DetectedProcessRole.PrimaryWithVisibleWindow);
                var remainingPrimaryPids = remainingConfirmed
                    .Where(p => p.Role is DetectedProcessRole.PrimaryWithVisibleWindow or DetectedProcessRole.PrimaryTrayOrBackground)
                    .Select(p => p.ProcessId)
                    .ToHashSet();

                // Если главное окно закрылось, но Electron/Chromium-приложение осталось в трее, отправляем штатный WM_ENDSESSION.
                // Для обычного консольного/Win32-приложения без Electron/Chromium не отправляем WM_ENDSESSION, чтобы не убить консольный процесс при скрытом окне.
                var supportsTraySessionEnd = application.UseChromiumProxyFlag
                    || current.HelperOrAuxiliaryProcesses.Count > 0;

                if (!stillHasVisible && remainingPrimaryPids.Count > 0 && supportsTraySessionEnd)
                {
                    strategyName = "CloseThenSessionEnd";
                    var status = SendSessionEndToPids(remainingPrimaryPids);
                    if (status == SessionEndSignalStatus.Vetoed)
                    {
                        var afterVeto = InspectApplication(application, targetPath, current);
                        return new ProcessStopResult
                        {
                            Outcome = ProcessStopOutcome.GracefulVetoed,
                            FinalInspection = afterVeto,
                            RemainingConfirmedProcesses = afterVeto.ConfirmedProcesses,
                            RemainingAmbiguousProcesses = afterVeto.AmbiguousProcesses,
                            GracefulStrategyUsed = strategyName
                        };
                    }

                    current = await PollUntilStoppedAsync(application, targetPath, current, timeoutMs: 2800, pollIntervalMs: 200, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var finalConfirmed = current.ConfirmedProcesses;
        var finalAmbiguous = current.AmbiguousProcesses;
        var outcome = finalConfirmed.Count == 0 && finalAmbiguous.Count == 0
            ? ProcessStopOutcome.AllStopped
            : finalConfirmed.Count == 0 && finalAmbiguous.Count > 0
                ? ProcessStopOutcome.AmbiguousRemaining
                : ProcessStopOutcome.StillRunningAfterGraceful;

        return new ProcessStopResult
        {
            Outcome = outcome,
            FinalInspection = current,
            RemainingConfirmedProcesses = finalConfirmed,
            RemainingAmbiguousProcesses = finalAmbiguous,
            GracefulStrategyUsed = strategyName
        };
    }

    private async Task<ProcessStopResult> StopForcefullyAsync(
        ApplicationProfile application,
        string targetPath,
        RunningAppInspection initialInspection,
        CancellationToken cancellationToken)
    {
        const int maxPasses = 5;
        const int totalTimeoutMs = 6000;

        var totalWatch = Stopwatch.StartNew();
        var current = initialInspection;
        var permanentErrorsByKey = new Dictionary<(int Pid, DateTime? StartedUtc), ProcessTerminationError>();

        for (var pass = 0; pass < maxPasses && totalWatch.ElapsedMilliseconds < totalTimeoutMs; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var orderedTargets = OrderTargetsForTermination(current.ConfirmedProcesses);
            if (orderedTargets.Count == 0)
            {
                break;
            }

            var attemptedAnyKill = false;
            var waitHandles = new List<SafeProcessHandle>();

            try
            {
                foreach (var candidate in orderedTargets)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var key = (candidate.ProcessId, candidate.StartedUtc);
                    if (permanentErrorsByKey.ContainsKey(key))
                    {
                        continue;
                    }

                    if (candidate.StartedUtc is null)
                    {
                        permanentErrorsByKey[key] = new ProcessTerminationError(
                            candidate.ProcessId,
                            candidate.ProcessName,
                            candidate.ExecutablePath,
                            ErrorAccessDenied,
                            "Не удалось подтвердить время создания процесса для безопасного завершения.");
                        continue;
                    }

                    attemptedAnyKill = true;
                    var attempt = _platform.TryTerminateProcess(candidate.ProcessId, candidate.StartedUtc);
                    switch (attempt.Status)
                    {
                        case TerminateAttemptStatus.Terminated:
                            if (attempt.WaitHandle is not null)
                            {
                                waitHandles.Add(attempt.WaitHandle);
                            }
                            break;

                        case TerminateAttemptStatus.AlreadyExited:
                        case TerminateAttemptStatus.CreationTimeMismatch:
                            break;

                        case TerminateAttemptStatus.Failed:
                            permanentErrorsByKey[key] = new ProcessTerminationError(
                                candidate.ProcessId,
                                candidate.ProcessName,
                                candidate.ExecutablePath,
                                attempt.NativeErrorCode,
                                attempt.ErrorMessage ?? $"Win32 error {attempt.NativeErrorCode}");
                            break;
                    }
                }

                if (waitHandles.Count > 0)
                {
                    var remainingBudget = (int)Math.Max(50, Math.Min(350, totalTimeoutMs - totalWatch.ElapsedMilliseconds));
                    WaitAllBounded(waitHandles, remainingBudget);
                }
            }
            finally
            {
                foreach (var h in waitHandles)
                {
                    h.Dispose();
                }
            }

            if (pass > 0 && !attemptedAnyKill)
            {
                break;
            }

            // Даём ядру и возможным перезапускающимся потомкам короткое окно перед повторной проверкой
            var settleMs = (int)Math.Min(120, Math.Max(0, totalTimeoutMs - totalWatch.ElapsedMilliseconds));
            if (settleMs > 0)
            {
                await Task.Delay(settleMs, cancellationToken).ConfigureAwait(false);
            }

            current = InspectApplication(application, targetPath, current);
            var remainingKeys = current.ConfirmedProcesses
                .Select(p => (p.ProcessId, p.StartedUtc))
                .ToHashSet();

            // Удаляем ошибки по процессам, которые всё-таки завершились к моменту повторного снимка
            foreach (var existingKey in permanentErrorsByKey.Keys.ToList())
            {
                if (!remainingKeys.Contains(existingKey))
                {
                    permanentErrorsByKey.Remove(existingKey);
                }
            }

            if (current.ConfirmedProcesses.Count == 0)
            {
                break;
            }

            var allRemainingBlocked = current.ConfirmedProcesses
                .All(p => permanentErrorsByKey.ContainsKey((p.ProcessId, p.StartedUtc)));
            if (allRemainingBlocked)
            {
                break;
            }
        }

        var finalConfirmed = current.ConfirmedProcesses;
        var finalAmbiguous = current.AmbiguousProcesses;
        var errors = permanentErrorsByKey.Values.ToList();

        var outcome = finalConfirmed.Count == 0 && finalAmbiguous.Count == 0 && errors.Count == 0
            ? ProcessStopOutcome.AllStopped
            : finalConfirmed.Count == 0 && finalAmbiguous.Count > 0 && errors.Count == 0
                ? ProcessStopOutcome.AmbiguousRemaining
                : ProcessStopOutcome.PartialOrFailedTermination;

        return new ProcessStopResult
        {
            Outcome = outcome,
            FinalInspection = current,
            RemainingConfirmedProcesses = finalConfirmed,
            RemainingAmbiguousProcesses = finalAmbiguous,
            Errors = errors,
            GracefulStrategyUsed = null
        };
    }

    private async Task<RunningAppInspection> PollUntilStoppedAsync(
        ApplicationProfile application,
        string targetPath,
        RunningAppInspection current,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = InspectApplication(application, targetPath, current);
            if (current.ConfirmedProcesses.Count == 0)
            {
                return current;
            }

            var delay = (int)Math.Min(pollIntervalMs, Math.Max(10, timeoutMs - sw.ElapsedMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return InspectApplication(application, targetPath, current);
    }

    private static string ResolveGracefulStrategy(ApplicationProfile application, RunningAppInspection inspection)
    {
        if (string.Equals(application.TemplateKey, BuiltInAppTemplates.ClaudeDesktopKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(application.TemplateKey, BuiltInAppTemplates.CodexDesktopKey, StringComparison.OrdinalIgnoreCase))
        {
            return "SessionEndAppExit";
        }

        var hasVisible = inspection.ConfirmedProcesses.Any(p => p.Role == DetectedProcessRole.PrimaryWithVisibleWindow);
        if (!hasVisible && (application.UseChromiumProxyFlag || inspection.HelperOrAuxiliaryProcesses.Count > 0))
        {
            return "SessionEndAppExit";
        }

        return "CloseMainWindow";
    }

    private bool TryClosePrimaryWindows(int pid)
        => TryClosePrimaryWindowsBatch(new HashSet<int> { pid });

    private bool TryClosePrimaryWindowsBatch(HashSet<int> primaryPids)
    {
        var windows = _platform.EnumerateTopLevelWindows(primaryPids)
            .Where(w => primaryPids.Contains(w.ProcessId) && w.IsVisible)
            .OrderByDescending(w => !string.IsNullOrWhiteSpace(w.Title))
            .ToList();

        var sentAny = false;
        foreach (var win in windows)
        {
            if (_platform.TryPostCloseToWindow(win.Handle))
            {
                sentAny = true;
            }
        }

        return sentAny;
    }

    private SessionEndSignalStatus SendSessionEndToPids(HashSet<int> targetPids)
    {
        var candidateWindows = _platform.EnumerateTopLevelWindows(targetPids)
            .Where(w => targetPids.Contains(w.ProcessId) && !IsAuxiliaryOrInjectedWindowClass(w.ClassName))
            .ToList();

        if (candidateWindows.Count == 0)
        {
            return SessionEndSignalStatus.NoWindows;
        }

        var windows = candidateWindows
            .GroupBy(w => w.ProcessId)
            .SelectMany(group =>
            {
                var chromeWindows = group
                    .Where(w => w.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return chromeWindows.Count > 0 ? chromeWindows : group.ToList();
            })
            .OrderBy(w => GetWindowPriority(w))
            .ToList();

        return _platform.SendGracefulSessionEnd(windows);
    }

    private static bool IsAuxiliaryOrInjectedWindowClass(string className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        return className.Equals("IME", StringComparison.OrdinalIgnoreCase)
            || className.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase)
            || className.Equals("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase)
            || className.Equals("CicLoaderWndClass", StringComparison.OrdinalIgnoreCase)
            || className.Equals("Touch Tooltip Window", StringComparison.OrdinalIgnoreCase)
            || className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase)
            || className.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
            || className.Equals("PseudoConsoleWindow", StringComparison.OrdinalIgnoreCase)
            || className.Equals("UAC_InputIndicatorOverlayWnd", StringComparison.OrdinalIgnoreCase)
            || className.Equals("UAC Input Indicator", StringComparison.OrdinalIgnoreCase)
            || className.Equals("GDI+ Hook Window Class", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith(".NET-BroadcastEventWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0
                && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static int GetWindowPriority(TopLevelWindowEntry w)
    {
        if (w.ClassName.StartsWith("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
        {
            return w.IsVisible ? 0 : 1;
        }

        if (w.ClassName.StartsWith("Chrome_WidgetWin_0", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return w.IsVisible ? 3 : 4;
    }

    private static List<DetectedProcessInfo> OrderTargetsForTermination(IReadOnlyList<DetectedProcessInfo> confirmed)
    {
        var confirmedByPid = confirmed.ToDictionary(p => p.ProcessId);

        int GetDepth(DetectedProcessInfo p)
        {
            var depth = 0;
            var currentPid = p.ParentProcessId;
            var visited = new HashSet<int> { p.ProcessId };
            while (currentPid is int ppid && visited.Add(ppid) && confirmedByPid.TryGetValue(ppid, out var parent) && depth < 16)
            {
                depth++;
                currentPid = parent.ParentProcessId;
            }

            return depth;
        }

        int GetRolePriority(DetectedProcessRole role) => role switch
        {
            DetectedProcessRole.PrimaryWithVisibleWindow => 0,
            DetectedProcessRole.PrimaryTrayOrBackground => 1,
            DetectedProcessRole.Unknown => 2,
            DetectedProcessRole.AuxiliaryOrCompanion => 3,
            DetectedProcessRole.ChromiumHelper => 4,
            _ => 5
        };

        return confirmed
            .OrderBy(p => GetRolePriority(p.Role))
            .ThenBy(GetDepth)
            .ThenBy(p => p.StartedUtc ?? DateTime.MaxValue)
            .ThenBy(p => p.ProcessId)
            .ToList();
    }

    private static void WaitAllBounded(List<SafeProcessHandle> handles, int totalTimeoutMs)
    {
        if (handles.Count == 0)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        foreach (var handle in handles)
        {
            var remaining = (int)Math.Max(0, totalTimeoutMs - sw.ElapsedMilliseconds);
            Win32ProcessPlatformOperations.Instance.WaitForExit(handle, remaining);
        }
    }

    private bool IsTrackedForOtherApp(int pid, DateTime? startedUtc, Guid? currentAppId)
    {
        if (currentAppId is null || !_tracked.TryGetValue(pid, out var entry) || entry.Info.HasExited)
        {
            return false;
        }

        if (entry.Info.ApplicationId == currentAppId.Value)
        {
            return false;
        }

        return entry.Info.ProcessCreationTimeUtc is null
               || startedUtc is null
               || entry.Info.ProcessCreationTimeUtc == startedUtc;
    }

    private bool IsTrackedPidMatch(int pid, DateTime? startedUtc)
    {
        if (!_tracked.TryGetValue(pid, out var entry) || entry.Info.HasExited)
        {
            return false;
        }

        return entry.Info.ProcessCreationTimeUtc is null
               || startedUtc is null
               || entry.Info.ProcessCreationTimeUtc == startedUtc;
    }

    private static int? ResolveNearestSelectedAncestor(
        int pid,
        HashSet<int> selectedPids,
        Dictionary<int, (int ParentPid, string ProcName, DateTime? StartedUtc, string? ExePath, string? PkgFull, string? PkgFamily)> sessionMeta,
        Dictionary<int, int> parentByPid)
    {
        if (!sessionMeta.TryGetValue(pid, out var childMeta))
        {
            return null;
        }

        var currentChildStarted = childMeta.StartedUtc;
        var currentPid = pid;
        var visited = new HashSet<int> { pid };

        for (var hop = 0; hop < 8; hop++)
        {
            if (!parentByPid.TryGetValue(currentPid, out var ppid) || ppid == 0 || !visited.Add(ppid))
            {
                return null;
            }

            if (sessionMeta.TryGetValue(ppid, out var parentMeta)
                && parentMeta.StartedUtc.HasValue
                && currentChildStarted.HasValue
                && parentMeta.StartedUtc.Value > currentChildStarted.Value)
            {
                // Не переходим через переиспользованный PID с более поздним временем запуска
                return null;
            }

            if (selectedPids.Contains(ppid))
            {
                return ppid;
            }

            if (sessionMeta.TryGetValue(ppid, out var intermediateMeta) && intermediateMeta.StartedUtc.HasValue)
            {
                currentChildStarted = intermediateMeta.StartedUtc;
            }

            currentPid = ppid;
        }

        return null;
    }

    private static HashSet<string> BuildProcessNameSet(
        string? targetName,
        string? expectedTargetExe,
        IReadOnlyCollection<string>? relatedProcessNames)
    {
        var processNamesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            processNamesToCheck.Add(targetName);
        }

        if (!string.IsNullOrWhiteSpace(expectedTargetExe))
        {
            var realTargetName = Path.GetFileNameWithoutExtension(expectedTargetExe);
            if (!string.IsNullOrWhiteSpace(realTargetName))
            {
                processNamesToCheck.Add(realTargetName);
            }
        }

        if (relatedProcessNames is not null)
        {
            foreach (var related in relatedProcessNames)
            {
                if (string.IsNullOrWhiteSpace(related))
                {
                    continue;
                }

                var cleaned = related.Trim();
                if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[..^4];
                }

                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    processNamesToCheck.Add(cleaned);
                }
            }
        }

        return processNamesToCheck;
    }

    private static Dictionary<int, string?> BuildVisibleWindowMap(IReadOnlyList<TopLevelWindowEntry> windows)
    {
        var result = new Dictionary<int, string?>();
        foreach (var w in windows)
        {
            if (!w.IsVisible)
            {
                continue;
            }

            if (!result.TryGetValue(w.ProcessId, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                result[w.ProcessId] = string.IsNullOrWhiteSpace(w.Title) ? null : w.Title;
            }
        }

        return result;
    }

    private static string? GetSafeInstallRoot(string normalizedMainExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(normalizedMainExe);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            // Не считаем системные каталоги WindowsApps / System32 / Temp общей папкой одного приложения
            var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (leaf.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("System32", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Temp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return dir;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var rootWithSep = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        return candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
               || candidatePath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        // Важно: отписываемся и освобождаем только обёртки Process, НЕ завершая сами дочерние приложения.
        foreach (var pid in _tracked.Keys.ToList())
        {
            if (_tracked.TryRemove(pid, out var entry))
            {
                try
                {
                    entry.Process.EnableRaisingEvents = false;
                    entry.Process.Exited -= entry.ExitHandler;
                    entry.Process.Dispose();
                }
                catch
                {
                    // Игнорируем ошибки освобождения
                }
            }
        }
    }

    private static string NormalizePathSafe(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Win32ProcessPlatformOperations : IProcessPlatformOperations
    {
        public static Win32ProcessPlatformOperations Instance { get; } = new();

        public int CurrentProcessId => Environment.ProcessId;
        public int CurrentSessionId { get; } = GetCurrentSessionIdSafe();

        private static int GetCurrentSessionIdSafe()
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }

        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses()
        {
            var result = new List<ProcessSnapshotEntry>();
            try
            {
                using var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
                if (snap.IsInvalid)
                {
                    return result;
                }

                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32FirstW(snap, ref entry))
                {
                    return result;
                }

                do
                {
                    result.Add(new ProcessSnapshotEntry(
                        (int)entry.th32ProcessID,
                        (int)entry.th32ParentProcessID,
                        entry.szExeFile ?? string.Empty));
                }
                while (Process32NextW(snap, ref entry));
            }
            catch
            {
                // Игнорируем сбои снимка процессов
            }

            return result;
        }

        public IReadOnlyList<TopLevelWindowEntry> EnumerateTopLevelWindows(IReadOnlySet<int>? filterPids = null)
        {
            if (filterPids is not null && filterPids.Count == 0)
            {
                return [];
            }

            var currentPid = CurrentProcessId;
            var result = new List<TopLevelWindowEntry>();
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid == 0 || (int)pid == currentPid)
                    {
                        return true;
                    }

                    if (filterPids is not null && !filterPids.Contains((int)pid))
                    {
                        return true;
                    }

                    var className = GetWindowClassSafe(hWnd);
                    if (IsAuxiliaryOrInjectedWindowClass(className))
                    {
                        return true;
                    }

                    var isVisible = IsWindowVisible(hWnd)
                        && !IsWindowCloaked(hWnd)
                        && !IsHungAppWindow(hWnd)
                        && GetWindowRect(hWnd, out var rect)
                        && rect.Right - rect.Left > 0
                        && rect.Bottom - rect.Top > 0;

                    string? title = null;
                    if (isVisible)
                    {
                        var exStyle = GetWindowLongPtrW(hWnd, GwlExStyle).ToInt64();
                        title = GetWindowTitleSafe(hWnd);
                        var hasUntitled = string.IsNullOrWhiteSpace(title);
                        if (((exStyle & WsExToolWindow) != 0 && hasUntitled)
                            || ((exStyle & WsExNoActivate) != 0 && (exStyle & WsExAppWindow) == 0 && hasUntitled)
                            || ((exStyle & WsExTransparent) != 0 && (exStyle & WsExLayered) != 0 && hasUntitled))
                        {
                            isVisible = false;
                        }
                    }

                    result.Add(new TopLevelWindowEntry(hWnd, (int)pid, className, title, isVisible));
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // Игнорируем сбои перечисления окон
            }

            return result;
        }

        public int? TryGetSessionId(int pid)
            => ProcessIdToSessionId(pid, out var sessionId) ? sessionId : null;

        public DateTime? TryGetProcessCreationTimeUtc(int pid)
        {
            using var handle = OpenProcessLimited(pid);
            if (handle is null || handle.IsInvalid)
            {
                return null;
            }

            return TryGetProcessCreationTimeUtc(handle);
        }

        private static DateTime? TryGetProcessCreationTimeUtc(SafeProcessHandle handle)
        {
            if (GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                var fileTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
                if (fileTime > 0)
                {
                    return DateTime.FromFileTimeUtc(fileTime);
                }
            }

            return null;
        }

        public string? TryQueryExecutablePath(int pid)
        {
            using var handle = OpenProcessLimited(pid);
            if (handle is null || handle.IsInvalid)
            {
                return null;
            }

            try
            {
                var capacity = 1024;
                var sb = new StringBuilder(capacity);
                if (QueryFullProcessImageNameW(handle, 0, sb, ref capacity) && capacity > 0)
                {
                    return sb.ToString(0, capacity);
                }
            }
            catch
            {
            }

            return null;
        }

        public (string? PackageFullName, string? PackageFamilyName) TryGetPackageIdentity(int pid)
        {
            using var handle = OpenProcessLimited(pid);
            if (handle is null || handle.IsInvalid)
            {
                return (null, null);
            }

            return (TryGetPackageFullName(handle), TryGetPackageFamilyName(handle));
        }

        public string? TryGetCommandLine(int pid)
        {
            using var handle = OpenProcessLimited(pid);
            if (handle is null || handle.IsInvalid)
            {
                return null;
            }

            return TryGetProcessCommandLine(handle);
        }

        public bool IsProcessStillActive(int pid, DateTime? expectedStartedUtc)
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
            if (handle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                return err == ErrorAccessDenied;
            }

            if (expectedStartedUtc is not null)
            {
                var actualStart = TryGetProcessCreationTimeUtc(handle);
                if (actualStart is null || actualStart.Value != expectedStartedUtc.Value)
                {
                    return false;
                }
            }

            var waitRc = WaitForSingleObject(handle, 150);
            if (waitRc == 0)
            {
                return false;
            }

            if (GetExitCodeProcess(handle, out var exitCode))
            {
                return exitCode == StillActive;
            }

            return true;
        }

        public bool TryPostCloseToWindow(IntPtr hWnd)
            => PostMessageW(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);

        public SessionEndSignalStatus SendGracefulSessionEnd(IReadOnlyList<TopLevelWindowEntry> windows)
        {
            if (windows.Count == 0)
            {
                return SessionEndSignalStatus.NoWindows;
            }

            var acceptedWindows = new List<IntPtr>();
            foreach (var w in windows)
            {
                var sent = SendMessageTimeoutW(
                    w.Handle,
                    WmQueryEndSession,
                    IntPtr.Zero,
                    (IntPtr)EndSessionCloseApp,
                    SmtoAbortIfHung,
                    2000,
                    out var result);

                if (sent != IntPtr.Zero)
                {
                    if (result == IntPtr.Zero)
                    {
                        return SessionEndSignalStatus.Vetoed;
                    }

                    acceptedWindows.Add(w.Handle);
                }
            }

            if (acceptedWindows.Count == 0)
            {
                return SessionEndSignalStatus.NoWindows;
            }

            foreach (var hWnd in acceptedWindows)
            {
                SendMessageTimeoutW(
                    hWnd,
                    WmEndSession,
                    (IntPtr)1,
                    (IntPtr)EndSessionCloseApp,
                    SmtoAbortIfHung,
                    3000,
                    out _);
            }

            return SessionEndSignalStatus.Sent;
        }

        public TerminateAttemptResult TryTerminateProcess(int pid, DateTime? expectedStartedUtc)
        {
            var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, pid);
            if (handle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                handle.Dispose();

                if (err == ErrorInvalidParameter)
                {
                    return new TerminateAttemptResult(TerminateAttemptStatus.AlreadyExited);
                }

                if (!IsProcessStillActive(pid, expectedStartedUtc))
                {
                    return new TerminateAttemptResult(TerminateAttemptStatus.AlreadyExited);
                }

                return new TerminateAttemptResult(
                    TerminateAttemptStatus.Failed,
                    NativeErrorCode: err,
                    ErrorMessage: new Win32Exception(err).Message);
            }

            var actualStart = TryGetProcessCreationTimeUtc(handle);
            if (expectedStartedUtc is null || actualStart is null || actualStart.Value != expectedStartedUtc.Value)
            {
                handle.Dispose();
                return new TerminateAttemptResult(TerminateAttemptStatus.CreationTimeMismatch);
            }

            if (TerminateProcess(handle, 1))
            {
                return new TerminateAttemptResult(TerminateAttemptStatus.Terminated, WaitHandle: handle);
            }

            var termErr = Marshal.GetLastWin32Error();
            var waitRc = WaitForSingleObject(handle, 150);
            var stillActive = waitRc != 0 && (!GetExitCodeProcess(handle, out var exitCode) || exitCode == StillActive);
            handle.Dispose();

            if (!stillActive || termErr == ErrorInvalidParameter)
            {
                return new TerminateAttemptResult(TerminateAttemptStatus.AlreadyExited);
            }

            return new TerminateAttemptResult(
                TerminateAttemptStatus.Failed,
                NativeErrorCode: termErr,
                ErrorMessage: new Win32Exception(termErr).Message);
        }

        public bool WaitForExit(SafeProcessHandle handle, int timeoutMs)
            => WaitForSingleObject(handle, (uint)Math.Max(0, timeoutMs)) == 0;

        private static SafeProcessHandle? OpenProcessLimited(int pid)
        {
            try
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                return handle.IsInvalid ? null : handle;
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? TryGetPackageFullName(SafeProcessHandle handle)
    {
        try
        {
            var length = 0;
            var rc = GetPackageFullName(handle, ref length, null);
            if (rc == AppModelErrorNoPackage || rc != ErrorInsufficientBuffer || length <= 1)
            {
                return null;
            }

            var sb = new StringBuilder(length);
            rc = GetPackageFullName(handle, ref length, sb);
            return rc == 0 ? sb.ToString().TrimEnd('\0') : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPackageFamilyName(SafeProcessHandle handle)
    {
        try
        {
            var length = 0;
            var rc = GetPackageFamilyName(handle, ref length, null);
            if (rc == AppModelErrorNoPackage || rc != ErrorInsufficientBuffer || length <= 1)
            {
                return null;
            }

            var sb = new StringBuilder(length);
            rc = GetPackageFamilyName(handle, ref length, sb);
            return rc == 0 ? sb.ToString().TrimEnd('\0') : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessCommandLine(SafeProcessHandle handle)
    {
        var bufferSize = 2048;
        var ptr = IntPtr.Zero;
        try
        {
            ptr = Marshal.AllocHGlobal(bufferSize);
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, ptr, bufferSize, out var needed);
            if (status != 0 && needed > bufferSize && needed <= 64 * 1024)
            {
                Marshal.FreeHGlobal(ptr);
                bufferSize = needed;
                ptr = Marshal.AllocHGlobal(bufferSize);
                status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, ptr, bufferSize, out _);
            }

            if (status != 0)
            {
                return null;
            }

            var us = Marshal.PtrToStructure<UnicodeString>(ptr);
            if (us.Length == 0 || us.Buffer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    private static string? GetWindowTitleSafe(IntPtr hWnd)
    {
        try
        {
            var len = GetWindowTextLengthW(hWnd);
            if (len <= 0)
            {
                return null;
            }

            var sb = new StringBuilder(Math.Min(len + 1, 512));
            var read = GetWindowTextW(hWnd, sb, sb.Capacity);
            return read > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowClassSafe(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            var read = GetClassNameW(hWnd, sb, sb.Capacity);
            return read > 0 ? sb.ToString(0, read) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int dwProcessId, out int pSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out FileTime lpCreationTime,
        out FileTime lpExitTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(SafeProcessHandle hProcess, ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle hProcess, ref int packageFamilyNameLength, StringBuilder? packageFamilyName);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GwOwner = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
