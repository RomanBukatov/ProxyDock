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
/// Подробная диагностическая информация об одном обнаруженном процессе.
/// </summary>
public sealed class DetectedProcessInfo
{
    public DateTime? StartedUtc { get; init; }
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
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
}

/// <summary>
/// Результат инспекции уже работающих процессов целевого приложения.
/// </summary>
public sealed class RunningAppInspection
{
    public string? ExpectedPackageFamilyName { get; init; }
    public string? ExpectedExecutablePath { get; init; }
    public IReadOnlyList<DetectedProcessInfo> Processes { get; init; } = [];

    public IReadOnlyList<DetectedProcessInfo> PrimaryVisibleProcesses
        => Processes.Where(p => p.Role == DetectedProcessRole.PrimaryWithVisibleWindow).ToList();

    public IReadOnlyList<DetectedProcessInfo> PrimaryTrayOrBackgroundProcesses
        => Processes.Where(p => p.Role == DetectedProcessRole.PrimaryTrayOrBackground).ToList();

    public IReadOnlyList<DetectedProcessInfo> HelperOrAuxiliaryProcesses
        => Processes.Where(p => p.Role is DetectedProcessRole.ChromiumHelper or DetectedProcessRole.AuxiliaryOrCompanion).ToList();

    // Related names and a shared installation directory alone do not establish ownership.
    public IReadOnlyList<DetectedProcessInfo> ConfirmedProcesses
    {
        get
        {
            var confirmed = Processes.Where(p =>
                (p.MatchedByMsixIdentity || p.MatchedByExecutablePath) &&
                !string.IsNullOrWhiteSpace(ExpectedExecutablePath) &&
                string.Equals(p.ExecutablePath, ExpectedExecutablePath, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.ProcessId);

            // The same package can also run an independent CLI session.
            // Include companion binaries only through a verified, older parent.
            int count;
            do
            {
                count = confirmed.Count;
                foreach (var p in Processes)
                {
                    if ((p.MatchedByMsixIdentity || p.MatchedByExecutablePath) &&
                        p.ParentProcessId is int parentId && confirmed.TryGetValue(parentId, out var parent) &&
                        parent.StartedUtc <= p.StartedUtc)
                    {
                        confirmed.TryAdd(p.ProcessId, p);
                    }
                }
            } while (count != confirmed.Count);

            return confirmed.Values.ToList();
        }
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

        sb.Append($"Обнаружено процессов: {Processes.Count}");
        sb.Append($" (основных с окном: {visible.Count}, в трее/фоне: {tray.Count}, вспомогательных: {helpers.Count}).");

        if (visible.Count > 0)
        {
            var pids = string.Join(", ", visible.Select(p => FormatPidWithName(p)));
            sb.Append($" Видимое окно: {pids}.");
        }

        if (tray.Count > 0)
        {
            var pids = string.Join(", ", tray.Select(p => FormatPidWithName(p)));
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

    private static string FormatPidWithName(DetectedProcessInfo p)
        => string.IsNullOrWhiteSpace(p.MainWindowTitle)
            ? $"{p.ProcessName} (PID {p.ProcessId})"
            : $"{p.ProcessName} (PID {p.ProcessId}, «{p.MainWindowTitle}»)";
}
