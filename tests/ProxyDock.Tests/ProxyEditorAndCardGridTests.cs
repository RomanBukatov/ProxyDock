using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App.Controls;
using ProxyDock.App.Localization;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProxyEditorAndCardGridTests
{
    [Theory]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080, null, null)]
    [InlineData("proxy.example.com:3128", "proxy.example.com", 3128, null, null)]
    [InlineData("[::1]:8888", "::1", 8888, null, null)]
    [InlineData("http://192.168.1.5:8080", "192.168.1.5", 8080, null, null)]
    [InlineData("https://gw.corp.local:8443/", "gw.corp.local", 8443, null, null)]
    [InlineData("http://alice:s3cr3t%21@10.0.0.2:3128", "10.0.0.2", 3128, "alice", "s3cr3t!")]
    [InlineData("bob:pass123@proxy.local:9090", "proxy.local", 9090, "bob", "pass123")]
    [InlineData("198.51.100.12:10808:user1:my:pass", "198.51.100.12", 10808, "user1", "my:pass")]
    public void ProxyInputParser_ParsesSupportedFormats(
        string input,
        string expectedHost,
        int expectedPort,
        string? expectedUser,
        string? expectedPass)
    {
        var ok = ProxyInputParser.TryParse(input, out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal(expectedHost, parsed.Host);
        Assert.Equal(expectedPort, parsed.Port);
        Assert.Equal(expectedUser, parsed.Username);
        Assert.Equal(expectedPass, parsed.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("proxy.example.com")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("127.0.0.1:0")]
    public void ProxyInputParser_RejectsPlainHostOrInvalidInput(string input)
    {
        var ok = ProxyInputParser.TryParse(input, out var parsed);
        Assert.False(ok);
        Assert.Null(parsed);
    }

    [Fact]
    public async Task ProxyEditorViewModel_AutoFillsFromPaste_ExpandsAdvancedOnValidationError_AndTestsUnsavedCredentials()
    {
        using var scope = new TempTestScope();
        var tester = scope.Services.GetRequiredService<IProxyConnectivityTester>();
        var loc = new LocalizationService();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sawBasicAuthHeader = false;
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(serverCts.Token);
                }
                catch (Exception) when (serverCts.IsCancellationRequested)
                {
                    break;
                }

                using (client)
                using (var stream = client.GetStream())
                {
                    var buf = new byte[4096];
                    var read = await stream.ReadAsync(buf, serverCts.Token);
                    if (read == 0)
                    {
                        continue;
                    }

                    var req = Encoding.ASCII.GetString(buf, 0, read);
                    if (req.Contains("Proxy-Authorization: Basic", StringComparison.OrdinalIgnoreCase))
                    {
                        sawBasicAuthHeader = true;
                        const string body = "{\"ip\":\"203.0.113.55\"}";
                        var okResp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(okResp), serverCts.Token);
                    }
                    else
                    {
                        const string authResp = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(authResp), serverCts.Token);
                    }
                }
            }
        }, serverCts.Token);

        try
        {
            var vm = new ProxyEditorViewModel(loc, tester, initial: null, suggestDefault: true);
            Assert.False(vm.IsAdvancedExpanded);
            Assert.True(vm.IsDefault);

            // Автозаполнение при вставке URL с учётными данными
            vm.QuickPasteInput = $"http://tester:secretPass@127.0.0.1:{port}";
            Assert.Equal("127.0.0.1", vm.Host);
            Assert.Equal(port, vm.Port);
            Assert.Equal("tester", vm.Username);
            Assert.Equal("secretPass", vm.Password);
            Assert.Equal($"127.0.0.1:{port}", vm.Name);
            Assert.True(vm.QuickPasteApplied);

            // Проверка невалидного URL диагностики автоматически раскрывает «Дополнительные настройки»
            vm.DiagnosticEndpointUrl = "not-a-valid-url";
            vm.SaveCommand.Execute(null);
            Assert.True(vm.IsAdvancedExpanded);
            Assert.NotNull(vm.ValidationError);

            // Исправляем URL диагностики и запускаем проверку прокси прямо в редакторе с ещё не сохранённым паролем
            vm.DiagnosticEndpointUrl = "http://example.test/ip";
            await vm.TestOrCancelCommand.ExecuteAsync(null);

            Assert.True(sawBasicAuthHeader);
            Assert.True(vm.HasTestResult);
            Assert.Equal(ProxyCheckStatus.Reachable, vm.TestStatus);
            Assert.Contains("203.0.113.55", vm.TestResultTitle, StringComparison.Ordinal);
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(380, 232, 14, 4, 1)]
    [InlineData(460, 232, 14, 4, 1)]
    [InlineData(520, 232, 14, 4, 2)]
    [InlineData(780, 232, 14, 4, 3)]
    [InlineData(1060, 232, 14, 4, 4)]
    [InlineData(1600, 232, 14, 4, 4)]
    public void AdaptiveCardGridPanel_CalculatesExpectedColumnCount_IncludingSingleColumnFallback(
        double width,
        double minColWidth,
        double spacing,
        int maxCols,
        int expectedCols)
    {
        var cols = AdaptiveCardGridPanel.CalculateColumnCount(width, minColWidth, spacing, maxCols);
        Assert.Equal(expectedCols, cols);
    }

    [Fact]
    public void ResolveProxySummary_ReturnsCleanProxyNameWithoutTechnicalNoise()
    {
        var loc = new LocalizationService();
        var proxy = new ProxyProfile { Id = Guid.NewGuid(), Name = "Frankfurt Fast", Host = "10.20.30.40", Port = 8080, IsDefault = true };
        var byId = new Dictionary<Guid, ProxyProfile> { [proxy.Id] = proxy };

        var inheritedApp = new ApplicationProfile { Name = "App1", ProxyMode = ProxyAssignmentMode.InheritDefault };
        var (label1, _) = ApplicationsViewModel.ResolveProxySummary(inheritedApp, byId, proxy, loc);
        Assert.Equal("Frankfurt Fast", label1);

        var directApp = new ApplicationProfile { Name = "App2", ProxyMode = ProxyAssignmentMode.DirectNoProxy };
        var (label2, _) = ApplicationsViewModel.ResolveProxySummary(directApp, byId, proxy, loc);
        Assert.Equal(loc["ProxyModeDirect"], label2);
    }
}
