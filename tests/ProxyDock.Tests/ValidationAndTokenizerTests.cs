using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Core.Validation;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ValidationAndTokenizerTests
{
    [Theory]
    [InlineData("", "127.0.0.1", 8080, false)]
    [InlineData("Proxy A", "http://127.0.0.1", 8080, false)]
    [InlineData("Proxy A", "127.0.0.1", 0, false)]
    [InlineData("Proxy A", "127.0.0.1", 70000, false)]
    [InlineData("Proxy A", "127.0.0.1", 8080, true)]
    [InlineData("Proxy v6", "::1", 3128, true)]
    public void ProxyProfileValidator_ValidatesCorrectly(string name, string host, int port, bool expected)
    {
        var proxy = new ProxyProfile { Name = name, Host = host, Port = port };
        var result = ProxyProfileValidator.Validate(proxy);
        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void ProxyProfileValidator_RequiresUsername_WhenPasswordProvided()
    {
        var proxy = new ProxyProfile { Name = "Auth", Host = "10.0.0.1", Port = 8080 };
        var result = ProxyProfileValidator.Validate(proxy, newPassword: "secret");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ProxyProfile.Username));
    }

    [Fact]
    public void ApplicationProfileValidator_RejectsUnclosedQuotesAndDuplicateEnvKeys()
    {
        var app = new ApplicationProfile
        {
            Name = "Test App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\App\test.exe",
            Arguments = "--foo \"unclosed",
            CustomEnvironmentVariables =
            [
                new EnvironmentVariableEntry { Key = "VAR_A", Value = "1" },
                new EnvironmentVariableEntry { Key = "var_a", Value = "2" }
            ]
        };

        var result = ApplicationProfileValidator.Validate(app);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void CommandLineTokenizer_HandlesQuotesAndEscapes()
    {
        var ok = CommandLineTokenizer.TryTokenize("--dir \"C:\\Program Files\\My App\" --msg \"hello \\\"world\\\"\"", out var args, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(["--dir", @"C:\Program Files\My App", "--msg", "hello \"world\""], args);
    }
}
