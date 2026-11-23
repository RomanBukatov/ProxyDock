using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.Infrastructure.Platform;

/// <summary>
/// Отслеживает запущенные процессы событийно (без фонового опроса) и не завершает их при закрытии ProxyDock.
/// </summary>
public sealed class WindowsProcessTracker : IProcessTracker
{
    private readonly ConcurrentDictionary<int, (TrackedProcessInfo Info, Process Process, EventHandler ExitHandler)> _tracked = new();

    public event EventHandler<TrackedProcessInfo>? ProcessExited;

    public IReadOnlyCollection<TrackedProcessInfo> ActiveProcesses
        => _tracked.Values.Select(v => v.Info).Where(i => !i.HasExited).ToList();

    public void Track(Process process, ApplicationProfile application, string resolvedTarget)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(application);

        var pid = process.Id;
        var info = new TrackedProcessInfo
        {
            ProcessId = pid,
            ApplicationId = application.Id,
            ApplicationName = application.Name,
            ExecutablePath = resolvedTarget,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        EventHandler exitHandler = (_, _) =>
        {
            info.HasExited = true;
            if (_tracked.TryRemove(pid, out var removed))
            {
                try
                {
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
    {
        var pids = new HashSet<int>();
        var targetName = Path.GetFileNameWithoutExtension(resolvedExecutablePath);
        var normalizedTargetPath = NormalizePathSafe(resolvedExecutablePath);
        var isWindowsAppsAlias = normalizedTargetPath.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);

        var processNamesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            processNamesToCheck.Add(targetName);
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

        foreach (var procName in processNamesToCheck)
        {
            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(procName);
            }
            catch
            {
                continue;
            }

            foreach (var proc in candidates)
            {
                try
                {
                    if (proc.Id == Environment.ProcessId || proc.HasExited)
                    {
                        continue;
                    }

                    // Для обычного exe проверяем совпадение пути, если доступ к MainModule разрешён;
                    // для связанных имён (MSIX: claude-desktop.exe -> Claude.exe, chatgpt-desktop.exe -> ChatGPT.exe) или при Win32Exception учитываем имя процесса.
                    var isPrimaryExeName = string.Equals(procName, targetName, StringComparison.OrdinalIgnoreCase);
                    if (isPrimaryExeName && !isWindowsAppsAlias)
                    {
                        try
                        {
                            var mainPath = proc.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(mainPath)
                                && !string.Equals(NormalizePathSafe(mainPath), normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }
                        catch (Win32Exception)
                        {
                            // Нет доступа к MainModule (например, другая разрядность/уровень прав) — считаем совпадением по имени
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }
                    }

                    pids.Add(proc.Id);
                }
                catch
                {
                    // Игнорируем процессы, к которым нет доступа
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        return pids.Order().ToList();
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
}
