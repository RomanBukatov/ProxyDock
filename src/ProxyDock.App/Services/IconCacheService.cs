using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.Services;

/// <summary>
/// Декодирует и кеширует нативные иконки desktop-приложений в WriteableBitmap.
/// </summary>
public sealed class IconCacheService
{
    private readonly IAppIconExtractor _extractor;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly ConcurrentDictionary<string, WriteableBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IconCacheService(IAppIconExtractor extractor, IExecutableDiscoveryService discovery)
    {
        _extractor = extractor;
        _discovery = discovery;
    }

    public Bitmap? GetIcon(string? executablePath, string? templateKey = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var cacheKey = $"{executablePath}|{templateKey}";
        return _cache.GetOrAdd(cacheKey, _ => LoadIcon(executablePath, templateKey));
    }

    private WriteableBitmap? LoadIcon(string executablePath, string? templateKey)
    {
        try
        {
            var stack = _discovery.InspectExecutable(executablePath, templateKey);
            var sourcePath = !string.IsNullOrWhiteSpace(stack.IconSourcePath)
                ? stack.IconSourcePath
                : executablePath;

            var extracted = _extractor.ExtractIconPixels(sourcePath);
            if (extracted is null && !string.Equals(sourcePath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                extracted = _extractor.ExtractIconPixels(executablePath);
            }

            if (extracted is null)
            {
                return null;
            }

            var (width, height, pixels) = extracted.Value;
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using var fb = bitmap.Lock();
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * rowBytes, fb.Address + (y * fb.RowBytes), rowBytes);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
