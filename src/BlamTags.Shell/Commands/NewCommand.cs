using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>new &lt;group&gt;</c> — create a fresh tag from a group schema.</summary>
public static class NewCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        string group = args.Positional(0) ?? throw new CliError("new: missing <group>");
        string game = ctx.RequireGame("new");
        string schema = ctx.SchemaPath(group);
        if (!File.Exists(schema))
            throw new CliError($"schema not found: {schema} (is the group name right and `definitions/{game}/` present?)");

        string outPath = output ?? $"{group}.{group}";
        if (File.Exists(outPath))
            throw new CliError($"refusing to overwrite existing file: {outPath}");

        TagFile tag;
        try { tag = TagFile.New(schema); }
        catch (Exception e) { throw new CliError($"failed to build tag from {schema}: {e.Message}"); }
        tag.Write(outPath);

        Console.WriteLine($"created {outPath} from {schema}");
        return 0;
    }
}
