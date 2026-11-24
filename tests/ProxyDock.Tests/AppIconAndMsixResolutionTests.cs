using System.Buffers.Binary;
using System.IO.Compression;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App;
using ProxyDock.App.Services;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Platform;
using Xunit;
using Xunit.Abstractions;

namespace ProxyDock.Tests;

public sealed class AppIconAndMsixResolutionTests(ITestOutputHelper output)
{
    private static readonly Lazy<bool> AvaloniaReady = new(() =>
    {
        if (Application.Current is null)
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
        }

        return true;
    });

    [Fact]
    public void MsixManifestExtractor_SelectsUnplatedAndLightUnplatedAssetsForDpiAndTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var scope = new TempTestScope();
        var pkgDir = Path.Combine(scope.RootPath, "SyntheticMsixPkg");
        var appDir = Path.Combine(pkgDir, "app");
        var assetsDir = Path.Combine(pkgDir, "Assets");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(assetsDir);

        var dummyExe = Path.Combine(appDir, "SampleApp.exe");
        File.WriteAllBytes(dummyExe, [0x4D, 0x5A]);

        File.WriteAllText(
            Path.Combine(pkgDir, "AppxManifest.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Identity Name="Test.SyntheticApp" Publisher="CN=Test" Version="1.0.0.0" />
              <Properties>
                <DisplayName>SyntheticApp</DisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>
              <Applications>
                <Application Id="App" Executable="app\SampleApp.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="SyntheticApp"
                                      Description="Test"
                                      Square44x44Logo="Assets\Square44x44Logo.png"
                                      Square150x150Logo="Assets\Square150x150Logo.png"
                                      BackgroundColor="#3143FF" />
                </Application>
              </Applications>
            </Package>
            """);

        // Создаём валидные PNG разного размера и цвета для точной проверки выбора варианта:
        // scale-100 (44x44, синий), targetsize-128 unplated (128x128, зелёный), targetsize-128 lightunplated (128x128, оранжевый)
        WriteSolidPng(Path.Combine(assetsDir, "Square44x44Logo.png"), 44, 44, r: 20, g: 60, b: 220, a: 255);
        WriteSolidPng(Path.Combine(assetsDir, "Square44x44Logo.targetsize-128_altform-unplated.png"), 128, 128, r: 40, g: 210, b: 95, a: 255);
        WriteSolidPng(Path.Combine(assetsDir, "Square44x44Logo.targetsize-128_altform-lightunplated.png"), 128, 128, r: 230, g: 110, b: 45, a: 255);
        WriteSolidPng(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png"), 300, 300, r: 180, g: 40, b: 180, a: 255);

        var extractor = new WindowsAppIconExtractor();

        var darkIcon = extractor.ExtractIconPixels(dummyExe, desiredPixelSize: 96, preferLightTheme: false);
        Assert.NotNull(darkIcon);
        Assert.Equal(128, darkIcon.Value.Width);
        Assert.Equal(128, darkIcon.Value.Height);
        // BGRA в центре: зелёный unplated (B=95, G=210, R=40)
        Assert.Equal(95, darkIcon.Value.BgraPixels[0]);
        Assert.Equal(210, darkIcon.Value.BgraPixels[1]);
        Assert.Equal(40, darkIcon.Value.BgraPixels[2]);

        var lightIcon = extractor.ExtractIconPixels(dummyExe, desiredPixelSize: 96, preferLightTheme: true);
        Assert.NotNull(lightIcon);
        Assert.Equal(128, lightIcon.Value.Width);
        Assert.Equal(128, lightIcon.Value.Height);
        // BGRA в центре: оранжевый lightunplated (B=45, G=110, R=230)
        Assert.Equal(45, lightIcon.Value.BgraPixels[0]);
        Assert.Equal(110, lightIcon.Value.BgraPixels[1]);
        Assert.Equal(230, lightIcon.Value.BgraPixels[2]);
    }

    [Fact]
    public void InstalledDesktopTemplates_ExtractRealHighResIconsInDarkAndLightThemes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = AvaloniaReady.Value;

        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
        var extractor = scope.Services.GetRequiredService<IAppIconExtractor>();
        var cache = new IconCacheService(extractor, discovery);

        foreach (var tpl in BuiltInAppTemplates.DesktopTemplates)
        {
            var resolved = discovery.DiscoverTemplate(tpl.Key);
            if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.ResolvedPath))
            {
                output.WriteLine($"[{tpl.Key}] not installed on this machine, skipping live assertion.");
                continue;
            }

            var darkPixels = extractor.ExtractIconPixels(resolved.ResolvedPath, desiredPixelSize: 128, preferLightTheme: false);
            var lightPixels = extractor.ExtractIconPixels(resolved.ResolvedPath, desiredPixelSize: 128, preferLightTheme: true);

            Assert.NotNull(darkPixels);
            Assert.NotNull(lightPixels);

            // Реальные иконки пакетов/приложений должны быть высокого разрешения (не системная 32x32 заглушка!)
            Assert.True(darkPixels.Value.Width >= 64, $"Expected high-DPI icon >=64px for {tpl.Key}, got {darkPixels.Value.Width}x{darkPixels.Value.Height}");
            Assert.True(lightPixels.Value.Width >= 64, $"Expected high-DPI light icon >=64px for {tpl.Key}, got {lightPixels.Value.Width}x{lightPixels.Value.Height}");

            var cachedDark = cache.GetIcon(resolved.ResolvedPath, tpl.Key, dipSize: 48, preferLightThemeOverride: false, dpiScaleOverride: 1.5);
            var cachedLight = cache.GetIcon(resolved.ResolvedPath, tpl.Key, dipSize: 48, preferLightThemeOverride: true, dpiScaleOverride: 1.5);
            Assert.NotNull(cachedDark);
            Assert.NotNull(cachedLight);
            Assert.Same(cachedDark, cache.GetIcon(resolved.ResolvedPath, tpl.Key, dipSize: 48, preferLightThemeOverride: false, dpiScaleOverride: 1.5));

            output.WriteLine($"[{tpl.Key}] OK path={resolved.ResolvedPath} dark={darkPixels.Value.Width}x{darkPixels.Value.Height} light={lightPixels.Value.Width}x{lightPixels.Value.Height}");
        }
    }

    private static void WriteSolidPng(string path, int width, int height, byte r, byte g, byte b, byte a)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR"u8, ihdr);

        var rawScanlines = new byte[height * (1 + (width * 4))];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * (1 + (width * 4));
            rawScanlines[rowOffset] = 0; // filter None
            for (var x = 0; x < width; x++)
            {
                var px = rowOffset + 1 + (x * 4);
                rawScanlines[px] = r;
                rawScanlines[px + 1] = g;
                rawScanlines[px + 2] = b;
                rawScanlines[px + 3] = a;
            }
        }

        using var idatCompressed = new MemoryStream();
        using (var zlib = new ZLibStream(idatCompressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rawScanlines);
        }

        WriteChunk(ms, "IDAT"u8, idatCompressed.ToArray());
        WriteChunk(ms, "IEND"u8, []);

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);

        var crc = ComputeCrc32(type, data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, a);
        crc = UpdateCrc(crc, b);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var t in bytes)
        {
            crc ^= t;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }
}
