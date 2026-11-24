using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.Infrastructure.Platform;

/// <summary>
/// Извлекает оригинальные иконки Windows-приложений:
/// 1) Для MSIX-пакетов — находит пакет, анализирует AppxManifest.xml и декодирует оригинальные
///    Square44x44Logo / Square150x150Logo ресурсы с учётом scale, targetsize, unplated/lightunplated и DPI.
/// 2) Для Win32-приложений — извлекает настоящую высокоразрешающую иконку из PE-ресурсов .exe (PrivateExtractIconsW).
/// 3) Системная стандартная иконка (SHGetFileInfo) используется исключительно как самый последний fallback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppIconExtractor : IAppIconExtractor
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint ImageLockModeRead = 0x0001;
    private const int PixelFormat32bppArgb = 0x0026200A;

    private static readonly Lazy<IntPtr> GdiPlusToken = new(InitializeGdiPlus, LazyThreadSafetyMode.ExecutionAndPublication);

    public (int Width, int Height, byte[] BgraPixels)? ExtractIconPixels(
        string filePath,
        int desiredPixelSize = 96,
        bool preferLightTheme = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = Environment.ExpandEnvironmentVariables(filePath.Trim().Trim('"'));
        var targetSize = Math.Clamp(desiredPixelSize, 32, 256);

        // 1. MSIX-приложения: определяем пакет и разбираем AppxManifest.xml (не ограничиваясь App Execution Alias)
        var msixIcon = TryExtractMsixIcon(normalized, targetSize, preferLightTheme);
        if (msixIcon is not null)
        {
            return msixIcon;
        }

        if (!File.Exists(normalized))
        {
            return null;
        }

        // 2. Прямые графические файлы (.png, .ico, .bmp)
        var ext = Path.GetExtension(normalized);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeImageViaGdiPlus(normalized);
        }

        if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return TryExtractViaPrivateExtractIcons(normalized, targetSize)
                   ?? TryDecodeImageViaGdiPlus(normalized);
        }

        // 3. Обычные Win32-приложения: извлекаем настоящую встроенную иконку из исполняемого файла (.exe/.dll)
        if (!MsixPackageResolver.IsWindowsAppsAliasPath(normalized)
            && MsixPackageResolver.TryReadAppExecLink(normalized) is null)
        {
            var embeddedIcon = TryExtractEmbeddedWin32Icon(normalized, targetSize);
            if (embeddedIcon is not null)
            {
                return embeddedIcon;
            }
        }

        // 4. Последний fallback — стандартная иконка оболочки Windows
        return TryExtractFallbackShellIcon(normalized);
    }

    private static (int Width, int Height, byte[] BgraPixels)? TryExtractMsixIcon(
        string filePath,
        int targetSize,
        bool preferLightTheme)
    {
        var package = MsixPackageResolver.TryResolvePackage(filePath);
        if (package is null)
        {
            return null;
        }

        var manifest = MsixPackageResolver.ParseManifestVisuals(package);
        if (manifest is not null)
        {
            var resolvedAsset = MsixPackageResolver.ResolveBestLogoAsset(manifest, targetSize, preferLightTheme);
            if (resolvedAsset is not null)
            {
                var decoded = TryDecodeImageViaGdiPlus(resolvedAsset.FilePath);
                if (decoded is not null)
                {
                    return ApplyPlateIfRequired(
                        decoded.Value,
                        resolvedAsset.AltForm,
                        resolvedAsset.BackgroundColor,
                        preferLightTheme);
                }
            }

            // Fallback внутри пакета: настоящая иконка из основного исполняемого файла <Application Executable="...">
            if (!string.IsNullOrWhiteSpace(manifest.ApplicationExecutablePath)
                && File.Exists(manifest.ApplicationExecutablePath))
            {
                var appExeIcon = TryExtractEmbeddedWin32Icon(manifest.ApplicationExecutablePath, targetSize);
                if (appExeIcon is not null)
                {
                    return appExeIcon;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(package.TargetExecutablePath)
            && File.Exists(package.TargetExecutablePath))
        {
            var targetExeIcon = TryExtractEmbeddedWin32Icon(package.TargetExecutablePath, targetSize);
            if (targetExeIcon is not null)
            {
                return targetExeIcon;
            }
        }

        return null;
    }

    private static (int Width, int Height, byte[] BgraPixels)? TryExtractEmbeddedWin32Icon(string exePath, int targetSize)
    {
        if (!File.Exists(exePath))
        {
            return null;
        }

        // Проверяем, что в PE-ресурсах действительно вшита хотя бы одна группа иконок,
        // чтобы не подменять отсутствие иконки пустой системной заглушкой раньше времени.
        var embeddedGroups = ExtractIconExW(exePath, -1, null, null, 0);
        if (embeddedGroups == 0 || embeddedGroups == uint.MaxValue)
        {
            return null;
        }

        return TryExtractViaPrivateExtractIcons(exePath, targetSize)
               ?? (targetSize != 256 ? TryExtractViaPrivateExtractIcons(exePath, 256) : null)
               ?? TryExtractViaPrivateExtractIcons(exePath, 48);
    }

    private static (int Width, int Height, byte[] BgraPixels)? TryExtractViaPrivateExtractIcons(string filePath, int targetSize)
    {
        var icons = new IntPtr[1];
        var iconIds = new uint[1];
        var extracted = PrivateExtractIconsW(filePath, 0, targetSize, targetSize, icons, iconIds, 1, 0);
        if (extracted == 0 || extracted == uint.MaxValue || icons[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ConvertHIconToBgra(icons[0]);
        }
        finally
        {
            _ = DestroyIcon(icons[0]);
        }
    }

    private static (int Width, int Height, byte[] BgraPixels)? TryExtractFallbackShellIcon(string filePath)
    {
        var shfi = new ShFileInfo();
        var res = SHGetFileInfo(filePath, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ConvertHIconToBgra(shfi.hIcon);
        }
        finally
        {
            _ = DestroyIcon(shfi.hIcon);
        }
    }

    private static (int Width, int Height, byte[] BgraPixels)? ConvertHIconToBgra(IntPtr hIcon)
    {
        var iconInfo = new IconInfo();
        var gotIconInfo = false;
        var hdc = IntPtr.Zero;

        try
        {
            gotIconInfo = GetIconInfo(hIcon, out iconInfo);
            if (!gotIconInfo)
            {
                return null;
            }

            var bmpHandle = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
            if (bmpHandle == IntPtr.Zero)
            {
                return null;
            }

            if (GetObjectBitmap(bmpHandle, Marshal.SizeOf<BitmapStruct>(), out var bmp) == 0
                || bmp.bmWidth <= 0
                || bmp.bmHeight <= 0)
            {
                return null;
            }

            var width = bmp.bmWidth;
            var height = iconInfo.hbmColor != IntPtr.Zero ? bmp.bmHeight : bmp.bmHeight / 2;
            if (height <= 0)
            {
                return null;
            }

            hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return null;
            }

            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // top-down BGRA
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb
            };

            var pixels = new byte[width * height * 4];
            var lines = GetDIBits(hdc, bmpHandle, 0, (uint)height, pixels, ref bmi, DibRgbColors);
            if (lines == 0)
            {
                return null;
            }

            var hasNonZeroAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasNonZeroAlpha = true;
                    break;
                }
            }

            if (!hasNonZeroAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            return (width, height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hdc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }

            if (gotIconInfo)
            {
                if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.hbmColor);
                }

                if (iconInfo.hbmMask != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.hbmMask);
                }
            }
        }
    }

    private static (int Width, int Height, byte[] BgraPixels)? TryDecodeImageViaGdiPlus(string filePath)
    {
        if (!File.Exists(filePath) || GdiPlusToken.Value == IntPtr.Zero)
        {
            return null;
        }

        var gpBitmap = IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromFile(filePath, out gpBitmap) != 0 || gpBitmap == IntPtr.Zero)
            {
                return null;
            }

            if (GdipGetImageWidth(gpBitmap, out var uWidth) != 0
                || GdipGetImageHeight(gpBitmap, out var uHeight) != 0
                || uWidth == 0
                || uHeight == 0
                || uWidth > 2048
                || uHeight > 2048)
            {
                return null;
            }

            var width = (int)uWidth;
            var height = (int)uHeight;
            var rect = new GpRect { X = 0, Y = 0, Width = width, Height = height };
            if (GdipBitmapLockBits(gpBitmap, ref rect, ImageLockModeRead, PixelFormat32bppArgb, out var bmpData) != 0)
            {
                return null;
            }

            try
            {
                var pixels = new byte[width * height * 4];
                var rowBytes = width * 4;
                for (var y = 0; y < height; y++)
                {
                    var srcRow = bmpData.Scan0 + (y * bmpData.Stride);
                    Marshal.Copy(srcRow, pixels, y * rowBytes, rowBytes);
                }

                return (width, height, pixels);
            }
            finally
            {
                _ = GdipBitmapUnlockBits(gpBitmap, ref bmpData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (gpBitmap != IntPtr.Zero)
            {
                _ = GdipDisposeImage(gpBitmap);
            }
        }
    }

    private static (int Width, int Height, byte[] BgraPixels) ApplyPlateIfRequired(
        (int Width, int Height, byte[] BgraPixels) source,
        MsixLogoAltForm altForm,
        string? manifestBackgroundColor,
        bool preferLightTheme)
    {
        var hasPlateColor = TryParseManifestColor(manifestBackgroundColor, out var bgR, out var bgG, out var bgB);

        // Для plated-ресурсов (без altform-unplated/lightunplated), если манифест задаёт непрозрачный BackgroundColor
        // и сама картинка имеет прозрачный фон — накладываем на фирменную подложку пакета (как в меню Пуск Windows).
        if (altForm == MsixLogoAltForm.Plated && hasPlateColor && HasTransparentBorder(source))
        {
            return CompositeOverRoundedPlate(source, bgR, bgG, bgB, insetRatio: 0.12f);
        }

        // Если в светлой теме пакет не предоставил lightunplated, а глиф чисто белый на прозрачном фоне —
        // подкладываем фирменный цвет манифеста или аккуратную тёмную плашку, чтобы иконка не слилась с карточкой.
        if (preferLightTheme && altForm != MsixLogoAltForm.LightUnplated && IsMonochromeExtreme(source, nearWhite: true))
        {
            return hasPlateColor
                ? CompositeOverRoundedPlate(source, bgR, bgG, bgB, insetRatio: 0.14f)
                : CompositeOverRoundedPlate(source, 28, 35, 49, insetRatio: 0.14f);
        }

        // Аналогично для чисто чёрного глифа в тёмной теме
        if (!preferLightTheme && IsMonochromeExtreme(source, nearWhite: false))
        {
            return CompositeOverRoundedPlate(source, 238, 242, 248, insetRatio: 0.14f);
        }

        return source;
    }

    private static bool HasTransparentBorder((int Width, int Height, byte[] BgraPixels) img)
    {
        if (img.Width < 4 || img.Height < 4)
        {
            return false;
        }

        var p = img.BgraPixels;
        var topLeftA = p[3];
        var topRightA = p[((img.Width - 1) * 4) + 3];
        var bottomLeftA = p[(((img.Height - 1) * img.Width) * 4) + 3];
        return topLeftA < 16 && topRightA < 16 && bottomLeftA < 16;
    }

    private static bool IsMonochromeExtreme((int Width, int Height, byte[] BgraPixels) img, bool nearWhite)
    {
        if (!HasTransparentBorder(img))
        {
            return false;
        }

        var visibleCount = 0;
        var extremeCount = 0;
        var p = img.BgraPixels;

        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a < 40)
            {
                continue;
            }

            visibleCount++;
            var b = p[i];
            var g = p[i + 1];
            var r = p[i + 2];

            if (nearWhite)
            {
                if (r >= 225 && g >= 225 && b >= 225)
                {
                    extremeCount++;
                }
            }
            else if (r <= 35 && g <= 35 && b <= 35)
            {
                extremeCount++;
            }
        }

        return visibleCount > 16 && (double)extremeCount / visibleCount >= 0.92;
    }

    private static (int Width, int Height, byte[] BgraPixels) CompositeOverRoundedPlate(
        (int Width, int Height, byte[] BgraPixels) fg,
        byte plateR,
        byte plateG,
        byte plateB,
        float insetRatio)
    {
        var w = fg.Width;
        var h = fg.Height;
        var dst = new byte[w * h * 4];
        var radius = MathF.Max(4f, MathF.Min(w, h) * 0.22f);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var plateAlpha = RoundedRectCoverage(x + 0.5f, y + 0.5f, w, h, radius);
                var idx = ((y * w) + x) * 4;
                dst[idx] = plateB;
                dst[idx + 1] = plateG;
                dst[idx + 2] = plateR;
                dst[idx + 3] = (byte)Math.Clamp((int)MathF.Round(plateAlpha * 255f), 0, 255);
            }
        }

        var padX = (int)MathF.Round(w * insetRatio);
        var padY = (int)MathF.Round(h * insetRatio);
        var drawW = Math.Max(1, w - (padX * 2));
        var drawH = Math.Max(1, h - (padY * 2));

        for (var dy = 0; dy < drawH; dy++)
        {
            var sy = ((dy + 0.5f) * h / drawH) - 0.5f;
            for (var dx = 0; dx < drawW; dx++)
            {
                var sx = ((dx + 0.5f) * w / drawW) - 0.5f;
                var (fgB, fgG, fgR, fgA) = SampleBilinear(fg.BgraPixels, w, h, sx, sy);
                if (fgA <= 0.001f)
                {
                    continue;
                }

                var tx = padX + dx;
                var ty = padY + dy;
                var dIdx = ((ty * w) + tx) * 4;

                var bgA = dst[dIdx + 3] / 255f;
                var outA = fgA + (bgA * (1f - fgA));
                if (outA <= 0.001f)
                {
                    continue;
                }

                var bgB = dst[dIdx] / 255f;
                var bgG = dst[dIdx + 1] / 255f;
                var bgR = dst[dIdx + 2] / 255f;

                var outB = ((fgB * fgA) + (bgB * bgA * (1f - fgA))) / outA;
                var outG = ((fgG * fgA) + (bgG * bgA * (1f - fgA))) / outA;
                var outR = ((fgR * fgA) + (bgR * bgA * (1f - fgA))) / outA;

                dst[dIdx] = (byte)Math.Clamp((int)MathF.Round(outB * 255f), 0, 255);
                dst[dIdx + 1] = (byte)Math.Clamp((int)MathF.Round(outG * 255f), 0, 255);
                dst[dIdx + 2] = (byte)Math.Clamp((int)MathF.Round(outR * 255f), 0, 255);
                dst[dIdx + 3] = (byte)Math.Clamp((int)MathF.Round(outA * 255f), 0, 255);
            }
        }

        return (w, h, dst);
    }

    private static (float B, float G, float R, float A) SampleBilinear(byte[] px, int w, int h, float x, float y)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, w - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, h - 1);
        var x1 = Math.Clamp(x0 + 1, 0, w - 1);
        var y1 = Math.Clamp(y0 + 1, 0, h - 1);
        var fx = Math.Clamp(x - MathF.Floor(x), 0f, 1f);
        var fy = Math.Clamp(y - MathF.Floor(y), 0f, 1f);

        static (float B, float G, float R, float A) GetPx(byte[] p, int width, int ix, int iy)
        {
            var i = ((iy * width) + ix) * 4;
            var a = p[i + 3] / 255f;
            return ((p[i] / 255f) * a, (p[i + 1] / 255f) * a, (p[i + 2] / 255f) * a, a);
        }

        var c00 = GetPx(px, w, x0, y0);
        var c10 = GetPx(px, w, x1, y0);
        var c01 = GetPx(px, w, x0, y1);
        var c11 = GetPx(px, w, x1, y1);

        var w00 = (1f - fx) * (1f - fy);
        var w10 = fx * (1f - fy);
        var w01 = (1f - fx) * fy;
        var w11 = fx * fy;

        var pa = (c00.A * w00) + (c10.A * w10) + (c01.A * w01) + (c11.A * w11);
        if (pa <= 0.0001f)
        {
            return (0f, 0f, 0f, 0f);
        }

        var pb = ((c00.B * w00) + (c10.B * w10) + (c01.B * w01) + (c11.B * w11)) / pa;
        var pg = ((c00.G * w00) + (c10.G * w10) + (c01.G * w01) + (c11.G * w11)) / pa;
        var pr = ((c00.R * w00) + (c10.R * w10) + (c01.R * w01) + (c11.R * w11)) / pa;
        return (pb, pg, pr, pa);
    }

    private static float RoundedRectCoverage(float px, float py, int w, int h, float r)
    {
        var cx = Math.Clamp(px, r, w - r);
        var cy = Math.Clamp(py, r, h - r);
        var dx = px - cx;
        var dy = py - cy;
        var dist = MathF.Sqrt((dx * dx) + (dy * dy));
        return Math.Clamp(r - dist + 0.5f, 0f, 1f);
    }

    private static bool TryParseManifestColor(string? colorText, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return false;
        }

        var s = colorText.Trim();
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length == 6
                && byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }

            if (hex.Length == 8
                && byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)
                && a > 0
                && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
        }

        return false;
    }

    private static IntPtr InitializeGdiPlus()
    {
        try
        {
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            return GdiplusStartup(out var token, ref input, IntPtr.Zero) == 0 ? token : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectBitmap(IntPtr hObject, int cbBuffer, out BitmapStruct lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BitmapInfoHeader lpbmi,
        uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapLockBits(
        IntPtr bitmap,
        ref GpRect rect,
        uint flags,
        int format,
        out GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapStruct
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpBitmapData
    {
        public uint Width;
        public uint Height;
        public int Stride;
        public int PixelFormat;
        public IntPtr Scan0;
        public IntPtr Reserved;
    }
}
