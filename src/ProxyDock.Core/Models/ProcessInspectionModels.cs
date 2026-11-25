using System.Text;

namespace ProxyDock.Core.Models;

/// <summary>
/// Классификация роли обнаруженного процесса при проверке уже запущенных экземпляров.
/// </summary>
public enum DetectedProcessRole
{
    Unknown = 0,
    PrimaryWithVisibleWindow = 1,
    PrimaryTrayOrBackground = 2,
    ChromiumHelper = 3,
    AuxiliaryOrCompanion = 4
}

/// <summary>
/// Итог операции штатного или принудительного завершения приложения.
/// </summary>
public enum ProcessStopOutcome
{
    AllStopped = 0,
    StillRunningAfterGraceful = 1,
    GracefulVetoed = 2,
    PartialOrFailedTermination = 3,
    AmbiguousRemaining = 4
}

/// <summary>
/// Подробная ошибка завершения конкретного процесса.
/// </summary>
public sealed record ProcessTerminationError(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    int NativeErrorCode,
    string Message);

/// <summary>
/// Подробная диагностическая информация об одном обнаруженном процессе.
/// </summary>
public sealed class DetectedProcessInfo
{
    public DateTime? StartedUtc { get; init; }
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public int? AncestorCandidateProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public bool HasVisibleWindow { get; init; }
    public string? MainWindowTitle { get; init; }
    public string? ProcessTypeFlag { get; init; }
    public DetectedProcessRole Role { get; init; }
    public bool MatchedByMsixIdentity { get; init; }
    public bool MatchedByExecutablePath { get; init; }
    public bool MatchedByProcessName { get; init; }
    public bool IsTrackedByProxyDock { get; init; }
    public bool IsTrackedForTargetProfile { get; init; }
    public bool IsTrackedForOtherProfile { get; init; }
}

/// <summary>
/// Результат штатного или принудительного завершения процессов целевого экземпляра.
/// </summary>
public sealed class ProcessStopResult
{
    public ProcessStopOutcome Outcome { get; init; }
    public required RunningAppInspection FinalInspection { get; init; }
    public IReadOnlyList<DetectedProcessInfo> RemainingConfirmedProcesses { get; init; } = [];
    public IReadOnlyList<DetectedProcessInfo> RemainingAmbiguousProcesses { get; init; } = [];
    public IReadOnlyList<ProcessTerminationError> Errors { get; init; } = [];
    public string? GracefulStrategyUsed { get; init; }

    public bool AllTargetProcessesStopped
        => RemainingConfirmedProcesses.Count == 0
           && RemainingAmbiguousProcesses.Count == 0
           && Errors.Count == 0;
}

/// <summary>
/// Результат инспекции уже работающих процессов целевого приложения.
/// </summary>
public sealed class RunningAppInspection
{
    private readonly List<(DetectedProcessInfo Info, DateTime LastSeenUtc)> _previouslyConfirmed = [];

    public DateTime InspectedAtUtc { get; init; } = DateTime.UtcNow;
    public string? ExpectedPackageFamilyName { get; init; }
    public string? ExpectedExecutablePath { get; init; }
    public bool HasActiveTrackedRootForTarget { get; init; }
    public IReadOnlyList<DetectedProcessInfo> Processes { get; init; } = [];

    public IReadOnlyList<DetectedProcessInfo> PrimaryVisibleProcesses
        => Processes.Where(p => p.Role == DetectedProcessRole.PrimaryWithVisibleWindow).ToList();

    public IReadOnlyList<DetectedProcessInfo> PrimaryTrayOrBackgroundProcesses
        => Processes.Where(p => p.Role == DetectedProcessRole.PrimaryTrayOrBackground).ToList();

    public IReadOnlyList<DetectedProcessInfo> HelperOrAuxiliaryProcesses
        => Processes.Where(p => p.Role is DetectedProcessRole.ChromiumHelper or DetectedProcessRole.AuxiliaryOrCompanion).ToList();

    public void RetainVerifiedInstances(RunningAppInspection? previous)
    {
        if (previous is null || !string.Equals(ExpectedExecutablePath, previous.ExpectedExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cutoff = InspectedAtUtc.AddSeconds(-15);
        var byKey = new Dictionary<(int ProcessId, DateTime? StartedUtc), (DetectedProcessInfo Info, DateTime LastSeenUtc)>();

        foreach (var entry in _previouslyConfirmed)
        {
            if (entry.LastSeenUtc >= cutoff)
            {
                byKey[(entry.Info.ProcessId, entry.Info.StartedUtc)] = entry;
            }
        }

        foreach (var entry in previous._previouslyConfirmed)
        {
            if (entry.LastSeenUtc >= cutoff)
            {
                var key = (entry.Info.ProcessId, entry.Info.StartedUtc);
                if (!byKey.TryGetValue(key, out var existing) || entry.LastSeenUtc > existing.LastSeenUtc)
                {
                    byKey[key] = entry;
                }
            }
        }

        foreach (var proc in previous.ConfirmedProcesses)
        {
            var key = (proc.ProcessId, proc.StartedUtc);
            byKey[key] = (proc, previous.InspectedAtUtc);
        }

        _previouslyConfirmed.Clear();
        if (HasActiveTrackedRootForTarget)
        {
            // При активном отслеживаемом корне не переносим старые внешние процессы из предыдущих снимков
            _previouslyConfirmed.AddRange(byKey.Values.Where(v => v.Info.IsTrackedForTargetProfile || v.Info.ParentProcessId != null));
        }
        else
        {
            _previouslyConfirmed.AddRange(byKey.Values);
        }
    }

    // Related names and a shared installation directory alone do not establish ownership.
    public IReadOnlyList<DetectedProcessInfo> ConfirmedProcesses
    {
        get
        {
            var confirmed = new Dictionary<int, DetectedProcessInfo>();
            var expectedFileName = string.IsNullOrWhiteSpace(ExpectedExecutablePath)
                ? null
                : Path.GetFileName(ExpectedExecutablePath);

            foreach (var p in Processes)
            {
                if (p.IsTrackedForOtherProfile)
                {
                    continue;
                }

                if (!(p.MatchedByMsixIdentity || p.MatchedByExecutablePath))
                {
                    continue;
                }

                var isSeed = false;

                if (HasActiveTrackedRootForTarget)
                {
                    isSeed = p.IsTrackedForTargetProfile;
                }
                else
                {
                    var matchesExactExpected = !string.IsNullOrWhiteSpace(ExpectedExecutablePath)
                        && string.Equals(p.ExecutablePath, ExpectedExecutablePath, StringComparison.OrdinalIgnoreCase);

                    var matchesMsixPrimaryImage = !string.IsNullOrWhiteSpace(ExpectedPackageFamilyName)
                        && p.MatchedByMsixIdentity
                        && !string.IsNullOrWhiteSpace(p.ExecutablePath)
                        && !string.IsNullOrWhiteSpace(expectedFileName)
                        && p.ExecutablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetFileName(p.ExecutablePath), expectedFileName, StringComparison.OrdinalIgnoreCase);

                    isSeed = matchesExactExpected || matchesMsixPrimaryImage || MatchesPreviouslyConfirmed(p);
                }

                if (isSeed)
                {
                    confirmed.TryAdd(p.ProcessId, p);
                }
            }

            // The same package can also run an independent CLI session.
            // Include companion binaries only through a verified, older parent (live or recently observed).
            int count;
            do
            {
                count = confirmed.Count;
                foreach (var p in Processes)
                {
                    if (p.IsTrackedForOtherProfile
                        || confirmed.ContainsKey(p.ProcessId)
                        || !(p.MatchedByMsixIdentity || p.MatchedByExecutablePath))
                    {
                        continue;
                    }

                    if (MatchesLiveParent(p, p.ParentProcessId, confirmed)
                        || MatchesLiveParent(p, p.AncestorCandidateProcessId, confirmed)
                        || MatchesRecentParent(p, p.ParentProcessId)
                        || MatchesRecentParent(p, p.AncestorCandidateProcessId))
                    {
                        confirmed.TryAdd(p.ProcessId, p);
                    }
                }
            }
            while (count != confirmed.Count);

            return confirmed.Values.ToList();
        }
    }

    /// <summary>
    /// Связанные процессы того же пакета или папки установки, принадлежность которых к управляемому экземпляру не удалось доказать однозначно.
    /// </summary>
    public IReadOnlyList<DetectedProcessInfo> AmbiguousProcesses
    {
        get
        {
            if (HasActiveTrackedRootForTarget)
            {
                return [];
            }

            var confirmedPids = ConfirmedProcesses.Select(p => p.ProcessId).ToHashSet();
            var expectedDir = GetDirectorySafe(ExpectedExecutablePath);

            var ambiguous = new List<DetectedProcessInfo>();
            foreach (var p in Processes)
            {
                if (confirmedPids.Contains(p.ProcessId) || p.IsTrackedForOtherProfile)
                {
                    continue;
                }

                var samePackageOrWindowsApps = !string.IsNullOrWhiteSpace(ExpectedPackageFamilyName)
                    && p.MatchedByMsixIdentity;

                var sameInstallRoot = !string.IsNullOrWhiteSpace(expectedDir)
                    && !string.IsNullOrWhiteSpace(p.ExecutablePath)
                    && IsPathUnderRoot(p.ExecutablePath, expectedDir);

                if (samePackageOrWindowsApps || sameInstallRoot)
                {
                    ambiguous.Add(p);
                }
            }

            return ambiguous;
        }
    }

    public bool HasRemainingInstances(IReadOnlyList<DetectedProcessInfo> previous)
    {
        if (ConfirmedProcesses.Count > 0 || AmbiguousProcesses.Count > 0)
        {
            return true;
        }

        // An approved companion can outlive its parent and lose the ancestry match.
        return Processes.Any(current => previous.Any(old => old.ProcessId == current.ProcessId
            && old.StartedUtc.HasValue
            && old.StartedUtc == current.StartedUtc));
    }

    public bool HasActiveMainInstance
        => PrimaryVisibleProcesses.Count > 0 || PrimaryTrayOrBackgroundProcesses.Count > 0;

    public bool HasVisibleWindowInstance
        => PrimaryVisibleProcesses.Count > 0;

    public bool HasOnlyBackgroundOrHelperProcesses
        => !HasActiveMainInstance && HelperOrAuxiliaryProcesses.Count > 0;

    public IReadOnlyList<int> ActiveMainProcessIds
        => Processes
            .Where(p => p.Role is DetectedProcessRole.PrimaryWithVisibleWindow or DetectedProcessRole.PrimaryTrayOrBackground)
            .Select(p => p.ProcessId)
            .Distinct()
            .Order()
            .ToList();

    public string BuildDiagnosticSummary()
    {
        if (Processes.Count == 0)
        {
            return string.IsNullOrWhiteSpace(ExpectedPackageFamilyName)
                ? "Активные процессы приложения не обнаружены."
                : $"Активные процессы пакета {ExpectedPackageFamilyName} не обнаружены.";
        }

        var sb = new StringBuilder();
        var visible = PrimaryVisibleProcesses;
        var tray = PrimaryTrayOrBackgroundProcesses;
        var helpers = HelperOrAuxiliaryProcesses;
        var confirmedCount = ConfirmedProcesses.Count;
        var ambiguousCount = AmbiguousProcesses.Count;

        sb.Append($"Обнаружено процессов: {Processes.Count}");
        sb.Append($" (основных с окном: {visible.Count}, в трее/фоне: {tray.Count}, вспомогательных: {helpers.Count}, подтверждено: {confirmedCount}");
        if (ambiguousCount > 0)
        {
            sb.Append($", неоднозначных: {ambiguousCount}");
        }

        sb.Append(").");

        if (visible.Count > 0)
        {
            var pids = string.Join(", ", visible.Select(FormatPidWithName));
            sb.Append($" Видимое окно: {pids}.");
        }

        if (tray.Count > 0)
        {
            var pids = string.Join(", ", tray.Select(FormatPidWithName));
            sb.Append($" Основной процесс без окна/в трее: {pids}.");
        }

        if (helpers.Count > 0)
        {
            var helperDesc = string.Join(", ", helpers.Take(4).Select(p =>
            {
                var tag = !string.IsNullOrWhiteSpace(p.ProcessTypeFlag)
                    ? $"--type={p.ProcessTypeFlag}"
                    : p.Role == DetectedProcessRole.AuxiliaryOrCompanion ? "companion" : "child";
                return $"{p.ProcessName}#{p.ProcessId} ({tag})";
            }));
            if (helpers.Count > 4)
            {
                helperDesc += $" +{helpers.Count - 4}";
            }

            sb.Append($" Вспомогательные: {helperDesc}.");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedPackageFamilyName))
        {
            sb.Append($" MSIX: {ExpectedPackageFamilyName}.");
        }

        return sb.ToString();
    }

    private bool MatchesPreviouslyConfirmed(DetectedProcessInfo candidate)
        => _previouslyConfirmed.Any(old =>
            old.Info.ProcessId == candidate.ProcessId
            && old.Info.StartedUtc.HasValue
            && old.Info.StartedUtc == candidate.StartedUtc
            && string.Equals(old.Info.ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesLiveParent(
        DetectedProcessInfo child,
        int? parentId,
        Dictionary<int, DetectedProcessInfo> confirmed)
    {
        if (parentId is not int pid || !confirmed.TryGetValue(pid, out var parent))
        {
            return false;
        }

        return !parent.StartedUtc.HasValue
               || !child.StartedUtc.HasValue
               || parent.StartedUtc.Value <= child.StartedUtc.Value;
    }

    private bool MatchesRecentParent(DetectedProcessInfo child, int? parentId)
    {
        if (parentId is not int pid || !child.StartedUtc.HasValue)
        {
            return false;
        }

        return _previouslyConfirmed.Any(old =>
            old.Info.ProcessId == pid
            && old.Info.StartedUtc.HasValue
            && old.Info.StartedUtc.Value <= child.StartedUtc.Value
            && child.StartedUtc.Value <= old.LastSeenUtc.AddSeconds(10));
    }

    private static string? GetDirectorySafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        try
        {
            var normalizedCand = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
            var rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedCand.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                   || normalizedCand.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPidWithName(DetectedProcessInfo p)
        => string.IsNullOrWhiteSpace(p.MainWindowTitle)
            ? $"{p.ProcessName} (PID {p.ProcessId})"
            : $"{p.ProcessName} (PID {p.ProcessId}, «{p.MainWindowTitle}»)";
}
