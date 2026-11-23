using Microsoft.Extensions.DependencyInjection;
using ProxyDock.Infrastructure;

namespace ProxyDock.Tests;

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
