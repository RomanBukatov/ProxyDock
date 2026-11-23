using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Infrastructure.Launching;
using Xunit;

namespace ProxyDock.Tests;

public sealed class InteractiveTerminalSmokeTests
{
    private sealed record ChildOutput(
        int ProcessId,
        string WorkingDirectory,
        string[] Args,
        Dictionary<string, string?> Environment);

    [Fact]
    public void Discovery_ResolvesPowerShell_AndSkipsExtensionlessNpmShim()
    {
        using var scope = new TempTestScope();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();

        var ps = discovery.ResolveTerminalExecutable(TerminalPreference.PowerShell);
        Assert.NotNull(ps);
        Assert.True(File.Exists(ps));

        var fakeBin = Path.Combine(scope.RootPath, "FakeNpmBin");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "mytool"), "#!/bin/sh\necho unix\n");
        File.WriteAllText(Path.Combine(fakeBin, "mytool.cmd"), "@echo off\r\necho win\r\n");

        var prevPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + prevPath);
            var resolved = discovery.ResolveTarget(LaunchKind.Cli, "mytool");
            Assert.True(resolved.Found);
            Assert.EndsWith("mytool.cmd", resolved.ResolvedPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(resolved.IsBatchOrScript);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
        }
    }

    [Fact]
    public async Task CliTerminalScript_DecryptsDpapiCredentialsAndPassesEnvAndArgsToChild()
    {
        using var scope = new TempTestScope();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var credentialStore = scope.Services.GetRequiredService<ICredentialStore>();
        var cliStrategy = scope.Services.GetServices<ILaunchStrategy>().OfType<CliTerminalLaunchStrategy>().Single();
        var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();

        var testChildExe = TestPaths.LocateTestChildExecutable();
        var outFile = Path.Combine(scope.RootPath, "cli-dpapi-out.json");

        var proxy = await proxyService.SaveAsync(new ProxyProfile
        {
            Name = "Auth Proxy",
            Host = "127.0.0.1",
            Port = 8899,
            Username = "cliuser",
            NoProxy = "localhost"
        }, newPassword: "CliSecretPassword!99");

        var creds = await credentialStore.LoadAsync(proxy.Id);
        var app = new ApplicationProfile
        {
            Name = "CLI Test Tool",
            LaunchKind = LaunchKind.Cli,
            ExecutableOrCommand = testChildExe,
            WorkingDirectory = scope.RootPath,
            Arguments = $"--output \"{outFile}\" --msg \"it's ok\"",
            LaunchInTerminal = true,
            KeepTerminalOpen = false,
            TerminalOverride = TerminalPreference.PowerShell
        };

        var plan = await cliStrategy.BuildPlanAsync(app, proxy, creds, new AppSettings { DefaultTerminal = TerminalPreference.PowerShell }, isDirectNoProxy: false);

        Assert.True(plan.UseInteractiveTerminal);
        Assert.NotNull(plan.GeneratedScriptPath);
        var scriptText = await File.ReadAllTextAsync(plan.GeneratedScriptPath!);
        Assert.DoesNotContain("CliSecretPassword!99", scriptText, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-SecureString", scriptText, StringComparison.Ordinal);

        // Запускаем powershell.exe по сгенерированному плану (с автозакрытием после завершения, т.к. KeepTerminalOpen = false)
        var psExe = discovery.ResolveTerminalExecutable(TerminalPreference.PowerShell)!;
        var psi = new ProcessStartInfo
        {
            FileName = psExe,
            WorkingDirectory = plan.WorkingDirectory!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in plan.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.Equal(0, proc.ExitCode);
        Assert.False(File.Exists(plan.GeneratedScriptPath)); // Скрипт самоудаляется
        Assert.True(File.Exists(outFile));

        var child = JsonSerializer.Deserialize<ChildOutput>(await File.ReadAllTextAsync(outFile))!;
        Assert.Equal("http://cliuser:CliSecretPassword%2199@127.0.0.1:8899", child.Environment["HTTPS_PROXY"]);
        Assert.Equal("localhost", child.Environment["NO_PROXY"]);
        Assert.Contains("it's ok", child.Args);
    }
}
