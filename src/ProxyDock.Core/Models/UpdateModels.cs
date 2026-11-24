using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

public sealed class UpdateReleaseInfo
{
    public required string Version { get; init; }
    public required string CurrentVersion { get; init; }
    public string ReleaseNotesMarkdown { get; init; } = string.Empty;
    public long? DownloadSizeBytes { get; init; }
    public string ReleasePageUrl { get; init; } = "https://github.com/RomanBukatov/ProxyDock/releases";
    public bool CanSelfUpdate { get; init; }
    public bool IsPrerelease { get; init; }
    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;
    public object? InternalPayload { get; init; }
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string CurrentVersion { get; init; }
    public UpdateReleaseInfo? AvailableRelease { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorDetails { get; init; }
    public bool CanSelfUpdate { get; init; }
}

public sealed record UpdateDownloadProgress(int Percentage, string StatusMessage);

public sealed class UpdateRuntimeOptions
{
    public bool IsAutoCheckSuppressed { get; init; }
    public string? TestUpdateSourcePath { get; init; }
    public string[] RestartArguments { get; init; } = [];
}
