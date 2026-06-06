using System.Text.RegularExpressions;
using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>find</c> — deep value search across a directory of tags.
/// (JSON output: TODO. Output is sorted by path for determinism.)</summary>
public static class FindCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? group = args.TakeOption("--group");
        string? fieldName = args.TakeOption("--field-name");
        bool regex = args.TakeFlag("--regex");
        bool json = args.TakeFlag("--json");
        bool strict = args.TakeFlag("--strict");
        if (json) throw new CliError("find --json is not yet implemented");
        string dir = args.Positional(0) ?? throw new CliError("find: missing <dir>");
        string query = args.Positional(1) ?? throw new CliError("find: missing <value>");

        Regex? valueRe = regex ? new Regex(query) : null;
        Regex? fieldRe = fieldName is null ? null : new Regex(fieldName);

        var paths = new List<string>();
        WalkDir(dir, paths);
        paths.Sort(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            TagFile tag;
            try { tag = TagFile.Read(path); }
            catch (Exception e)
            {
                if (strict) throw new CliError($"failed to read '{path}': {e.Message}");
                continue;
            }
            if (group is not null && GroupTag.Format(tag.Group.Tag) != group) continue;

            var visitor = new FindVisitor(ctx, query, valueRe, fieldRe, path);
            Walk.Run(tag.Root, visitor);
            foreach (var h in visitor.Hits)
                Console.WriteLine($"{h.Tag} :: {h.FieldPath} = {h.Value}");
        }
        return 0;
    }

    private sealed class FindVisitor(CliContext ctx, string query, Regex? valueRe, Regex? fieldRe, string tagPath) : FieldVisitor
    {
        public List<(string Tag, string FieldPath, string Value)> Hits { get; } = new();

        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (fieldRe is not null && !fieldRe.IsMatch(field.Name)) return;
            if (field.Value is not { } value) return;
            string formatted = Formatter.FormatValue(ctx, value, false);
            bool matched = valueRe is not null ? valueRe.IsMatch(formatted) : formatted.Contains(query, StringComparison.Ordinal);
            if (matched) Hits.Add((tagPath, path, formatted));
        }
    }

    private static void WalkDir(string dir, List<string> outPaths)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(entry)) { WalkDir(entry, outPaths); continue; }
            if (Path.GetFileName(entry) == ".DS_Store") continue;
            outPaths.Add(entry);
        }
    }
}
