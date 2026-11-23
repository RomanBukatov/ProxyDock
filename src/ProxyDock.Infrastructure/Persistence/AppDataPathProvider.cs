using ProxyDock.Core.Interfaces;

namespace ProxyDock.Infrastructure.Persistence;

public sealed class AppDataPathProvider : IPathProvider
{
    public AppDataPathProvider(string? customRootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(customRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyDock")
            : Path.GetFullPath(customRootDirectory);

        ConfigDirectory = Path.Combine(RootDirectory, "config");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        BackupsDirectory = Path.Combine(RootDirectory, "backups");
        SecretsDirectory = Path.Combine(RootDirectory, "secrets");
        RuntimeDirectory = Path.Combine(RootDirectory, "runtime");

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }

    public string RootDirectory { get; }
    public string ConfigDirectory { get; }
    public string LogsDirectory { get; }
    public string BackupsDirectory { get; }
    public string SecretsDirectory { get; }
    public string RuntimeDirectory { get; }

    public string ApplicationsFilePath => Path.Combine(ConfigDirectory, "applications.json");
    public string ProxiesFilePath => Path.Combine(ConfigDirectory, "proxies.json");
    public string SettingsFilePath => Path.Combine(ConfigDirectory, "settings.json");
    public string HistoryFilePath => Path.Combine(ConfigDirectory, "history.json");
}
