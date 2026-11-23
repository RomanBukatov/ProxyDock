using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProxyEnvironmentBuilderTests
{
    [Fact]
    public void Build_SetsAllSixProxyVariablesAndNoProxy_WhenProxyActive()
    {
        var proxy = new ProxyProfile
        {
            Host = "127.0.0.1",
            Port = 7890,
            NoProxy = "localhost,127.0.0.1"
        };

        var custom = new[]
        {
            new EnvironmentVariableEntry { Key = "CUSTOM_API_URL", Value = "https://example.test", IsSecret = false },
            new EnvironmentVariableEntry { Key = "MY_API_KEY", Value = "super-secret-value", IsSecret = false }
        };

        var delta = ProxyEnvironmentBuilder.Build(proxy, null, null, custom, isDirectNoProxy: false);

        Assert.Empty(delta.Remove);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["ALL_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["http_proxy"]);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["https_proxy"]);
        Assert.Equal("http://127.0.0.1:7890", delta.Set["all_proxy"]);
        Assert.Equal("localhost,127.0.0.1", delta.Set["NO_PROXY"]);
        Assert.Equal("localhost,127.0.0.1", delta.Set["no_proxy"]);
        Assert.Equal("https://example.test", delta.Set["CUSTOM_API_URL"]);
        Assert.Contains("super-secret-value", delta.SensitiveValues);
    }

    [Fact]
    public void Build_RemovesOnlyProxyVariables_WhenDirectNoProxy()
    {
        var delta = ProxyEnvironmentBuilder.Build(
            proxy: new ProxyProfile { Host = "127.0.0.1", Port = 7890 },
            credentials: null,
            noProxyOverride: null,
            customVariables: [new EnvironmentVariableEntry { Key = "KEEP_ME", Value = "1" }],
            isDirectNoProxy: true);

        Assert.Equal(6, delta.Remove.Count);
        Assert.Contains("HTTP_PROXY", delta.Remove);
        Assert.Contains("all_proxy", delta.Remove);
        Assert.Equal("1", delta.Set["KEEP_ME"]);
    }
}
