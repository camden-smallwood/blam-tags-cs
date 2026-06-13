using BlamTags;

namespace BlamTags.Shell;

/// <summary>A loaded tag plus where it came from and a dirty flag.</summary>
public sealed class LoadedTag(string path, TagFile tag)
{
    public string Path { get; set; } = path;
    public TagFile Tag { get; set; } = tag;
    public bool Dirty { get; set; }

    /// <summary>Write to <paramref name="dest"/> (or back to <see cref="Path"/>),
    /// clearing the dirty flag. Returns the path written.</summary>
    public string Save(string? dest)
    {
        string target = dest ?? Path;
        Tag.Write(target);
        Dirty = false;
        return target;
    }

    /// <summary>Save and report whether output was redirected (<c>--output</c>
    /// to a path other than the source).</summary>
    public (string Target, bool Redirected) Commit(string? dest)
    {
        string source = Path;
        string target = Save(dest);
        return (target, target != source);
    }
}

/// <summary>
/// Per-invocation CLI state: the loaded tag, the REPL navigation prefix, the
/// selected game, and its group-tag index. Mirrors the Rust
/// <c>CliContext</c>.
/// </summary>
public sealed class CliContext
{
    public LoadedTag? Loaded { get; set; }
    public List<string> Nav { get; } = new();
    public string? Game { get; }
    public string? DefinitionsRoot { get; }
    public TagIndex TagIndex { get; }

    public CliContext(string? game)
    {
        Game = game;
        DefinitionsRoot = Definitions.FindRoot();
        if (game is not null)
        {
            if (DefinitionsRoot is null)
                throw new CliError($"--game {game} set but no definitions/ directory found (looked next to the binary, in the cwd, and up the tree)");
            TagIndex = TagIndex.Load(DefinitionsRoot, game);
        }
        else
        {
            TagIndex = TagIndex.Empty;
        }
    }

    /// <summary>The game id or an error referencing the command needing it.</summary>
    public string RequireGame(string command) =>
        Game ?? throw new CliError($"`{command}` needs a `--game` (e.g. `--game halo3_mcc`) to locate its schema");

    /// <summary>Schema JSON path for a group under the current game.</summary>
    public string SchemaPath(string group)
    {
        RequireGame(group);
        return System.IO.Path.Combine(DefinitionsRoot!, Game!, $"{group}.json");
    }

    /// <summary>REPL mode: tag-bound commands use the already-loaded tag and
    /// accumulate edits instead of reloading from disk each command.</summary>
    public bool ReplMode { get; set; }

    /// <summary>For a tag-bound command: load <paramref name="file"/> in
    /// one-shot mode, or require an already-loaded tag in REPL mode (where the
    /// injected file positional is ignored).</summary>
    public void EnsureLoaded(string file)
    {
        if (ReplMode)
        {
            if (Loaded is null) throw new CliError("no tag loaded (use `open <path>` first)");
        }
        else
        {
            Load(file);
        }
    }

    /// <summary>Load a tag file into <see cref="Loaded"/>, resetting nav.</summary>
    public void Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) { throw new CliError($"failed to load tag file: {e.Message}"); }
        TagFile tag;
        try { tag = ReadTagFromBytes(bytes); }
        catch (CliError) { throw; }
        catch (Exception e) { throw new CliError($"failed to load tag file: {e.Message}"); }
        Loaded = new LoadedTag(path, tag);
        Nav.Clear();
    }

    /// <summary>Parse tag bytes, routing classic (Halo CE / Halo 2) loose tags
    /// through the classic decoder and MCC self-describing tags through the
    /// embedded-layout reader. Classic tags carry no layout chunk, so a
    /// <c>--game</c> is required to locate the group's JSON schema.</summary>
    private TagFile ReadTagFromBytes(byte[] bytes)
    {
        var classic = Classic.ParseHeader(bytes);
        if (classic is not { } parsed)
            return TagFile.ReadFromBytes(bytes);

        if (Game is null)
            throw new CliError(
                "classic Halo CE / Halo 2 tags carry no embedded layout — pass a `--game` (e.g. `--game halo2_mcc`)");

        uint groupTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(parsed.Header.GroupTag);
        string? name = TagIndex.NameFor(groupTag)
            ?? throw new CliError($"no group definition for group tag \"{GroupTag.Format(groupTag)}\" in definitions/{Game}/");
        var layout = TagLayout.FromJson(SchemaPath(name));
        return Classic.ReadClassicTagFile(bytes, layout);
    }

    public LoadedTag LoadedOrThrow(string command) =>
        Loaded ?? throw new CliError($"`{command}` needs a loaded tag");

    /// <summary>Load a tag referenced by the currently-loaded tag: resolve
    /// <paramref name="relPath"/> (a Halo-style <c>\</c>-separated relative
    /// path) under the loaded tag's <c>tags/</c> root, appending the friendly
    /// group extension. Mirrors the Rust <c>load_referenced_tag</c> (loose-tag
    /// path; monolithic-cache reads are a backlog item).</summary>
    public TagFile LoadReferencedTag(string relPath, string groupExt)
    {
        var loaded = LoadedOrThrow("load_referenced_tag");
        string tagsRoot = TagPaths.DeriveTagsRoot(loaded.Path)
            ?? throw new CliError("failed to derive tags root from input path — input must live under a `tags/` directory");
        string path = TagPaths.ResolveTagPath(tagsRoot, relPath, groupExt);
        try { return ReadTagFromBytes(File.ReadAllBytes(path)); }
        catch (CliError) { throw; }
        catch (Exception e) { throw new CliError($"failed to read {path}: {e.Message}"); }
    }

    /// <summary>Resolve a user path against the REPL nav prefix (leading
    /// <c>/</c> = absolute).</summary>
    public string ResolvePath(string userPath)
    {
        if (userPath.StartsWith('/'))
            return userPath[1..];
        if (Nav.Count == 0)
            return userPath;
        if (userPath.Length == 0)
            return string.Join('/', Nav);
        return $"{string.Join('/', Nav)}/{userPath}";
    }
}

/// <summary>A user-facing CLI error (printed as <c>Error: …</c>, exit 1).</summary>
public sealed class CliError(string message) : Exception(message);
