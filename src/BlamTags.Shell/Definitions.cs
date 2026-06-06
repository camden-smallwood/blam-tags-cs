namespace BlamTags.Shell;

/// <summary>
/// Locates the <c>definitions/</c> tree. A shipped binary carries its
/// definitions next to the executable, so that's checked first; the current
/// directory and a walk-up (dev convenience) are fallbacks. An explicit
/// <c>BLAM_TAGS_DEFINITIONS</c> env var overrides everything.
/// </summary>
public static class Definitions
{
    /// <summary>Resolve the definitions root, or null if not found.</summary>
    public static string? FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BLAM_TAGS_DEFINITIONS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;

        // Next to the executable (the shipped layout).
        var beside = Path.Combine(AppContext.BaseDirectory, "definitions");
        if (Directory.Exists(beside))
            return beside;

        // Current working directory.
        var cwd = Path.Combine(Environment.CurrentDirectory, "definitions");
        if (Directory.Exists(cwd))
            return cwd;

        // Walk up from the executable (dev tree: bin/Debug/... -> repo root).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "definitions");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
