using ProxyDock.Core.Services;
using Xunit;

namespace ProxyDock.Tests;

public sealed class ScriptAndRedactorTests
{
    [Fact]
    public void PowerShellScriptBuilder_EscapesInjectionPayloadsAndNeverStoresPlaintextDpapiPassword()
    {
        var script = PowerShellScriptBuilder.Build(new CliScriptOptions(
            ApplicationName: "Codex; Remove-Item C:\\*",
            TargetCommandOrPath: @"C:\Tools\My 'Tool'\app.cmd",
            Arguments: ["hello'; Start-Process calc; #", "--flag=a'b"],
            WorkingDirectory: @"C:\Projects\O'Brien",
            NonSecretEnvironmentSet: new Dictionary<string, string> { ["NO_PROXY"] = "localhost" },
            EnvironmentRemove: [],
            MaskedProxyDisplay: "http://***@127.0.0.1:8888",
            IsDirectNoProxy: false,
            PlainProxyUri: null,
            DpapiHexProtectedProxyUri: "01000000D08C9DDF"));

        Assert.Contains("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path", script);
        Assert.Contains("Microsoft.PowerShell.Core\\Import-Module", script);
        Assert.Contains("Microsoft.PowerShell.Security", script);
        Assert.Contains("ConvertTo-SecureString '01000000D08C9DDF'", script);
        Assert.Contains("-ErrorAction Stop", script);
        Assert.Contains("'hello''; Start-Process calc; #'", script);
        Assert.Contains(@"& $__pdCmd @__pdArgs", script);
        Assert.DoesNotContain("super-secret-password", script);
    }

    [Fact]
    public void SecretRedactor_MasksCredentialsFlagsTokensAndExplicitSecrets()
    {
        var raw = "curl http://alice:topsecret@proxy.local:8080 --token=tok_12345 -H \"Authorization: Bearer abcdef123\" custom=myRawSecret";
        var redacted = SecretRedactor.Redact(raw, ["myRawSecret"]);

        Assert.DoesNotContain("topsecret", redacted);
        Assert.DoesNotContain("alice", redacted);
        Assert.DoesNotContain("tok_12345", redacted);
        Assert.DoesNotContain("abcdef123", redacted);
        Assert.DoesNotContain("myRawSecret", redacted);
        Assert.Contains("http://***:***@proxy.local:8080", redacted);
    }
}
