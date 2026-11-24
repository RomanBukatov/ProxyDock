using System.Collections.Concurrent;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App;
using ProxyDock.Infrastructure;

namespace ProxyDock.Tests;

internal static class AvaloniaTestRuntime
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread UiThread = CreateUiThread();

    private static Thread CreateUiThread()
    {
        var ready = new ManualResetEventSlim(false);
        Exception? initError = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    Program.BuildAvaloniaApp().SetupWithoutStarting();
                }
            }
            catch (Exception ex)
            {
                initError = ex;
            }
            finally
            {
                ready.Set();
            }

            foreach (var work in Queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "AvaloniaTestUiThread"
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        ready.Wait();
        if (initError is not null)
        {
            throw new InvalidOperationException("Failed to initialize Avalonia test runtime.", initError);
        }

        return thread;
    }

    public static void RunOnUiThread(Action action)
    {
        _ = UiThread;
        if (Thread.CurrentThread == UiThread)
        {
            action();
            return;
        }

        var done = new ManualResetEventSlim(false);
        Exception? error = null;
        Queue.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}

internal sealed class TempTestScope : IDisposable
{
    public TempTestScope()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "ProxyDock.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);

        var services = new ServiceCollection();
        services.AddProxyDockInfrastructure(RootPath);
        Services = services.BuildServiceProvider();
    }

    public string RootPath { get; }
    public ServiceProvider Services { get; }

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Игнорируем возможные задержки освобождения дескрипторов на Windows
        }
    }
}

internal static class TestPaths
{
    public static string LocateTestChildExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);

        while (current is not null)
        {
            var testsDir = Path.Combine(current.FullName, "tests", "ProxyDock.TestChild", "bin");
            if (Directory.Exists(testsDir))
            {
                var exe = Directory.EnumerateFiles(testsDir, "ProxyDock.TestChild.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (exe is not null)
                {
                    return exe;
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Не найден тестовый дочерний процесс ProxyDock.TestChild.exe. Соберите решение перед запуском тестов.");
    }
}
