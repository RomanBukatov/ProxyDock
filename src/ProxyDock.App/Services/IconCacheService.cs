using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.Services;

/// <summary>
/// Декодирует и кеширует нативные иконки Win32/MSIX-приложений с учётом текущего DPI экрана и темы оформления.
/// </summary>
public sealed class IconCacheService
{
    private readonly IAppIconExtractor _extractor;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly ConcurrentDictionary<string, WriteableBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _lastKnownLightTheme;

    public IconCacheService(IAppIconExtractor extractor, IExecutableDiscoveryService discovery)
    {
        _extractor = extractor;
        _discovery = discovery;

        if (Application.Current is { } app)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _lastKnownLightTheme = app.ActualThemeVariant == ThemeVariant.Light;
            }

            app.ActualThemeVariantChanged += (_, _) =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    _lastKnownLightTheme = app.ActualThemeVariant == ThemeVariant.Light;
                }
            };
        }
    }

    public Bitmap? GetIcon(
        string? executablePath,
        string? templateKey = null,
        double dipSize = 48.0,
        bool? preferLightThemeOverride = null,
        double? dpiScaleOverride = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var normalizedPath = Environment.ExpandEnvironmentVariables(executablePath.Trim().Trim('"'));
        var isLight = preferLightThemeOverride ?? IsLightThemeActive();
        var dpiScale = dpiScaleOverride ?? GetCurrentDpiScale();

        // Запрашиваем 2x суперсэмплинг относительно физического размера (в пределах 96..256 px) для идеально чётких контуров
        var targetPixels = Math.Clamp((int)Math.Ceiling(dipSize * dpiScale * 2.0), 96, 256);
        var themeTag = isLight ? "light" : "dark";
        var cacheKey = $"{normalizedPath}|{templateKey}|{targetPixels}|{themeTag}";

        return _cache.GetOrAdd(cacheKey, _ => LoadIcon(normalizedPath, templateKey, targetPixels, isLight));
    }

    public void Clear() => _cache.Clear();

    private WriteableBitmap? LoadIcon(string executablePath, string? templateKey, int targetPixels, bool preferLightTheme)
    {
        try
        {
            // Сначала передаём исходный путь (например, MSIX App Execution Alias), чтобы резолвер пакета
            // сохранил точный AUMID / ApplicationId из reparse-точки и прочитал AppxManifest.xml.
            var extracted = _extractor.ExtractIconPixels(executablePath, targetPixels, preferLightTheme);

            if (extracted is null)
            {
                var stack = _discovery.InspectExecutable(executablePath, templateKey);
                if (!string.IsNullOrWhiteSpace(stack.IconSourcePath)
                    && !string.Equals(stack.IconSourcePath, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    extracted = _extractor.ExtractIconPixels(stack.IconSourcePath, targetPixels, preferLightTheme);
                }
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

    private bool IsLightThemeActive()
    {
        if (Dispatcher.UIThread.CheckAccess() && Application.Current is { } app)
        {
            var current = app.ActualThemeVariant == ThemeVariant.Light;
            _lastKnownLightTheme = current;
            return current;
        }

        return _lastKnownLightTheme;
    }

    private static double GetCurrentDpiScale()
    {
        if (Dispatcher.UIThread.CheckAccess()
            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.RenderScaling: > 0 } desktop
            && desktop.MainWindow is not null)
        {
            return desktop.MainWindow.RenderScaling;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var dpi = GetDpiForSystem();
                if (dpi >= 96)
                {
                    return dpi / 96.0;
                }
            }
            catch
            {
                // Игнорируем при недоступности API
            }
        }

        return 1.25;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
