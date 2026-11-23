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
        Mutex? instanceMutex = null;

        if (!verifyStartup)
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
