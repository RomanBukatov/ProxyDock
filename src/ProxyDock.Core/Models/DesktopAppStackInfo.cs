using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Результат анализа упаковки, технологического стека и применимости прокси для desktop-приложения.
/// </summary>
public sealed class DesktopAppStackInfo
{
    public AppPackagingKind PackagingKind { get; set; } = AppPackagingKind.Unknown;
    public AppRuntimeStack RuntimeStack { get; set; } = AppRuntimeStack.NativeOrUnknown;
    public ProxyApplicabilityLevel ApplicabilityLevel { get; set; } = ProxyApplicabilityLevel.Unverified;
    public bool SupportsChromiumProxyFlag { get; set; }
    public string? ResolvedPackageRoot { get; set; }
    public string? IconSourcePath { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Caveats { get; set; } = [];
}
