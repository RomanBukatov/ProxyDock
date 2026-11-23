using System.Runtime.Versioning;
using Microsoft.Win32;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.Infrastructure.Platform;

/// <summary>
/// Управляет автозапуском ProxyDock через ветку HKCU (без прав администратора).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProxyDock";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть ветку автозагрузки HKCU.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу ProxyDock.");
        }

        key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
    }
}
