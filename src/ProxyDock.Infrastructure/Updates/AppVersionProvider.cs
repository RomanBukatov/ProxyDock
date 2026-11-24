using System.Reflection;

namespace ProxyDock.Infrastructure.Updates;

/// <summary>
/// Единый источник текущей версии приложения из AssemblyInformationalVersion (SemVer).
/// </summary>
public static class AppVersionProvider
{
    public static string GetCurrentVersion(Assembly? targetAssembly = null)
    {
        var asm = targetAssembly ?? Assembly.GetEntryAssembly() ?? typeof(AppVersionProvider).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plusIdx = info.IndexOf('+');
            var cleaned = (plusIdx >= 0 ? info[..plusIdx] : info).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        var asmVer = asm.GetName().Version;
        if (asmVer is not null)
        {
            return $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}";
        }

        return "0.1.0";
    }
}
