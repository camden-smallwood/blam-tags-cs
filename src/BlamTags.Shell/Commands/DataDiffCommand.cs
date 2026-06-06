using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>data-diff</c> — compare two tags' values at every leaf path.
/// (JSON output: TODO.)</summary>
public static class DataDiffCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? subtree = args.TakeOption("--only");
        bool json = args.TakeFlag("--json");
        if (json) throw new CliError("data-diff --json is not yet implemented");
        string fileA = args.Positional(0) ?? throw new CliError("data-diff: missing <file_a>");
        string fileB = args.Positional(1) ?? throw new CliError("data-diff: missing <file_b>");

        TagFile tagA, tagB;
        try { tagA = TagFile.Read(fileA); } catch (Exception e) { throw new CliError($"failed to read '{fileA}': {e.Message}"); }
        try { tagB = TagFile.Read(fileB); } catch (Exception e) { throw new CliError($"failed to read '{fileB}': {e.Message}"); }

        var mapA = Collect(ctx, tagA, subtree);
        var mapB = Collect(ctx, tagB, subtree);

        int changed = 0, onlyA = 0, onlyB = 0;
        var lines = new List<string>();
        foreach (var (path, va) in mapA)
        {
            if (!mapB.TryGetValue(path, out var vb)) { lines.Add($"- {path}: {va}"); onlyA++; }
            else if (vb != va) { lines.Add($"~ {path}: {va} -> {vb}"); changed++; }
        }
        foreach (var (path, vb) in mapB)
            if (!mapA.ContainsKey(path)) { lines.Add($"+ {path}: {vb}"); onlyB++; }

        // Re-order to match Rust: all A-side findings (path order), then B-only.
        // The two foreach loops above already produce that order since both maps
        // are sorted and B-only is appended last.
        if (lines.Count == 0)
        {
            Console.WriteLine($"identical — no differences under {subtree ?? "/"}");
            return 0;
        }
        Console.WriteLine($"--- {fileA}");
        Console.WriteLine($"+++ {fileB}");
        if (subtree is not null) Console.WriteLine($"subtree: {subtree}");
        Console.WriteLine();
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine();
        Console.WriteLine($"{changed} changed, {onlyA} only in a, {onlyB} only in b");
        return 0;
    }

    private static SortedDictionary<string, string> Collect(CliContext ctx, TagFile tag, string? subtree)
    {
        var root = tag.Root;
        var start = subtree is null ? root
            : root.Descend(subtree) ?? throw new CliError($"subtree '{subtree}' does not resolve to a struct");
        var v = new CollectVisitor(ctx);
        Walk.Run(start, v);
        return v.Values;
    }

    private sealed class CollectVisitor(CliContext ctx) : FieldVisitor
    {
        public SortedDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (field.Value is { } value && field.Name.Length != 0)
                Values[path] = Formatter.FormatValue(ctx, value, false);
        }
    }
}
