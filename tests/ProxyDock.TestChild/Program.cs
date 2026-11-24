using System.Collections;
using System.Text;
using System.Text.Json;

namespace ProxyDock.TestChild;

internal sealed record ChildReport(
    int ProcessId,
    string WorkingDirectory,
    string[] Args,
    Dictionary<string, string?> Environment);

internal static class Program
{
    private static readonly string[] InspectedKeys =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
        "NODE_USE_ENV_PROXY"
    ];

    public static async Task<int> Main(string[] args)
    {
        string? outputPath = null;
        string? postSleepOutputPath = null;
        var sleepMs = 0;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputPath = args[i + 1];
            }
            else if (args[i] == "--post-sleep-output" && i + 1 < args.Length)
            {
                postSleepOutputPath = args[i + 1];
            }
            else if (args[i] == "--sleep-ms" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ms))
            {
                sleepMs = ms;
            }
        }

        await WriteReportAsync(args, outputPath);

        if (sleepMs > 0)
        {
            await Task.Delay(sleepMs);
        }

        if (!string.IsNullOrWhiteSpace(postSleepOutputPath))
        {
            await WriteReportAsync(args, postSleepOutputPath);
        }

        return 0;
    }

    private static async Task WriteReportAsync(string[] args, string? outputPath)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in InspectedKeys)
        {
            env[key] = Environment.GetEnvironmentVariable(key);
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string keyStr && keyStr.StartsWith("PROXYDOCK_TEST_", StringComparison.Ordinal))
            {
                env[keyStr] = entry.Value?.ToString();
            }
        }

        var report = new ChildReport(
            ProcessId: Environment.ProcessId,
            WorkingDirectory: Environment.CurrentDirectory,
            Args: args,
            Environment: env);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8);
        }
        else
        {
            Console.WriteLine(json);
        }
    }
}
