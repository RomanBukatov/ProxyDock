using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.Infrastructure.Platform;

/// <summary>
/// Извлекает нативную 32-битную BGRA иконку Windows-приложения через Shell32/User32/Gdi32 без зависимости от System.Drawing.Common.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppIconExtractor : IAppIconExtractor
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    public (int Width, int Height, byte[] BgraPixels)? ExtractIconPixels(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var shfi = new ShFileInfo();
        var res = SHGetFileInfo(filePath, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
        {
            return null;
        }

        var iconInfo = new IconInfo();
        var gotIconInfo = false;
        var hdc = IntPtr.Zero;
        try
        {
            gotIconInfo = GetIconInfo(shfi.hIcon, out iconInfo);
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

            _ = DestroyIcon(shfi.hIcon);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

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
}
