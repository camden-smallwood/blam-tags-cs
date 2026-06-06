using System.Diagnostics;
using System.Text;

namespace BlamTags.Tests;

/// <summary>
/// Thin wrapper over the Rust <c>blam-tag-shell</c> binary. Later phases
/// diff the C# port's output against this ground truth (CLI text, field
/// values, extractor results). Phase 0 only needs it to exist and run.
/// </summary>
public static class Oracle
{
    public readonly record struct Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>Run the oracle with the given arguments, capturing output.</summary>
    public static Result Run(params string[] args)
    {
        var oracle = TestEnvironment.OraclePath
            ?? throw new InvalidOperationException("Oracle binary not found.");

        var psi = new ProcessStartInfo(oracle)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(oracle),
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start oracle process.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        return new Result(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
