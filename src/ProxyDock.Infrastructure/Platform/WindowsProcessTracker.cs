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

/// <summary>
/// Отслеживает запущенные процессы событийно (без фонового опроса) и не завершает их при закрытии ProxyDock.
/// Выполняет умную инспекцию работающих процессов (MSIX identity, видимые окна, Electron/Chromium helper-процессы).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessTracker : IProcessTracker
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ProcessCommandLineInformation = 60;
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

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
    {
        var normalizedTargetPath = NormalizePathSafe(resolvedExecutablePath);
        var targetName = Path.GetFileNameWithoutExtension(normalizedTargetPath);
        var isWindowsAppsAlias = MsixPackageResolver.IsWindowsAppsAliasPath(normalizedTargetPath);

        var link = MsixPackageResolver.TryReadAppExecLink(normalizedTargetPath);
        var msixPkg = MsixPackageResolver.TryResolvePackage(normalizedTargetPath, templateKey);

        var expectedFamily = link?.PackageFamilyName ?? msixPkg?.PackageFamilyName;
        var expectedPackageRoot = msixPkg?.PackageRoot;
        var expectedTargetExe = link?.TargetExecutablePath ?? msixPkg?.TargetExecutablePath;

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

        var parentPids = SnapshotParentProcessIds();
        var visibleWindows = SnapshotVisibleWindowsByPid();
        var seenPids = new HashSet<int>();
        var rawCandidates = new List<RawProcessCandidate>();

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
                    if (proc.Id == Environment.ProcessId || proc.HasExited || !seenPids.Add(proc.Id))
                    {
                        continue;
                    }

                    var pid = proc.Id;
                    parentPids.TryGetValue(pid, out var parentPidVal);
                    int? parentPid = parentPids.ContainsKey(pid) ? parentPidVal : null;

                    visibleWindows.TryGetValue(pid, out var windowTitle);
                    var hasVisibleWindow = visibleWindows.ContainsKey(pid);

                    string? exePath = null;
                    string? commandLine = null;
                    string? pkgFullName = null;
                    string? pkgFamilyName = null;

                    using (var handle = OpenProcessLimited(pid))
                    {
                        if (handle is not null && !handle.IsInvalid)
                        {
                            exePath = TryQueryFullProcessImageName(handle);
                            pkgFullName = TryGetPackageFullName(handle);
                            pkgFamilyName = TryGetPackageFamilyName(handle);
                            commandLine = TryGetProcessCommandLine(handle);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        try
                        {
                            exePath = proc.MainModule?.FileName;
                        }
                        catch
                        {
                            // Игнорируем отказ доступа к MainModule
                        }
                    }

                    var matchedByPrimaryName = string.Equals(procName, targetName, StringComparison.OrdinalIgnoreCase);

                    rawCandidates.Add(new RawProcessCandidate(
                        ProcessId: pid,
                        ParentProcessId: parentPid,
                        ProcessName: proc.ProcessName,
                        ExecutablePath: exePath,
                        CommandLine: commandLine,
                        PackageFullName: pkgFullName,
                        PackageFamilyName: pkgFamilyName,
                        HasVisibleWindow: hasVisibleWindow,
                        MainWindowTitle: windowTitle,
                        MatchedByPrimaryName: matchedByPrimaryName,
                        IsTrackedByProxyDock: _tracked.ContainsKey(pid)));
                }
                catch
                {
                    // Игнорируем процессы, завершившиеся во время обхода
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        var matchContext = new ProcessMatchContext(
            ExpectedExecutablePath: normalizedTargetPath,
            ExpectedTargetExecutablePath: expectedTargetExe,
            ExpectedPackageFamilyName: expectedFamily,
            ExpectedPackageRoot: expectedPackageRoot,
            IsWindowsAppsAlias: isWindowsAppsAlias);

        return ProcessInstanceClassifier.Classify(rawCandidates, matchContext);
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

    private static string? TryQueryFullProcessImageName(SafeProcessHandle handle)
    {
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

    private static Dictionary<int, int> SnapshotParentProcessIds()
    {
        var result = new Dictionary<int, int>();
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
                result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32NextW(snap, ref entry));
        }
        catch
        {
            // Игнорируем сбои снимка процессов
        }

        return result;
    }

    private static Dictionary<int, string?> SnapshotVisibleWindowsByPid()
    {
        var result = new Dictionary<int, string?>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                if (!GetWindowRect(hWnd, out var rect) || rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
                {
                    return true;
                }

                var exStyle = GetWindowLongPtrW(hWnd, GwlExStyle).ToInt64();
                var title = GetWindowTitleSafe(hWnd);

                if ((exStyle & WsExToolWindow) != 0 && string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0)
                {
                    return true;
                }

                if (!result.TryGetValue((int)pid, out var existing) || string.IsNullOrWhiteSpace(existing))
                {
                    result[(int)pid] = string.IsNullOrWhiteSpace(title) ? null : title;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Игнорируем сбои перечисления окон
        }

        return result;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
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
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
