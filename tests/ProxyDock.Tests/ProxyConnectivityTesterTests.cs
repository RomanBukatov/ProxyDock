using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProxyConnectivityTesterTests
{
    [Fact]
    public async Task TestAsync_ReturnsUnreachable_WhenPortClosed()
    {
        using var scope = new TempTestScope();
        var tester = scope.Services.GetRequiredService<IProxyConnectivityTester>();

        var unusedPort = GetFreeLoopbackPort();
        var result = await tester.TestAsync(new ProxyProfile
        {
            Name = "Dead Proxy",
            Host = "127.0.0.1",
            Port = unusedPort,
            CheckTimeoutSeconds = 2,
            DiagnosticEndpointUrl = $"http://127.0.0.1:{unusedPort}/ip"
        });

        Assert.Equal(ProxyCheckStatus.Unreachable, result.Status);
        Assert.False(result.TcpSucceeded);
        Assert.False(result.HttpsSucceeded);
    }

    [Fact]
    public async Task TestAsync_DistinguishesAuthRequired407_AndReachableWithExternalIp()
    {
        using var scope = new TempTestScope();
        var tester = scope.Services.GetRequiredService<IProxyConnectivityTester>();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var httpRequestIndex = 0;
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
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, serverCts.Token);
                    if (read == 0)
                    {
                        // Шаг 1 в ProxyConnectivityTester (чистый TCP-connect без HTTP-данных) — просто закрываем
                        continue;
                    }

                    var reqText = Encoding.ASCII.GetString(buffer, 0, read);
                    var currentReq = Interlocked.Increment(ref httpRequestIndex);

                    string response;
                    if (currentReq == 1)
                    {
                        response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"ProxyDockTest\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }
                    else
                    {
                        Assert.Contains("http://example.test/ip", reqText, StringComparison.OrdinalIgnoreCase);
                        const string body = "{\"ip\":\"198.51.100.77\"}";
                        response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    }

                    var respBytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(respBytes, serverCts.Token);
                }
            }
        }, serverCts.Token);

        try
        {
            var proxy = new ProxyProfile
            {
                Name = "Mock HTTP Proxy",
                Host = "127.0.0.1",
                Port = port,
                CheckTimeoutSeconds = 5,
                DiagnosticEndpointUrl = "http://example.test/ip"
            };

            var authResult = await tester.TestAsync(proxy, serverCts.Token);
            Assert.Equal(ProxyCheckStatus.AuthRequired, authResult.Status);
            Assert.True(authResult.TcpSucceeded);
            Assert.False(authResult.HttpsSucceeded);
            Assert.Equal(407, authResult.HttpStatusCode);

            var okResult = await tester.TestAsync(proxy, serverCts.Token);
            Assert.Equal(ProxyCheckStatus.Reachable, okResult.Status);
            Assert.True(okResult.TcpSucceeded);
            Assert.True(okResult.HttpsSucceeded);
            Assert.Equal("198.51.100.77", okResult.ExternalIp);
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task TestAsync_ReturnsTcpOnly_WhenProxyAcceptsTcpAndClosesWithoutHttp()
    {
        using var scope = new TempTestScope();
        var tester = scope.Services.GetRequiredService<IProxyConnectivityTester>();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

                // Немедленно закрываем оба подключения (и TCP-пробу, и попытку HTTP/CONNECT)
                client.Close();
            }
        }, serverCts.Token);

        try
        {
            var result = await tester.TestAsync(new ProxyProfile
            {
                Name = "TCP Drop Proxy",
                Host = "127.0.0.1",
                Port = port,
                CheckTimeoutSeconds = 5,
                DiagnosticEndpointUrl = "https://example.test/ip"
            }, serverCts.Token);

            Assert.Equal(ProxyCheckStatus.TcpOnly, result.Status);
            Assert.True(result.TcpSucceeded);
            Assert.False(result.HttpsSucceeded);
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task TestAsync_CancelsCleanly_WhenCallerCancels()
    {
        using var scope = new TempTestScope();
        var tester = scope.Services.GetRequiredService<IProxyConnectivityTester>();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heldClients = new List<TcpClient>();
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(serverCts.Token);
                    heldClients.Add(client);
                }
                catch (Exception) when (serverCts.IsCancellationRequested)
                {
                    break;
                }
            }
        }, serverCts.Token);

        try
        {
            using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.TestAsync(new ProxyProfile
            {
                Name = "Silent Proxy",
                Host = "127.0.0.1",
                Port = port,
                CheckTimeoutSeconds = 10,
                DiagnosticEndpointUrl = "http://example.test/ip"
            }, callerCts.Token));
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            foreach (var c in heldClients)
            {
                c.Dispose();
            }
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
