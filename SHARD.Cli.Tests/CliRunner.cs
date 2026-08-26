using System.Diagnostics;

namespace SHARD.Cli.Tests;

/// <summary>
/// Launches the actual built shard-cli.dll as a subprocess and captures its output — a
/// deliberate black-box approach rather than invoking Program.cs's top-level-statement local
/// functions in-process. Program.cs's Die() calls Environment.Exit() on error paths (exactly
/// the paths these tests care about), which would tear down the whole test process if invoked
/// in-process; running as a real subprocess is also simply what "does the CLI work" means.
/// </summary>
internal static class CliRunner
{
    private static readonly string DllPath = Path.Combine(AppContext.BaseDirectory, "shard-cli.dll");

    public static CliResult Run(params string[] args)
    {
        if (!File.Exists(DllPath))
            throw new FileNotFoundException(
                $"shard-cli.dll not found at {DllPath} — check SHARD.Cli.Tests' ProjectReference to SHARD.Cli.", DllPath);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(DllPath);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start shard-cli subprocess.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CliResult(process.ExitCode, stdout, stderr);
    }
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
