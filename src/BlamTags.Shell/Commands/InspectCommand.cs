using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>inspect</c> — the field-tree view. Flat mode (default) recurses through
/// structs / arrays / pageable-resources but stops at blocks (count only);
/// <c>--full</c> recurses through blocks too. Single-leaf block/array elements
/// collapse to one <c>[i] name: type = value</c> line. (JSON output: TODO.)
/// </summary>
public static class InspectCommand
{
    public sealed class Filters
    {
        public required List<string> Names { get; init; }
        public required List<string> Excludes { get; init; }
        public required string? Value { get; init; }

        public bool IsActive => Names.Count != 0 || Excludes.Count != 0 || Value is not null;

        public bool LeafMatches(string name, string? formatted)
        {
            if (Names.Count != 0 && !Names.Any(name.Contains)) return false;
            if (Excludes.Any(name.Contains)) return false;
            if (Value is { } needle)
            {
                if (formatted is null || !formatted.Contains(needle)) return false;
            }
            return true;
        }
    }

    public static int Run(CliContext ctx, Args args)
    {
        bool showAll = args.TakeFlag("--all");
        bool full = args.TakeFlag("--full");
        bool json = args.TakeFlag("--json");
        var filters = new Filters
        {
            Names = SplitCsv(args.TakeOption("--filter")),
            Excludes = SplitCsv(args.TakeOption("--filter-not")),
            Value = args.TakeOption("--filter-value"),
        };
        if (json) throw new CliError("inspect --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("inspect: missing <file>");
        string? path = args.Positional(1);

        ctx.EnsureLoaded(file);
        string nav = string.Join('/', ctx.Nav);
        string? resolved = path is null ? null : ctx.ResolvePath(path);
        var root = ctx.LoadedOrThrow("inspect").Tag.Root;

        if (resolved is null)
        {
            var target = nav.Length == 0 ? root
                : root.Descend(nav) ?? throw new CliError($"nav path '{nav}' does not resolve to a struct");
            PrintViaWalker(ctx, target, filters, showAll, full);
            return 0;
        }

        // Trailing [N] → descend straight into that element.
        if (resolved.EndsWith(']') && root.Descend(resolved) is { } elem)
        {
            PrintViaWalker(ctx, elem, filters, showAll, full);
            return 0;
        }

        var field = root.FieldPath(resolved) ?? throw new CliError($"field '{resolved}' not found");

        if (field.AsStruct() is { } nested)
            PrintViaWalker(ctx, nested, filters, showAll, full);
        else if (field.AsBlock() is { } block)
            PrintBlock(ctx, block, resolved, filters, showAll, full);
        else if (field.AsArray() is { } array)
            PrintArray(ctx, array, resolved, filters, showAll, full);
        else if (field.AsResource() is { } resource)
            PrintResource(ctx, resource, resolved, filters, showAll, full);
        else
            throw new CliError($"field '{resolved}' is not a struct, block, array, or pageable_resource");
        return 0;
    }

    private static List<string> SplitCsv(string? s) =>
        string.IsNullOrEmpty(s) ? new List<string>() : s.Split(',').ToList();

    private static void PrintViaWalker(CliContext ctx, TagStruct start, Filters filters, bool showAll, bool full)
    {
        Walk.Run(start, new InspectText(ctx, filters, showAll, full));
    }

    private static void PrintBlock(CliContext ctx, TagBlock block, string label, Filters filters, bool showAll, bool full)
    {
        Console.WriteLine($"{label}: block [{block.Count} elements]");
        if (full)
        {
            int i = 0;
            foreach (var s in block.Elements())
            {
                var v = new InspectText(ctx, filters, showAll, full);
                if (!v.TryInlineElement(1, i, s))
                {
                    Console.WriteLine($"  [{i}]");
                    Walk.Run(s, v);
                }
                i++;
            }
        }
        else
        {
            Console.WriteLine($"  (pass --full to expand, or inspect a single element with `{label}[<index>]`)");
        }
    }

    private static void PrintArray(CliContext ctx, TagArray array, string label, Filters filters, bool showAll, bool full)
    {
        Console.WriteLine($"{label}: array [{array.Count} elements]");
        int i = 0;
        foreach (var s in array.Elements())
        {
            var v = new InspectText(ctx, filters, showAll, full);
            if (!v.TryInlineElement(1, i, s))
            {
                Console.WriteLine($"  [{i}]");
                Walk.Run(s, v);
            }
            i++;
        }
    }

    private static void PrintResource(CliContext ctx, TagResource resource, string label, Filters filters, bool showAll, bool full)
    {
        Console.WriteLine($"{label}: pageable_resource [{ResourceSummary(resource)}]");
        if (resource.AsStruct() is { } header)
            Walk.Run(header, new InspectText(ctx, filters, showAll, full));
    }

    internal static string ResourceSummary(TagResource resource)
    {
        string kind = resource.Kind switch
        {
            TagResourceKind.Null => "null",
            TagResourceKind.Exploded => "exploded",
            TagResourceKind.Xsync => "xsync",
            _ => "null",
        };
        if (resource.ExplodedPayload is { } ep) return $"{kind}, payload {ep.Length} bytes";
        if (resource.XsyncPayload is { } xp) return $"{kind}, payload {xp.Length} bytes";
        return kind;
    }

    private sealed class InspectText(CliContext ctx, Filters filters, bool showAll, bool full) : FieldVisitor
    {
        public override bool IncludePadding => showAll;

        private static string Indent(int depth) => new(' ', depth * 2);

        /// <summary>Inline a single-leaf element as <c>[i] name: type = value</c>;
        /// returns true if it consumed the element (including a filtered skip).</summary>
        public bool TryInlineElement(int depth, int index, TagStruct elem)
        {
            if (showAll) return false;
            var fields = elem.Fields().Take(2).ToList();
            if (fields.Count != 1 || fields[0].Value is null) return false;
            var only = fields[0];
            var value = only.Value!;
            string formatted = Formatter.FormatValue(ctx, value, false);
            if (!filters.IsActive || filters.LeafMatches(only.Name, formatted))
                Console.WriteLine($"{Indent(depth)}[{index}] {only.Name}: {only.TypeName} = {formatted}");
            return true;
        }

        public override VisitControl EnterStruct(string path, int depth, TagField field)
        {
            if (!filters.IsActive) Console.WriteLine($"{Indent(depth)}{field.Name}: struct");
            return VisitControl.Descend;
        }

        public override VisitControl EnterBlock(string path, int depth, TagField field, TagBlock block)
        {
            if (!filters.IsActive) Console.WriteLine($"{Indent(depth)}{field.Name}: block [{block.Count} elements]");
            return full ? VisitControl.Descend : VisitControl.Skip;
        }

        public override VisitControl EnterArray(string path, int depth, TagField field, TagArray array)
        {
            if (!filters.IsActive) Console.WriteLine($"{Indent(depth)}{field.Name}: array [{array.Count} elements]");
            return VisitControl.Descend;
        }

        public override VisitControl EnterElement(string path, int depth, int index, TagStruct elem)
        {
            if (TryInlineElement(depth, index, elem))
                return VisitControl.Skip;
            if (!filters.IsActive)
                Console.WriteLine($"{Indent(System.Math.Max(depth - 1, 0))}[{index}]");
            return VisitControl.Descend;
        }

        public override VisitControl EnterResource(string path, int depth, TagField field, TagResource resource)
        {
            if (!filters.IsActive)
                Console.WriteLine($"{Indent(depth)}{field.Name}: pageable_resource [{ResourceSummary(resource)}]");
            return VisitControl.Descend;
        }

        public override void VisitLeaf(string path, int depth, TagField field)
        {
            string name = field.Name, typeName = field.TypeName;
            if (field.Value is { } value)
            {
                string formatted = Formatter.FormatValue(ctx, value, false);
                if (!filters.IsActive || filters.LeafMatches(name, formatted))
                    Console.WriteLine($"{Indent(depth)}{name}: {typeName} = {formatted}");
            }
            else
            {
                if (!filters.IsActive || filters.LeafMatches(name, null))
                    Console.WriteLine($"{Indent(depth)}{name}: {typeName}");
            }
        }
    }
}
