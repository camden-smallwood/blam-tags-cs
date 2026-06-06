using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>extract-data</c> — write the raw bytes of a single <c>tag_data</c> field
/// to a file. Errors if the field path doesn't resolve to a data leaf.
/// </summary>
public static class ExtractDataCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        string file = args.Positional(0) ?? throw new CliError("extract-data: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("extract-data: missing <field-path>");

        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("extract-data");
        string resolved = ctx.ResolvePath(path);
        var root = loaded.Tag.Root;

        var field = root.FieldPath(resolved) ?? throw new CliError(
            Suggest.FieldName(root, resolved) is { } s
                ? $"field '{resolved}' not found. Did you mean '{s}'?"
                : $"field '{resolved}' not found");

        byte[] bytes = field.AsData()
            ?? throw new CliError($"field '{resolved}' is {field.TypeName} (not a `tag_data` field)");

        string target = output ?? DefaultOutputPath(loaded.Path, field.Name);
        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllBytes(target, bytes);

        Console.WriteLine($"{target}: {bytes.Length} bytes");
        return 0;
    }

    private static string DefaultOutputPath(string tagPath, string fieldName)
    {
        string stem = Path.GetFileNameWithoutExtension(tagPath);
        return $"{stem}.{Sanitize(fieldName)}.bin";
    }

    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
}
