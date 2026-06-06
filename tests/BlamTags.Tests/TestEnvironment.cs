namespace BlamTags.Tests;

/// <summary>
/// Resolves the two external resources the differential test suite needs:
/// a corpus of real tag files to sweep, and the Rust <c>blam-tag-shell</c>
/// binary that serves as the ground-truth oracle for value / CLI-output
/// comparisons in later phases.
/// </summary>
/// <remarks>
/// Both are optional. When a resource is absent the dependent tests report
/// as <em>Skipped</em> (via <c>Skip.If</c>) rather than failing, so the
/// suite is runnable on a fresh checkout with no corpus configured.
///
/// Configuration (env vars override the defaults):
/// <list type="bullet">
///   <item><c>BLAM_TAGS_CORPUS</c> — directory tree of tag files to sweep.</item>
///   <item><c>BLAM_TAGS_ORACLE</c> — path to the Rust release binary.
///     Defaults to <c>&lt;repo&gt;/../blam-tags/target/release/blam-tag-shell</c>.</item>
/// </list>
/// </remarks>
public static class TestEnvironment
{
    /// <summary>Corpus root, or <c>null</c> if unset / nonexistent.</summary>
    public static string? CorpusRoot
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("BLAM_TAGS_CORPUS");
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>The <c>definitions/</c> tree (symlinked from the Rust project),
    /// or <c>null</c> if not found.</summary>
    public static string? DefinitionsRoot
    {
        get
        {
            var slnDir = FindSolutionDirectory();
            if (slnDir is null)
                return null;
            var dir = Path.Combine(slnDir, "definitions");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>Path to the Rust oracle binary, or <c>null</c> if not found.</summary>
    public static string? OraclePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("BLAM_TAGS_ORACLE");
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? configured : null;

            var slnDir = FindSolutionDirectory();
            if (slnDir is null)
                return null;

            var candidate = Path.GetFullPath(Path.Combine(
                slnDir, "..", "blam-tags", "target", "release", "blam-tag-shell"));
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>
    /// Enumerate candidate tag files under the corpus. Tag files have no
    /// single extension (the extension <em>is</em> the group name), so we
    /// take every regular file and let the reader reject non-tags.
    /// </summary>
    public static IEnumerable<string> EnumerateCorpusTags(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    /// <summary>A tag file carries the <c>BLAM</c> magic at offset 60, in
    /// either byte order (LE files store it reversed as <c>MALB</c>).</summary>
    public static bool LooksLikeTagFile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64) return false;
        var sig = bytes.Slice(60, 4);
        return sig.SequenceEqual("BLAM"u8) || sig.SequenceEqual("MALB"u8);
    }

    /// <summary>Walk up from the test assembly looking for the solution file
    /// (<c>.slnx</c> on .NET 10+, or the legacy <c>.sln</c>).</summary>
    private static string? FindSolutionDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlamTags.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "BlamTags.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
