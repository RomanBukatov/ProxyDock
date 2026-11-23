using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Результат асинхронной проверки HTTP-прокси.
/// </summary>
public sealed class ProxyTestResult
{
    public ProxyCheckStatus Status { get; init; }
    public bool TcpSucceeded { get; init; }
    public bool HttpsSucceeded { get; init; }
    public long? TcpLatencyMs { get; init; }
    public long? HttpsLatencyMs { get; init; }
    public string? ExternalIp { get; init; }
    public int? HttpStatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? SuggestedAction { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Результат обнаружения исполняемого файла или CLI-команды.
/// </summary>
public sealed class ExecutableDiscoveryResult
{
    public bool Found { get; init; }
    public string? ResolvedPath { get; init; }
    public string? DisplaySource { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsBatchOrScript { get; init; }
    public DesktopAppStackInfo? StackInfo { get; init; }
}

/// <summary>
/// Сведения о запущенном приложении, отслеживаемом ProxyDock.
/// </summary>
public sealed class TrackedProcessInfo
{
    public int ProcessId { get; init; }
    public Guid ApplicationId { get; init; }
    public string ApplicationName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public bool HasExited { get; set; }
}
