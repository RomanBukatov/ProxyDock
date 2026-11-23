namespace ProxyDock.Core.Enums;

/// <summary>
/// Тип упаковки/исполняемого файла Windows-приложения.
/// </summary>
public enum AppPackagingKind
{
    Unknown = 0,
    Win32Executable = 1,
    MsixExecutionAlias = 2,
    ScriptOrShim = 3
}
