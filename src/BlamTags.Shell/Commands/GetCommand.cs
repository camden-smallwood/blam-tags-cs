using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>get</c> — read a single field's value. <c>--raw</c> strips the label,
/// <c>--hex</c> formats integers as hex. (JSON output: TODO.)
/// </summary>
public static class GetCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        bool raw = args.TakeFlag("--raw");
        bool hex = args.TakeFlag("--hex");
        bool json = args.TakeFlag("--json");
        if (json) throw new CliError("get --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("get: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("get: missing <path>");

        ctx.EnsureLoaded(file);
        string resolved = ctx.ResolvePath(path);
        var root = ctx.LoadedOrThrow("get").Tag.Root;

        var field = root.FieldPath(resolved) ?? throw new CliError(
            Suggest.FieldName(root, resolved) is { } s
                ? $"field '{resolved}' not found. Did you mean '{s}'?"
                : $"field '{resolved}' not found");

        string typeName = field.TypeName;

        string? summary = ContainerSummary(field);
        if (summary is not null)
        {
            Console.WriteLine(raw ? summary : $"{resolved}: {typeName} = {summary}");
            return 0;
        }

        var value = field.Value ?? throw new CliError("field has no parsed value");
        string formatted = Formatter.FormatValue(ctx, value, hex);
        Console.WriteLine(raw ? formatted : $"{resolved}: {typeName} = {formatted}");
        return 0;
    }

    private static string? ContainerSummary(TagField field)
    {
        if (field.AsStruct() is not null) return "struct";
        if (field.AsBlock() is { } block)
        {
            int n = block.Count;
            return $"block [{n} element{(n == 1 ? "" : "s")}]";
        }
        if (field.AsArray() is { } array)
            return $"array [{array.Count} elements]";
        if (field.AsResource() is { } resource)
        {
            string kind = resource.Kind switch
            {
                TagResourceKind.Null => "null",
                TagResourceKind.Exploded => "exploded",
                TagResourceKind.Xsync => "xsync",
                _ => "null",
            };
            return $"pageable_resource [{kind}]";
        }
        return null;
    }
}
