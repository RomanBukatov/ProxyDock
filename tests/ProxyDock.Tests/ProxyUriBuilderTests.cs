using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ProxyUriBuilderTests
{
    [Fact]
    public void BuildDisplayUri_FormatsHostAndIpv6Correctly_WithoutCredentials()
    {
        var ipv4 = new ProxyProfile { Host = "127.0.0.1", Port = 8080, Username = "alice", HasCredentials = true };
        var ipv6 = new ProxyProfile { Host = "::1", Port = 3128 };

        Assert.Equal("http://127.0.0.1:8080", ProxyUriBuilder.BuildDisplayUri(ipv4));
        Assert.Equal("http://[::1]:3128", ProxyUriBuilder.BuildDisplayUri(ipv6));
        Assert.Equal("http://***@127.0.0.1:8080", ProxyUriBuilder.BuildMaskedUri(ipv4));
    }

    [Fact]
    public void BuildRuntimeUri_EscapesSpecialCharactersInCredentials()
    {
        var proxy = new ProxyProfile { Host = "proxy.local", Port = 8888, Username = "user@corp", HasCredentials = true };
        var creds = new ProxyCredentials("user@corp", "p@ss:w/rd#1");

        var uri = ProxyUriBuilder.BuildRuntimeUri(proxy, creds);

        Assert.Equal("http://user%40corp:p%40ss%3Aw%2Frd%231@proxy.local:8888", uri);
    }
}
