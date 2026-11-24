using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Platform;

internal enum MsixLogoAltForm
{
    Plated = 0,
    Unplated = 1,
    LightUnplated = 2
}

internal sealed record AppExecLinkInfo(
    string PackageFamilyName,
    string AppUserModelId,
    string? ApplicationId,
    string TargetExecutablePath);

internal sealed record MsixPackageInfo(
    string PackageRoot,
    string ManifestPath,
    string? ApplicationId,
    string? AliasFileName,
    string? TargetExecutablePath);

internal sealed record MsixLogoReference(
    string RelativePath,
    int BaseLogicalSize,
    int KindPriority);

internal sealed record MsixManifestVisualInfo(
    string PackageRoot,
    string? ApplicationExecutablePath,
    string? BackgroundColor,
    IReadOnlyList<MsixLogoReference> LogoReferences);

internal sealed record MsixResolvedLogoAsset(
    string FilePath,
    MsixLogoAltForm AltForm,
    int EstimatedPixelSize,
    string? BackgroundColor);

/// <summary>
/// Безопасно (без прав администратора и без обхода прав на корневой каталог WindowsApps)
/// сопоставляет App Execution Alias или упакованный .exe с установленным MSIX-пакетом,
/// разбирает AppxManifest.xml и подбирает оптимальный графический ресурс иконки с учётом scale/targetsize/unplated.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MsixPackageResolver
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint IoReparseTagAppExecLink = 0x8000001B;

    private const string PackagesRegistrySubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static bool IsWindowsAppsAliasPath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    public static AppExecLinkInfo? TryReadAppExecLink(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            using var handle = CreateFileW(
                filePath,
                dwDesiredAccess: 0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(
                    handle,
                    FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    buffer.Length,
                    out var bytesReturned,
                    IntPtr.Zero)
                || bytesReturned < 16)
            {
                return null;
            }

            var tag = BitConverter.ToUInt32(buffer, 0);
            if (tag != IoReparseTagAppExecLink)
            {
                return null;
            }

            var dataLength = BitConverter.ToUInt16(buffer, 4);
            var payloadBytes = Math.Min(dataLength - 4, bytesReturned - 12);
            if (payloadBytes <= 0)
            {
                return null;
            }

            var decoded = Encoding.Unicode.GetString(buffer, 12, payloadBytes);
            var parts = decoded.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return null;
            }

            var familyName = parts[0].Trim();
            var aumid = parts[1].Trim();
            var targetExe = parts[2].Trim();

            string? appId = null;
            var bangIdx = aumid.IndexOf('!');
            if (bangIdx >= 0 && bangIdx + 1 < aumid.Length)
            {
                appId = aumid[(bangIdx + 1)..];
            }

            return new AppExecLinkInfo(familyName, aumid, appId, targetExe);
        }
        catch
        {
            return null;
        }
    }

    public static MsixPackageInfo? TryResolvePackage(string filePath, string? templateKey = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = Environment.ExpandEnvironmentVariables(filePath.Trim().Trim('"'));
        var aliasName = Path.GetFileName(normalized);

        // 1. Прямое чтение NTFS reparse point IO_REPARSE_TAG_APPEXECLINK (работает без админ-прав для любых MSIX alias)
        var link = TryReadAppExecLink(normalized);
        if (link is not null)
        {
            var rootFromTarget = FindAncestorWithManifest(link.TargetExecutablePath);
            if (rootFromTarget is not null)
            {
                return new MsixPackageInfo(
                    rootFromTarget,
                    Path.Combine(rootFromTarget, "AppxManifest.xml"),
                    link.ApplicationId,
                    aliasName,
                    link.TargetExecutablePath);
            }

            var rootFromFamily = TryResolvePackageRootFromRegistryByFamily(link.PackageFamilyName);
            if (rootFromFamily is not null)
            {
                return new MsixPackageInfo(
                    rootFromFamily,
                    Path.Combine(rootFromFamily, "AppxManifest.xml"),
                    link.ApplicationId,
                    aliasName,
                    link.TargetExecutablePath);
            }
        }

        // 2. Если передан файл уже внутри распакованного/установленного MSIX-каталога с AppxManifest.xml
        var ancestorRoot = FindAncestorWithManifest(normalized);
        if (ancestorRoot is not null)
        {
            return new MsixPackageInfo(
                ancestorRoot,
                Path.Combine(ancestorRoot, "AppxManifest.xml"),
                ApplicationId: null,
                AliasFileName: aliasName,
                TargetExecutablePath: normalized);
        }

        // 3. Fallback через HKCU AppModel\Repository\Packages для путей WindowsApps
        if (IsWindowsAppsAliasPath(normalized))
        {
            var regRoot = TryResolvePackageRootFromRegistryByAlias(normalized, templateKey);
            if (regRoot is not null)
            {
                return new MsixPackageInfo(
                    regRoot,
                    Path.Combine(regRoot, "AppxManifest.xml"),
                    ApplicationId: null,
                    AliasFileName: aliasName,
                    TargetExecutablePath: null);
            }
        }

        return null;
    }

    public static MsixManifestVisualInfo? ParseManifestVisuals(MsixPackageInfo package)
    {
        if (string.IsNullOrWhiteSpace(package.ManifestPath) || !File.Exists(package.ManifestPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(package.ManifestPath);
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            var appsParent = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Applications");
            var applications = appsParent?.Elements().Where(e => e.Name.LocalName == "Application").ToList() ?? [];

            var matchedApp = SelectBestApplicationElement(applications, package);

            string? resolvedAppExe = null;
            string? backgroundColor = null;
            var logos = new List<MsixLogoReference>();

            if (matchedApp is not null)
            {
                var relExe = matchedApp.Attribute("Executable")?.Value;
                if (!string.IsNullOrWhiteSpace(relExe))
                {
                    var candidateExe = CombinePackagePath(package.PackageRoot, relExe);
                    if (File.Exists(candidateExe))
                    {
                        resolvedAppExe = candidateExe;
                    }
                }

                var visualElements = matchedApp.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                if (visualElements is not null)
                {
                    backgroundColor = AttrLocal(visualElements, "BackgroundColor");

                    AddLogoRef(logos, AttrLocal(visualElements, "Square44x44Logo"), 44, kindPriority: 0);
                    AddLogoRef(logos, AttrLocal(visualElements, "SmallLogo"), 30, kindPriority: 1);

                    var defaultTile = visualElements.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultTile");
                    if (defaultTile is not null)
                    {
                        AddLogoRef(logos, AttrLocal(defaultTile, "Square71x71Logo"), 71, kindPriority: 1);
                    }

                    AddLogoRef(logos, AttrLocal(visualElements, "Square150x150Logo"), 150, kindPriority: 3);
                    AddLogoRef(logos, AttrLocal(visualElements, "Logo"), 150, kindPriority: 3);

                    if (defaultTile is not null)
                    {
                        AddLogoRef(logos, AttrLocal(defaultTile, "Square310x310Logo"), 310, kindPriority: 4);
                    }
                }
            }

            var propsElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var storeLogo = propsElement?.Elements().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
            AddLogoRef(logos, storeLogo, 50, kindPriority: 2);

            if (resolvedAppExe is null && !string.IsNullOrWhiteSpace(package.TargetExecutablePath) && File.Exists(package.TargetExecutablePath))
            {
                resolvedAppExe = package.TargetExecutablePath;
            }

            return new MsixManifestVisualInfo(
                package.PackageRoot,
                resolvedAppExe,
                backgroundColor,
                logos);
        }
        catch
        {
            return null;
        }
    }

    public static MsixResolvedLogoAsset? ResolveBestLogoAsset(
        MsixManifestVisualInfo manifest,
        int desiredPixelSize,
        bool preferLightTheme)
    {
        var targetPixels = Math.Clamp(desiredPixelSize, 32, 512);
        var allCandidates = new List<LogoCandidate>();

        foreach (var logoRef in manifest.LogoReferences)
        {
            DiscoverAssetVariants(manifest.PackageRoot, logoRef, allCandidates);
        }

        if (allCandidates.Count == 0)
        {
            return null;
        }

        // Исключаем high-contrast ресурсы, если доступны обычные ресурсы
        var usable = allCandidates.Where(c => !c.IsHighContrast).ToList();
        if (usable.Count == 0)
        {
            usable = allCandidates;
        }

        // Выбираем лучший кандидат:
        // 1. Предпочитаем логотип приложения (Square44x44Logo) перед широкопольными тайлами (Square150x150Logo),
        //    если у него есть ресурс достаточного качества (>= 48px или >= targetPixels).
        // 2. Учитываем соответствие DPI (EstimatedPixelSize >= targetPixels).
        // 3. Учитываем тему и unplated/lightunplated варианты.
        var best = usable
            .OrderBy(c => c.KindPriority == 0 && c.EstimatedPixels >= Math.Min(targetPixels, 64) ? 0 : c.KindPriority + 1)
            .ThenBy(c => c.EstimatedPixels >= targetPixels ? 0 : 1)
            .ThenBy(c => GetAltFormRank(c.AltForm, preferLightTheme))
            .ThenBy(c => c.EstimatedPixels >= targetPixels
                ? c.EstimatedPixels - targetPixels // минимальное превышение для чёткого downscale
                : targetPixels - c.EstimatedPixels) // максимальный из доступных, если все меньше targetPixels
            .FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        return new MsixResolvedLogoAsset(
            best.FilePath,
            best.AltForm,
            best.EstimatedPixels,
            manifest.BackgroundColor);
    }

    private static int GetAltFormRank(MsixLogoAltForm altForm, bool preferLightTheme)
    {
        if (preferLightTheme)
        {
            return altForm switch
            {
                MsixLogoAltForm.LightUnplated => 0,
                MsixLogoAltForm.Unplated => 1,
                _ => 2
            };
        }

        return altForm switch
        {
            MsixLogoAltForm.Unplated => 0,
            MsixLogoAltForm.Plated => 1,
            _ => 2
        };
    }

    private static void DiscoverAssetVariants(
        string packageRoot,
        MsixLogoReference logoRef,
        List<LogoCandidate> sink)
    {
        var normalizedRel = logoRef.RelativePath
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalizedRel))
        {
            return;
        }

        var fullSpecPath = Path.Combine(packageRoot, normalizedRel);
        var assetDir = Path.GetDirectoryName(fullSpecPath);
        var ext = Path.GetExtension(fullSpecPath);
        var stem = Path.GetFileNameWithoutExtension(fullSpecPath);

        if (string.IsNullOrWhiteSpace(assetDir) || string.IsNullOrWhiteSpace(stem))
        {
            return;
        }

        // На случай различия регистра имени папки в манифесте (например, Assets vs assets)
        var actualDir = ResolveCaseInsensitiveDirectory(packageRoot, Path.GetRelativePath(packageRoot, assetDir));
        if (actualDir is null || !Directory.Exists(actualDir))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".png";
        }

        // 1. Файлы непосредственно в папке ресурса: Square44x44Logo.png, Square44x44Logo.scale-200.png, Square44x44Logo.targetsize-256_altform-unplated.png
        TryCollectMatchingFilesInDirectory(actualDir, stem, ext, folderQualifierSegment: null, logoRef, sink);

        // 2. Подпапки квалификаторов MRT (например, Assets\scale-200\Square44x44Logo.png)
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(actualDir))
            {
                var subName = Path.GetFileName(subDir);
                if (subName.Contains("scale-", StringComparison.OrdinalIgnoreCase)
                    || subName.Contains("targetsize-", StringComparison.OrdinalIgnoreCase)
                    || subName.Contains("altform-", StringComparison.OrdinalIgnoreCase)
                    || subName.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
                {
                    TryCollectMatchingFilesInDirectory(subDir, stem, ext, subName, logoRef, sink);
                }
            }
        }
        catch
        {
            // Игнорируем недоступные подкаталоги
        }
    }

    private static void TryCollectMatchingFilesInDirectory(
        string directory,
        string stem,
        string ext,
        string? folderQualifierSegment,
        MsixLogoReference logoRef,
        List<LogoCandidate> sink)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly))
            {
                var fileStem = Path.GetFileNameWithoutExtension(file);
                string qualifierPart;

                if (fileStem.Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    qualifierPart = string.Empty;
                }
                else if (fileStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                {
                    qualifierPart = fileStem[(stem.Length + 1)..];
                }
                else
                {
                    continue;
                }

                var combinedQualifiers = string.IsNullOrWhiteSpace(folderQualifierSegment)
                    ? qualifierPart
                    : string.IsNullOrWhiteSpace(qualifierPart)
                        ? folderQualifierSegment
                        : $"{folderQualifierSegment}_{qualifierPart}";

                var parsed = ParseQualifiers(combinedQualifiers, logoRef.BaseLogicalSize);
                sink.Add(new LogoCandidate(
                    file,
                    logoRef.KindPriority,
                    parsed.EstimatedPixels,
                    parsed.AltForm,
                    parsed.IsHighContrast));
            }
        }
        catch
        {
            // Игнорируем ошибки чтения каталога
        }
    }

    private static (int EstimatedPixels, MsixLogoAltForm AltForm, bool IsHighContrast) ParseQualifiers(
        string qualifierString,
        int baseLogicalSize)
    {
        if (string.IsNullOrWhiteSpace(qualifierString))
        {
            return (baseLogicalSize, MsixLogoAltForm.Plated, false);
        }

        int? targetSize = null;
        int? scale = null;
        var altForm = MsixLogoAltForm.Plated;
        var isHighContrast = false;

        var tokens = qualifierString.Split(['_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.StartsWith("targetsize-", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(token["targetsize-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) && ts > 0)
                {
                    targetSize = ts;
                }
            }
            else if (token.StartsWith("scale-", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(token["scale-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc) && sc > 0)
                {
                    scale = sc;
                }
            }
            else if (token.StartsWith("altform-", StringComparison.OrdinalIgnoreCase))
            {
                var af = token["altform-".Length..];
                if (af.Equals("lightunplated", StringComparison.OrdinalIgnoreCase)
                    || af.Contains("lightunplated", StringComparison.OrdinalIgnoreCase))
                {
                    altForm = MsixLogoAltForm.LightUnplated;
                }
                else if (af.Equals("unplated", StringComparison.OrdinalIgnoreCase)
                         || af.Equals("colorful", StringComparison.OrdinalIgnoreCase)
                         || af.Contains("unplated", StringComparison.OrdinalIgnoreCase))
                {
                    altForm = MsixLogoAltForm.Unplated;
                }
            }
            else if (token.StartsWith("contrast-", StringComparison.OrdinalIgnoreCase))
            {
                var contrastVal = token["contrast-".Length..];
                if (!contrastVal.Equals("standard", StringComparison.OrdinalIgnoreCase))
                {
                    isHighContrast = true;
                }
            }
        }

        var estimated = targetSize
            ?? (scale.HasValue
                ? Math.Max(16, (int)Math.Round(baseLogicalSize * (scale.Value / 100.0)))
                : baseLogicalSize);

        return (estimated, altForm, isHighContrast);
    }

    private static XElement? SelectBestApplicationElement(IReadOnlyList<XElement> applications, MsixPackageInfo package)
    {
        if (applications.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(package.ApplicationId))
        {
            var byId = applications.FirstOrDefault(a =>
                string.Equals(a.Attribute("Id")?.Value, package.ApplicationId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(package.AliasFileName))
        {
            var byAlias = applications.FirstOrDefault(a =>
                a.Descendants().Any(d =>
                    d.Name.LocalName == "ExecutionAlias"
                    && string.Equals(d.Attribute("Alias")?.Value, package.AliasFileName, StringComparison.OrdinalIgnoreCase)));
            if (byAlias is not null)
            {
                return byAlias;
            }
        }

        if (!string.IsNullOrWhiteSpace(package.TargetExecutablePath))
        {
            var targetLeaf = Path.GetFileName(package.TargetExecutablePath);
            var byExe = applications.FirstOrDefault(a =>
            {
                var exeAttr = a.Attribute("Executable")?.Value;
                if (string.IsNullOrWhiteSpace(exeAttr))
                {
                    return false;
                }

                var fullCandidate = CombinePackagePath(package.PackageRoot, exeAttr);
                return string.Equals(fullCandidate, package.TargetExecutablePath, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(Path.GetFileName(fullCandidate), targetLeaf, StringComparison.OrdinalIgnoreCase);
            });

            if (byExe is not null)
            {
                return byExe;
            }
        }

        // Предпочитаем основное приложение пакета, которое отображается в меню Пуск (не AppListEntry="none")
        var primaryVisible = applications.FirstOrDefault(a =>
        {
            var ve = a.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            var appListEntry = ve is null ? null : AttrLocal(ve, "AppListEntry");
            return !string.Equals(appListEntry, "none", StringComparison.OrdinalIgnoreCase);
        });

        return primaryVisible ?? applications[0];
    }

    private static string? AttrLocal(XElement element, string localName)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static void AddLogoRef(List<MsixLogoReference> list, string? relativePath, int baseLogicalSize, int kindPriority)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var trimmed = relativePath.Trim();
        if (list.Any(x => string.Equals(x.RelativePath, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        list.Add(new MsixLogoReference(trimmed, baseLogicalSize, kindPriority));
    }

    private static string CombinePackagePath(string packageRoot, string relativePath)
    {
        var cleanRel = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(packageRoot, cleanRel));
    }

    private static string? ResolveCaseInsensitiveDirectory(string rootDir, string relativeDir)
    {
        var direct = Path.Combine(rootDir, relativeDir);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var current = rootDir;
        var segments = relativeDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var next = Path.Combine(current, seg);
            if (Directory.Exists(next))
            {
                current = next;
                continue;
            }

            try
            {
                var match = Directory.EnumerateDirectories(current)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), seg, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return null;
                }

                current = match;
            }
            catch
            {
                return null;
            }
        }

        return Directory.Exists(current) ? current : null;
    }

    private static string? FindAncestorWithManifest(string? fileOrDirPath)
    {
        if (string.IsNullOrWhiteSpace(fileOrDirPath))
        {
            return null;
        }

        try
        {
            var current = Directory.Exists(fileOrDirPath)
                ? Path.GetFullPath(fileOrDirPath)
                : Path.GetDirectoryName(Path.GetFullPath(fileOrDirPath));

            for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                var manifestCandidate = Path.Combine(current, "AppxManifest.xml");
                if (File.Exists(manifestCandidate))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // Игнорируем некорректные пути или недоступные родительские папки (например, корень WindowsApps)
        }

        return null;
    }

    private static string? TryResolvePackageRootFromRegistryByFamily(string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return null;
        }

        var lastUnderscore = packageFamilyName.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore + 1 >= packageFamilyName.Length)
        {
            return null;
        }

        var identityPrefix = packageFamilyName[..lastUnderscore] + "_";
        var publisherSuffix = "_" + packageFamilyName[(lastUnderscore + 1)..];
        return FindBestMatchingRegistryPackageRoot(identityPrefix, publisherSuffix);
    }

    private static string? TryResolvePackageRootFromRegistryByAlias(string aliasPath, string? templateKey)
    {
        var parentFolderName = Path.GetFileName(Path.GetDirectoryName(aliasPath) ?? string.Empty);
        var exeName = Path.GetFileName(aliasPath);

        if (!parentFolderName.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            var byFamily = TryResolvePackageRootFromRegistryByFamily(parentFolderName);
            if (byFamily is not null)
            {
                return byFamily;
            }
        }

        string? prefixHint = null;
        if (string.Equals(templateKey, BuiltInAppTemplates.ClaudeDesktopKey, StringComparison.OrdinalIgnoreCase)
            || exeName.Equals("claude-desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            prefixHint = "Claude_";
        }
        else if (string.Equals(templateKey, BuiltInAppTemplates.CodexDesktopKey, StringComparison.OrdinalIgnoreCase)
                 || exeName.Equals("chatgpt-desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            prefixHint = "OpenAI.Codex_";
        }

        return string.IsNullOrEmpty(prefixHint)
            ? null
            : FindBestMatchingRegistryPackageRoot(prefixHint, publisherSuffix: null);
    }

    private static string? FindBestMatchingRegistryPackageRoot(string identityPrefix, string? publisherSuffix)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(PackagesRegistrySubKey, writable: false);
            if (baseKey is null)
            {
                return null;
            }

            string? bestRoot = null;
            Version? bestVersion = null;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith(identityPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Проверяем, что сразу после '<IdentityName>_' идёт номер версии (цифра),
                // чтобы 'OpenAI.Codex_' не совпадал с 'OpenAI.CodexPrimaryRuntime...'
                if (subKeyName.Length <= identityPrefix.Length || !char.IsAsciiDigit(subKeyName[identityPrefix.Length]))
                {
                    continue;
                }

                if (publisherSuffix is not null && !subKeyName.EndsWith(publisherSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var pkgKey = baseKey.OpenSubKey(subKeyName, writable: false);
                var rootFolder = pkgKey?.GetValue("PackageRootFolder") as string;
                if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
                {
                    continue;
                }

                var afterPrefix = subKeyName[identityPrefix.Length..];
                var verEnd = afterPrefix.IndexOf('_');
                var verStr = verEnd > 0 ? afterPrefix[..verEnd] : afterPrefix;
                if (Version.TryParse(verStr, out var parsedVer))
                {
                    if (bestVersion is null || parsedVer > bestVersion)
                    {
                        bestVersion = parsedVer;
                        bestRoot = rootFolder;
                    }
                }
                else if (bestRoot is null)
                {
                    bestRoot = rootFolder;
                }
            }

            return bestRoot;
        }
        catch
        {
            return null;
        }
    }

    private sealed record LogoCandidate(
        string FilePath,
        int KindPriority,
        int EstimatedPixels,
        MsixLogoAltForm AltForm,
        bool IsHighContrast);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
