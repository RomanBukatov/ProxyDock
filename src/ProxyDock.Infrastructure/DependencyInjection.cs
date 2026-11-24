using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Infrastructure.Launching;
using ProxyDock.Infrastructure.Logging;
using ProxyDock.Infrastructure.Persistence;
using ProxyDock.Infrastructure.Platform;
using ProxyDock.Infrastructure.Proxy;
using ProxyDock.Infrastructure.Security;
using ProxyDock.Infrastructure.Updates;

namespace ProxyDock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProxyDockInfrastructure(
        this IServiceCollection services,
        string? customDataRoot = null,
        UpdateRuntimeOptions? updateOptions = null)
    {
        var pathProvider = new AppDataPathProvider(customDataRoot);
        var fileLoggerProvider = new FileLoggerProvider(pathProvider);

        services.AddSingleton<IPathProvider>(pathProvider);
        services.AddSingleton(updateOptions ?? new UpdateRuntimeOptions());
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
        services.AddSingleton<IUpdateService, VelopackUpdateService>();

        return services;
    }
}
