using System.Text;
using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>deps</c> — list every tag_reference in a tag as
/// <c>&lt;field_path&gt;: &lt;reference&gt;</c>. (JSON output: TODO.)</summary>
public static class DepsCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        bool unique = args.TakeFlag("--unique");
        bool json = args.TakeFlag("--json");
        if (json) throw new CliError("deps --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("deps: missing <file>");

        ctx.EnsureLoaded(file);
        var root = ctx.LoadedOrThrow("deps").Tag.Root;

        var visitor = new DepsVisitor(ctx);
        Walk.Run(root, visitor);

        var refs = visitor.Refs;
        if (unique)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            refs = refs.Where(r => seen.Add(r.Rendered)).ToList();
        }

        foreach (var (fieldPath, rendered) in refs)
            Console.WriteLine($"{fieldPath}: {rendered}");
        return 0;
    }

    private sealed class DepsVisitor(CliContext ctx) : FieldVisitor
    {
        public List<(string Path, string Rendered)> Refs { get; } = new();

        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (field.Value is not TagFieldData.TagReference tr || tr.Value.GroupTagAndName is null)
                return;
            var sb = new StringBuilder();
            Formatter.WriteTagReference(ctx, sb, tr.Value);
            Refs.Add((path, sb.ToString()));
        }
    }
}
