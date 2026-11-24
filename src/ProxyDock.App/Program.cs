using Avalonia;

namespace ProxyDock.App;

public sealed class Program
{
    public const string SingleInstanceMutexName = @"Local\ProxyDock_SingleInstance_v1";
    public const string ActivationEventName = @"Local\ProxyDock_ShowWindow_v1";

    [STAThread]
    public static void Main(string[] args)
    {
        var verifyStartup = args.Any(a => a.Equals("--verify-startup", StringComparison.OrdinalIgnoreCase));
        var customDataDir = TryParseDataDirArgument(args);
        Mutex? instanceMutex = null;

        if (!verifyStartup && customDataDir is null)
        {
            instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var showEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                    showEvent.Set();
                }
                catch
                {
                    // Если первый экземпляр ещё инициализируется, просто выходим без дублирования окна
                }

                instanceMutex.Dispose();
                return;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (instanceMutex is not null)
            {
                try
                {
                    instanceMutex.ReleaseMutex();
                }
                catch
                {
                    // Игнорируем при завершении
                }

                instanceMutex.Dispose();
            }
        }
    }

    public static string? TryParseDataDirArgument(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            string? rawValue = null;
            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    rawValue = args[i + 1];
                }
            }
            else if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                rawValue = arg["--data-dir=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                var cleaned = Environment.ExpandEnvironmentVariables(rawValue.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    return Path.GetFullPath(cleaned);
                }
            }
        }

        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
