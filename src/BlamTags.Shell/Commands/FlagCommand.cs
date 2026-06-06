namespace BlamTags.Shell.Commands;

/// <summary><c>flag</c> — read or set a single flag bit by name.</summary>
public static class FlagCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        bool dryRun = args.TakeFlag("--dry-run");
        string file = args.Positional(0) ?? throw new CliError("flag: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("flag: missing <path>");
        string flagName = args.Positional(2) ?? throw new CliError("flag: missing <flag_name>");
        string? action = args.Positional(3);

        ctx.EnsureLoaded(file);
        string resolved = ctx.ResolvePath(path);
        var loaded = ctx.LoadedOrThrow("flag");
        var field = loaded.Tag.Root.FieldPath(resolved)
            ?? throw new CliError($"field '{resolved}' not found");
        var flag = field.Flag(flagName)
            ?? throw new CliError($"flag '{flagName}' not found on field '{resolved}'");

        bool current = flag.IsSet;
        if (action is null)
        {
            Console.WriteLine($"{resolved}.{flagName} = {(current ? "on" : "off")}");
            return 0;
        }

        bool newValue = action switch
        {
            "on" => true,
            "off" => false,
            "toggle" => !current,
            _ => throw new CliError($"unknown action '{action}' (expected on, off, toggle)"),
        };

        string core = $"set {resolved}.{flagName} = {(newValue ? "on" : "off")} (was {(current ? "on" : "off")})";
        if (dryRun)
        {
            Console.WriteLine($"(dry run) would {core}");
            return 0;
        }

        flag.Set(newValue);
        loaded.Dirty = true;
        var (target, redirected) = loaded.Commit(output);
        Console.WriteLine(core);
        if (redirected) Console.WriteLine($"saved to {target}");
        return 0;
    }
}
