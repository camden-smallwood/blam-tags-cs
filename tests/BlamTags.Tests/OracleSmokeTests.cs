namespace BlamTags.Tests;

/// <summary>
/// Confirms the Rust ground-truth oracle is built and runnable, so later
/// phases can rely on it for differential comparison.
/// </summary>
public sealed class OracleSmokeTests
{
    [SkippableFact]
    public void Oracle_Binary_Runs()
    {
        Skip.If(TestEnvironment.OraclePath is null,
            "Oracle binary not found. Build it: cargo build --release --workspace.");

        var result = Oracle.Run("--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Halo tag file", result.StdOut);
    }
}
