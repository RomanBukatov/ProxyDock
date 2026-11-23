using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Запрос на запуск профиля приложения.
/// </summary>
public sealed class LaunchRequest
{
    public required ApplicationProfile Application { get; init; }
    public bool ForceDirectNoProxy { get; init; }
    public Guid? ProxyOverrideId { get; init; }
    public bool IgnoreAlreadyRunningWarning { get; init; }
}

/// <summary>
/// Сформированный план запуска дочернего процесса.
/// </summary>
public sealed class LaunchPlan
{
    public required string FileName { get; init; }
    public string? WorkingDirectory { get; init; }
    public List<string> Arguments { get; init; } = [];
    public Dictionary<string, string> EnvironmentSet { get; init; } = new(StringComparer.Ordinal);
    public List<string> EnvironmentRemove { get; init; } = [];
    public LaunchRoutingStrategy Strategy { get; init; }
    public bool UseInteractiveTerminal { get; init; }
    public string? GeneratedScriptPath { get; init; }
    public string? ProxyDisplayAddress { get; init; }
    public ProxyApplicabilityLevel ApplicabilityLevel { get; init; } = ProxyApplicabilityLevel.Unverified;
    public string? ApplicabilityNote { get; init; }
    public List<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> SensitiveValues { get; init; } = [];
}

/// <summary>
/// Результат выполнения запуска процесса.
/// </summary>
public sealed class LaunchResult
{
    public LaunchStatus Status { get; init; }
    public int? ProcessId { get; init; }
    public LaunchRoutingStrategy Strategy { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? SuggestedAction { get; init; }
    public ProxyApplicabilityLevel ApplicabilityLevel { get; init; } = ProxyApplicabilityLevel.Unverified;
    public string? ApplicabilityNote { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<int> AlreadyRunningProcessIds { get; init; } = [];
    public bool RequiresExistingProcessConfirmation { get; init; }
    public LaunchHistoryEntry? HistoryEntry { get; init; }
}
