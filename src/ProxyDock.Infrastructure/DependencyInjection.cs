using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Interfaces;
using ProxyDock.Infrastructure.Launching;
using ProxyDock.Infrastructure.Logging;
using ProxyDock.Infrastructure.Persistence;
using ProxyDock.Infrastructure.Platform;
using ProxyDock.Infrastructure.Proxy;
using ProxyDock.Infrastructure.Security;

namespace ProxyDock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProxyDockInfrastructure(
        this IServiceCollection services,
        string? customDataRoot = null)
    {
        var pathProvider = new AppDataPathProvider(customDataRoot);
        var fileLoggerProvider = new FileLoggerProvider(pathProvider);

        services.AddSingleton<IPathProvider>(pathProvider);
        services.AddSingleton(fileLoggerProvider);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(fileLoggerProvider);
        });

        services.AddSingleton<IConfigDiagnosticsStore, ConfigDiagnosticsStore>();
        services.AddSingleton<JsonConfigStore>();
        services.AddSingleton<ICredentialStore, DpapiCredentialStore>();

        services.AddSingleton<IProxyProfileRepository, ProxyProfileRepository>();
        services.AddSingleton<IApplicationProfileRepository, ApplicationProfileRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ILaunchHistoryRepository, LaunchHistoryRepository>();

        services.AddSingleton<IProxyProfileService, ProxyProfileService>();
        services.AddSingleton<IApplicationProfileService, ApplicationProfileService>();
        services.AddSingleton<IProxyConnectivityTester, ProxyConnectivityTester>();
        services.AddSingleton<IConfigExportService, ConfigExportService>();

        services.AddSingleton<IExecutableDiscoveryService, WindowsExecutableDiscoveryService>();
        services.AddSingleton<IProcessTracker, WindowsProcessTracker>();
        services.AddSingleton<IStartupManager, WindowsStartupManager>();
        services.AddSingleton<IAppIconExtractor, WindowsAppIconExtractor>();

        services.AddSingleton<ILaunchStrategy, DesktopLaunchStrategy>();
        services.AddSingleton<ILaunchStrategy, CliTerminalLaunchStrategy>();
        services.AddSingleton<IApplicationLauncher, ApplicationLauncher>();

        return services;
    }
}
