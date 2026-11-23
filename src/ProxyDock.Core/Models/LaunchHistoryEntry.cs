using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Запись о попытке запуска приложения (без секретов).
/// </summary>
public sealed class LaunchHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid ApplicationId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public LaunchKind LaunchKind { get; set; }
    public LaunchRoutingStrategy Strategy { get; set; }
    public LaunchStatus Status { get; set; }
    public string? ProxyName { get; set; }
    public string? ProxyDisplayAddress { get; set; }
    public string? ResolvedTarget { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? RedactedArguments { get; set; }
    public int? ProcessId { get; set; }
    public ProxyApplicabilityLevel ApplicabilityLevel { get; set; } = ProxyApplicabilityLevel.Unverified;
    public string? ApplicabilityNote { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
}
