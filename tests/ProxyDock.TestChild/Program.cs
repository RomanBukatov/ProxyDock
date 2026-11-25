using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ProxyDock.TestChild;

internal sealed record ChildReport(
    int ProcessId,
    string WorkingDirectory,
    string[] Args,
    Dictionary<string, string?> Environment);

internal static class Program
{
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmDestroy = 0x0002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int GwlExStyle = -20;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExAppWindow = 0x00040000;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private static readonly string[] InspectedKeys =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
        "NODE_USE_ENV_PROXY"
    ];

    private static WndProcDelegate? _pinnedWndProc;

    public static async Task<int> Main(string[] args)
    {
        string? outputPath = null;
        string? postSleepOutputPath = null;
        string? childrenOutputPath = null;
        string? respawnLogPath = null;
        string? trayWindowMode = null;
        string? gracefulMarkerPath = null;
        var spawnChildrenCount = 0;
        var surviveParent = false;
        var sleepMs = 0;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (args[i] == "--post-sleep-output" && i + 1 < args.Length)
            {
                postSleepOutputPath = args[++i];
            }
            else if (args[i] == "--children-output" && i + 1 < args.Length)
            {
                childrenOutputPath = args[++i];
            }
            else if (args[i] == "--respawn-child" && i + 1 < args.Length)
            {
                respawnLogPath = args[++i];
            }
            else if (args[i] == "--tray-window" && i + 1 < args.Length)
            {
                trayWindowMode = args[++i];
            }
            else if (args[i] == "--graceful-marker" && i + 1 < args.Length)
            {
                gracefulMarkerPath = args[++i];
            }
            else if (args[i] == "--spawn-children" && i + 1 < args.Length && int.TryParse(args[++i], out var count))
            {
                spawnChildrenCount = Math.Clamp(count, 0, 8);
            }
            else if (args[i] == "--survive-parent")
            {
                surviveParent = true;
            }
            else if (args[i] == "--sleep-ms" && i + 1 < args.Length && int.TryParse(args[++i], out var ms))
            {
                sleepMs = Math.Clamp(ms, 0, 30_000);
            }
        }

        await WriteReportAsync(args, outputPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var childProcesses = new List<Process>();
        Task? respawnTask = null;

        try
        {
            if (spawnChildrenCount > 0)
            {
                var exePath = Environment.ProcessPath!;
                var childHoldMs = surviveParent ? Math.Max(sleepMs, 15_000) : 25_000;
                string[] childTypes = ["renderer", "gpu-process", "utility"];
                var spawnedPids = new List<int>();

                for (var c = 0; c < spawnChildrenCount; c++)
                {
                    var typeFlag = childTypes[c % childTypes.Length];
                    var child = Process.Start(new ProcessStartInfo(exePath, $"--type={typeFlag} --sleep-ms {childHoldMs}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (child is not null)
                    {
                        childProcesses.Add(child);
                        spawnedPids.Add(child.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(childrenOutputPath))
                {
                    await WriteAtomicTextAsync(childrenOutputPath, JsonSerializer.Serialize(spawnedPids));
                }
            }

            if (!string.IsNullOrWhiteSpace(respawnLogPath))
            {
                respawnTask = RunRespawnLoopAsync(respawnLogPath, cts.Token);
            }

            if (!string.IsNullOrWhiteSpace(trayWindowMode))
            {
                RunTrayMessageLoop(trayWindowMode, gracefulMarkerPath, sleepMs > 0 ? sleepMs : 25_000);
            }
            else
            {
                if (args.Contains("--test-window"))
                {
                    RunTestWindow(28_000);
                }

                if (sleepMs > 0)
                {
                    await Task.Delay(sleepMs, cts.Token);
                }
            }

            if (!string.IsNullOrWhiteSpace(postSleepOutputPath))
            {
                await WriteReportAsync(args, postSleepOutputPath);
            }
        }
        catch (OperationCanceledException)
        {
            // Ограничение максимального времени жизни тестового процесса
        }
        finally
        {
            cts.Cancel();
            if (respawnTask is not null)
            {
                try { await respawnTask; } catch { }
            }

            foreach (var cp in childProcesses)
            {
                cp.Dispose();
            }
        }

        return 0;
    }

    private static async Task RunRespawnLoopAsync(string pidLogPath, CancellationToken token)
    {
        var exePath = Environment.ProcessPath!;
        var loggedPids = new List<int>();

        while (!token.IsCancellationRequested)
        {
            using var helper = Process.Start(new ProcessStartInfo(exePath, "--type=gpu-process --sleep-ms 25000")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (helper is null)
            {
                await Task.Delay(100, token);
                continue;
            }

            loggedPids.Add(helper.Id);
            await WriteAtomicTextAsync(pidLogPath, JsonSerializer.Serialize(loggedPids));

            try
            {
                await helper.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void RunTrayMessageLoop(string mode, string? gracefulMarkerPath, int maxLifetimeMs)
    {
        var done = new ManualResetEventSlim(false);
        var uiThread = new Thread(() =>
        {
            var className = $"ProxyDockTestTray_{Environment.ProcessId}_{Environment.TickCount64}";
            var hInstance = GetModuleHandleW(null);
            var uiThreadId = GetCurrentThreadId();
            var hWnd = IntPtr.Zero;

            _pinnedWndProc = (wnd, msg, wParam, lParam) =>
            {
                switch (msg)
                {
                    case WmClose:
                        // Эмуляция поведения Codex/Claude Desktop: закрытие окна скрывает его в системный трей
                        ShowWindow(wnd, SwHide);
                        return IntPtr.Zero;

                    case WmQueryEndSession:
                        if (string.Equals(mode, "veto-endsession", StringComparison.OrdinalIgnoreCase))
                        {
                            return IntPtr.Zero;
                        }

                        return new IntPtr(1);

                    case WmEndSession:
                        if (wParam != IntPtr.Zero
                            && string.Equals(mode, "honor-endsession", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(gracefulMarkerPath))
                            {
                                TryWriteTextSync(gracefulMarkerPath, $"graceful-exit:{Environment.ProcessId}");
                            }

                            DestroyWindow(wnd);
                            PostQuitMessage(0);
                        }

                        return IntPtr.Zero;

                    case WmDestroy:
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }

                return DefWindowProcW(wnd, msg, wParam, lParam);
            };

            var wc = new WndClass
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_pinnedWndProc),
                hInstance = hInstance,
                lpszClassName = className
            };
            RegisterClassW(ref wc);

            hWnd = CreateWindowExW(
                WsExAppWindow,
                className,
                "ProxyDock Test Tray App",
                WsOverlappedWindow,
                100,
                100,
                320,
                200,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SwShowNoActivate);
                UpdateWindow(hWnd);
            }

            using var timer = new Timer(_ =>
            {
                if (hWnd != IntPtr.Zero)
                {
                    PostThreadMessageW(uiThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                }
            }, null, maxLifetimeMs, Timeout.Infinite);

            while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }

            done.Set();
        })
        {
            IsBackground = false
        };

        if (OperatingSystem.IsWindows())
        {
            uiThread.SetApartmentState(ApartmentState.STA);
        }
        uiThread.Start();
        done.Wait();
    }

    private static void RunTestWindow(int maxLifetimeMs)
    {
        var done = new ManualResetEventSlim(false);
        var uiThread = new Thread(() =>
        {
            var className = $"ProxyDockTestWin_{Environment.ProcessId}_{Environment.TickCount64}";
            var hInstance = GetModuleHandleW(null);
            var uiThreadId = GetCurrentThreadId();
            var hWnd = IntPtr.Zero;

            _pinnedWndProc = (wnd, msg, wParam, lParam) =>
            {
                switch (msg)
                {
                    case WmClose:
                        DestroyWindow(wnd);
                        return IntPtr.Zero;

                    case WmDestroy:
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }

                return DefWindowProcW(wnd, msg, wParam, lParam);
            };

            var wc = new WndClass
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_pinnedWndProc),
                hInstance = hInstance,
                lpszClassName = className
            };
            RegisterClassW(ref wc);

            hWnd = CreateWindowExW(
                WsExAppWindow | WsExTopMost,
                className,
                "ProxyDock.TestChild",
                WsOverlappedWindow,
                120,
                120,
                320,
                180,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SwShowNoActivate);
                UpdateWindow(hWnd);
                SetWindowPos(hWnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }

            // Подавляем инжектированные ОС неактивируемые overlay-окна (например UAC Input Indicator),
            // чтобы Process.MainWindowHandle и CloseMainWindow в .NET гарантированно адресовали окно теста.
            using var overlayGuardTimer = new Timer(_ =>
            {
                if (hWnd == IntPtr.Zero)
                {
                    return;
                }

                var currentPid = (uint)Environment.ProcessId;
                EnumWindows((other, _) =>
                {
                    if (other == hWnd)
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(other, out var pid);
                    if (pid == currentPid
                        && IsWindowVisible(other)
                        && GetWindowTextLengthW(other) == 0
                        && (GetWindowLongPtrW(other, GwlExStyle).ToInt64() & WsExNoActivate) != 0)
                    {
                        ShowWindow(other, SwHide);
                    }

                    return true;
                }, IntPtr.Zero);

                SetWindowPos(hWnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }, null, 25, 40);

            using var lifetimeTimer = new Timer(_ =>
            {
                if (hWnd != IntPtr.Zero)
                {
                    PostThreadMessageW(uiThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                }
            }, null, maxLifetimeMs, Timeout.Infinite);

            while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }

            done.Set();
        })
        {
            IsBackground = false
        };

        if (OperatingSystem.IsWindows())
        {
            uiThread.SetApartmentState(ApartmentState.STA);
        }
        uiThread.Start();
        done.Wait();
    }

    private static async Task WriteReportAsync(string[] args, string? outputPath)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in InspectedKeys)
        {
            env[key] = Environment.GetEnvironmentVariable(key);
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string keyStr && keyStr.StartsWith("PROXYDOCK_TEST_", StringComparison.Ordinal))
            {
                env[keyStr] = entry.Value?.ToString();
            }
        }

        var report = new ChildReport(
            ProcessId: Environment.ProcessId,
            WorkingDirectory: Environment.CurrentDirectory,
            Args: args,
            Environment: env);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await WriteAtomicTextAsync(outputPath, json);
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    private static async Task WriteAtomicTextAsync(string outputPath, string content)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var pending = $"{outputPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(pending, content, Encoding.UTF8);
        File.Move(pending, outputPath, overwrite: true);
    }

    private static void TryWriteTextSync(string outputPath, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var pending = $"{outputPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(pending, content, Encoding.UTF8);
            File.Move(pending, outputPath, overwrite: true);
        }
        catch
        {
            // Игнорируем в тестовом окне
        }
    }
}
