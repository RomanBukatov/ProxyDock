namespace ProxyDock.Core.Enums;

/// <summary>
/// Честная оценка применимости прокси-стратегии для конкретного приложения.
/// </summary>
public enum ProxyApplicabilityLevel
{
    Confirmed = 0,
    LikelyCompatible = 1,
    NeedsVerification = 2,
    Unverified = 3
}
