namespace BlamTags.Shell.Commands;

/// <summary><c>set</c> — write a field value from a string. Emits
/// <c>(was X)</c> so edits are self-describing.</summary>
public static class SetCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        bool dryRun = args.TakeFlag("--dry-run");
        string file = args.Positional(0) ?? throw new CliError("set: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("set: missing <path>");
        string value = args.Positional(2) ?? throw new CliError("set: missing <value>");

        ctx.EnsureLoaded(file);
        string resolved = ctx.ResolvePath(path);
        var loaded = ctx.LoadedOrThrow("set");
        var field = loaded.Tag.Root.FieldPath(resolved)
            ?? throw new CliError($"field '{resolved}' not found");

        string? previous = field.Value is { } v ? Formatter.FormatValue(ctx, v, false) : null;
        var parsed = ValueParser.Parse(ctx, field, value);

        string was = previous is not null ? $" (was {previous})" : "";
        string core = $"set {resolved} = {value}{was}";

        if (dryRun)
        {
            Console.WriteLine($"(dry run) would {core}");
            return 0;
        }

        field.Set(parsed);
        loaded.Dirty = true;
        var (target, redirected) = loaded.Commit(output);
        Console.WriteLine(core);
        if (redirected) Console.WriteLine($"saved to {target}");
        return 0;
    }
}
