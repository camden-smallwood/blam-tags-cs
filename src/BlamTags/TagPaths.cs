namespace BlamTags;

/// <summary>
/// Filesystem helpers for locating tags relative to a loose <c>tags/</c>
/// tree — a port of the path utilities in the Rust <c>paths.rs</c>. Used by
/// reference-following extractors (e.g. <c>extract-geometry</c> resolving a
/// model's render/collision/physics references).
/// </summary>
public static class TagPaths
{
    /// <summary>Walk a path's components for the last <c>tags</c> directory and
    /// return the path up to and including it (the tags root), or null when
    /// the path doesn't live under a <c>tags/</c> directory.</summary>
    public static string? DeriveTagsRoot(string path)
    {
        string abs;
        try { abs = Path.GetFullPath(path); }
        catch { return null; }
        var parts = abs.Split(Path.DirectorySeparatorChar);
        int last = -1;
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] == "tags") last = i;
        if (last < 0) return null;
        return string.Join(Path.DirectorySeparatorChar, parts.Take(last + 1));
    }

    /// <summary>Join a Halo-style <c>\</c>-separated relative tag path onto the
    /// tags root and append the friendly group extension.</summary>
    public static string ResolveTagPath(string tagsRoot, string rel, string ext)
    {
        var segments = new List<string> { tagsRoot };
        segments.AddRange(rel.Split('\\'));
        return Path.Combine(segments.ToArray()) + "." + ext;
    }
}
