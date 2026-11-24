using System.Text.RegularExpressions;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Services;

/// <summary>
/// Сырые сведения о процессе-кандидате для чистой классификации без зависимостей от ОС.
/// </summary>
public sealed record RawProcessCandidate(
    int ProcessId,
    int? ParentProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? CommandLine,
    string? PackageFullName,
    string? PackageFamilyName,
    bool HasVisibleWindow,
    string? MainWindowTitle,
    bool MatchedByPrimaryName,
    bool IsTrackedByProxyDock = false,
    DateTime? StartedUtc = null);

/// <summary>
/// Контекст целевого приложения для сопоставления и классификации процессов.
/// </summary>
public sealed record ProcessMatchContext(
    string ExpectedExecutablePath,
    string? ExpectedTargetExecutablePath = null,
    string? ExpectedPackageFamilyName = null,
    string? ExpectedPackageRoot = null,
    bool IsWindowsAppsAlias = false);

/// <summary>
/// Детерминированный классификатор запущенных процессов:
/// отделяет основные окна и процессы в трее от Chromium/Electron helper-процессов,
/// вспомогательных утилит/служб и одноимённых процессов из других пакетов/папок.
/// </summary>
public static partial class ProcessInstanceClassifier
{
    private static readonly HashSet<string> ChromiumHelperTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "renderer",
        "gpu-process",
        "utility",
        "crashpad-handler",
        "zygote",
        "broker",
        "ppapi",
        "plugin",
        "network-service"
    };

    private static readonly string[] AuxiliaryNameTokens =
    [
        "crashpad",
        "updater",
        "cowork-svc",
        "sandbox-service"
    ];

    public static RunningAppInspection Classify(
        IReadOnlyList<RawProcessCandidate> candidates,
        ProcessMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var expectedMainExe = !string.IsNullOrWhiteSpace(context.ExpectedTargetExecutablePath)
            ? context.ExpectedTargetExecutablePath
            : context.ExpectedExecutablePath;

        var normalizedExpectedMain = NormalizePathSafe(expectedMainExe);
        var expectedMainDir = GetDirectorySafe(normalizedExpectedMain);
        var normalizedPackageRoot = string.IsNullOrWhiteSpace(context.ExpectedPackageRoot)
            ? null
            : NormalizePathSafe(context.ExpectedPackageRoot);

        var hasMsixContext = !string.IsNullOrWhiteSpace(context.ExpectedPackageFamilyName)
            || !string.IsNullOrWhiteSpace(normalizedPackageRoot);

        var filtered = new List<(RawProcessCandidate Candidate, bool ByMsix, bool ByPath)>();

        foreach (var candidate in candidates)
        {
            var normalizedCandPath = string.IsNullOrWhiteSpace(candidate.ExecutablePath)
                ? null
                : NormalizePathSafe(candidate.ExecutablePath);

            var matchedByMsix = false;
            var matchedByPath = false;

            if (hasMsixContext)
            {
                if (!string.IsNullOrWhiteSpace(context.ExpectedPackageFamilyName))
                {
                    if (!string.IsNullOrWhiteSpace(candidate.PackageFamilyName))
                    {
                        if (!string.Equals(candidate.PackageFamilyName, context.ExpectedPackageFamilyName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        matchedByMsix = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(normalizedPackageRoot)
                             && !string.IsNullOrWhiteSpace(normalizedCandPath))
                    {
                        if (!IsPathUnderRoot(normalizedCandPath, normalizedPackageRoot))
                        {
                            continue;
                        }

                        matchedByPath = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(normalizedCandPath))
                    {
                        // Например, отдельный Win32 CLI codex.exe без MSIX identity не должен совпадать с MSIX Codex Desktop
                        continue;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(normalizedPackageRoot) && !string.IsNullOrWhiteSpace(normalizedCandPath))
                {
                    if (!IsPathUnderRoot(normalizedCandPath, normalizedPackageRoot))
                    {
                        continue;
                    }

                    matchedByPath = true;
                }
            }
            else if (candidate.MatchedByPrimaryName && !context.IsWindowsAppsAlias)
            {
                if (!string.IsNullOrWhiteSpace(normalizedCandPath)
                    && !string.IsNullOrWhiteSpace(normalizedExpectedMain)
                    && !string.Equals(normalizedCandPath, normalizedExpectedMain, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(normalizedCandPath))
                {
                    matchedByPath = true;
                }
            }
            else if (!candidate.MatchedByPrimaryName && !string.IsNullOrWhiteSpace(normalizedCandPath) && !string.IsNullOrWhiteSpace(expectedMainDir))
            {
                // Для связанных имён без MSIX проверяем, находятся ли они в той же папке установки (если путь известен),
                // но не отбрасываем, если путь совпадает с подпапкой установки.
                if (IsPathUnderRoot(normalizedCandPath, expectedMainDir))
                {
                    matchedByPath = true;
                }
            }

            filtered.Add((candidate, matchedByMsix, matchedByPath));
        }

        var candidatePids = filtered.Select(x => x.Candidate.ProcessId).ToHashSet();
        var byPid = filtered.ToDictionary(x => x.Candidate.ProcessId, x => x.Candidate);

        var results = new List<DetectedProcessInfo>(filtered.Count);
        foreach (var (c, byMsix, byPath) in filtered)
        {
            var typeFlag = ExtractChromiumTypeFlag(c.CommandLine);
            var role = DetermineRole(c, typeFlag, candidatePids, byPid, expectedMainExe);

            results.Add(new DetectedProcessInfo
            {
                ProcessId = c.ProcessId,
                StartedUtc = c.StartedUtc,
                ParentProcessId = c.ParentProcessId,
                ProcessName = c.ProcessName,
                ExecutablePath = c.ExecutablePath,
                PackageFullName = c.PackageFullName,
                PackageFamilyName = c.PackageFamilyName,
                HasVisibleWindow = c.HasVisibleWindow,
                MainWindowTitle = c.MainWindowTitle,
                ProcessTypeFlag = typeFlag,
                Role = role,
                MatchedByMsixIdentity = byMsix,
                MatchedByExecutablePath = byPath,
                MatchedByProcessName = !byMsix && !byPath,
                IsTrackedByProxyDock = c.IsTrackedByProxyDock
            });
        }

        return new RunningAppInspection
        {
            ExpectedPackageFamilyName = context.ExpectedPackageFamilyName,
            ExpectedExecutablePath = expectedMainExe,
            Processes = results
        };
    }

    private static DetectedProcessRole DetermineRole(
        RawProcessCandidate candidate,
        string? typeFlag,
        HashSet<int> candidatePids,
        Dictionary<int, RawProcessCandidate> byPid,
        string expectedMainExe)
    {
        if (!string.IsNullOrWhiteSpace(typeFlag))
        {
            if (ChromiumHelperTypes.Contains(typeFlag)
                || !string.Equals(typeFlag, "browser", StringComparison.OrdinalIgnoreCase))
            {
                return DetectedProcessRole.ChromiumHelper;
            }
        }

        if (IsAuxiliaryBinary(candidate, expectedMainExe))
        {
            return DetectedProcessRole.AuxiliaryOrCompanion;
        }

        if (candidate.HasVisibleWindow)
        {
            return DetectedProcessRole.PrimaryWithVisibleWindow;
        }

        if (candidate.ParentProcessId is int ppid && candidatePids.Contains(ppid))
        {
            if (byPid.TryGetValue(ppid, out var parent)
                && string.Equals(parent.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return DetectedProcessRole.ChromiumHelper;
            }

            return DetectedProcessRole.AuxiliaryOrCompanion;
        }

        return DetectedProcessRole.PrimaryTrayOrBackground;
    }

    private static bool IsAuxiliaryBinary(RawProcessCandidate candidate, string expectedMainExe)
    {
        var procName = candidate.ProcessName ?? string.Empty;
        foreach (var token in AuxiliaryNameTokens)
        {
            if (procName.Contains(token, StringComparison.OrdinalIgnoreCase)
                || procName.EndsWith("-service", StringComparison.OrdinalIgnoreCase)
                || procName.EndsWith("-helper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExecutablePath))
        {
            var normalized = NormalizePathSafe(candidate.ExecutablePath);
            var expectedMainFile = Path.GetFileName(expectedMainExe);
            var candidateFile = Path.GetFileName(normalized);

            if (normalized.Contains(@"\resources\", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidateFile, expectedMainFile, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.CommandLine))
        {
            if (candidate.CommandLine.Contains("app-server", StringComparison.OrdinalIgnoreCase)
                || candidate.CommandLine.Contains("exec-server", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? ExtractChromiumTypeFlag(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = TypeFlagRegex().Match(commandLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"--type=([A-Za-z0-9\-_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeFlagRegex();

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

    private static string? GetDirectorySafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
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
            return path.TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
