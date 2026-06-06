using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>options</c> — enumerate an enum field's variants or a flags
/// field's bit names. (JSON output: TODO.)</summary>
public static class OptionsCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        bool json = args.TakeFlag("--json");
        if (json) throw new CliError("options --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("options: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("options: missing <path>");

        ctx.EnsureLoaded(file);
        string resolved = ctx.ResolvePath(path);
        var field = ctx.LoadedOrThrow("options").Tag.Root.FieldPath(resolved)
            ?? throw new CliError($"field '{resolved}' not found");

        switch (field.Options())
        {
            case TagOptions.Enum e:
                Console.WriteLine($"Enum options for '{resolved}':");
                for (int i = 0; i < e.Names.Count; i++)
                {
                    string marker = e.Current == i ? " <-" : "";
                    Console.WriteLine($"  {i}: {e.Names[i]}{marker}");
                }
                return 0;
            case TagOptions.Flags f:
                Console.WriteLine($"Flag options for '{resolved}':");
                foreach (var item in f.Items)
                {
                    string marker = item.IsSet ? "[x]" : "[ ]";
                    Console.WriteLine($"  {item.Bit}: {marker} {item.Name}");
                }
                return 0;
            default:
                throw new CliError($"field '{resolved}' is not an enum or flags field");
        }
    }
}
