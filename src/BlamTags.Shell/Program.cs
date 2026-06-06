using BlamTags.Shell;
using BlamTags.Shell.Commands;

// blam-tag-shell (C#) — Halo tag file inspector and editor.
//
// Phase 4: all non-extractor commands + REPL. The extract-* and
// list-cache verbs (which need the extractor / monolithic-cache layers)
// remain in the post-Phase-4 backlog.

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] argv)
    {
        try
        {
            var (game, _cache, rest) = ExtractGlobals(argv);
            if (rest.Count == 0)
            {
                Console.Error.WriteLine("Halo tag file inspector and editor\n\nUsage: blam-tag-shell [--game <GAME>] <COMMAND> [ARGS...]");
                return 2;
            }

            string command = rest[0];
            var ctx = new CliContext(game);

            if (command == "repl")
                return Repl.Run(ctx, rest.Count > 1 ? rest[1] : null);

            return Dispatch(ctx, command, new Args(rest.Skip(1)));
        }
        catch (CliError e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    /// <summary>Execute one parsed command. Shared by one-shot mode and the REPL.</summary>
    public static int Dispatch(CliContext ctx, string command, Args args) => command switch
    {
        "header" => HeaderCommand.Run(ctx, args),
        "inspect" => InspectCommand.Run(ctx, args),
        "get" => GetCommand.Run(ctx, args),
        "set" => SetCommand.Run(ctx, args),
        "flag" => FlagCommand.Run(ctx, args),
        "options" => OptionsCommand.Run(ctx, args),
        "deps" => DepsCommand.Run(ctx, args),
        "block" => BlockCommand.Run(ctx, args),
        "list" => ListCommand.Run(ctx, args),
        "find" => FindCommand.Run(ctx, args),
        "check" => CheckCommand.Run(ctx, args),
        "export" => ExportCommand.Run(ctx, args),
        "layout-diff" => LayoutDiffCommand.Run(ctx, args),
        "data-diff" => DataDiffCommand.Run(ctx, args),
        "extract-bitmap" => ExtractBitmapCommand.Run(ctx, args),
        "extract-geometry" => ExtractGeometryCommand.Run(ctx, args),
        "extract-animation" => ExtractAnimationCommand.Run(ctx, args),
        "extract-data" => ExtractDataCommand.Run(ctx, args),
        "list-animations" => ListAnimationsCommand.Run(ctx, args),
        "new" => NewCommand.Run(ctx, args),
        "add-dependency-list" or "remove-dependency-list" or "rebuild-dependency-list"
            or "add-import-info" or "remove-import-info"
            or "add-asset-depot-storage" or "remove-asset-depot-storage"
            => StreamsCommand.Run(ctx, command, args),
        _ => NotImplemented(command),
    };

    /// <summary>Commands whose first positional is the loaded tag file. The REPL
    /// injects the loaded path for these so the user omits it.</summary>
    public static bool IsTagBound(string command) => command switch
    {
        "header" or "inspect" or "get" or "set" or "flag" or "options" or "deps"
            or "block" or "check" or "export" or "extract-bitmap" or "extract-geometry" or "extract-animation"
            or "extract-data" or "list-animations"
            or "add-dependency-list" or "remove-dependency-list" or "rebuild-dependency-list"
            or "add-import-info" or "remove-import-info"
            or "add-asset-depot-storage" or "remove-asset-depot-storage" => true,
        _ => false,
    };

    private static int NotImplemented(string command)
    {
        Console.Error.WriteLine($"Error: command `{command}` is not yet ported (extract-* / list-cache are backlog)");
        return 2;
    }

    private static (string? Game, string? Cache, List<string> Remaining) ExtractGlobals(string[] argv)
    {
        string? game = null, cache = null;
        var rest = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            switch (a)
            {
                case "--game" or "-g":
                    game = Next(argv, ref i, a);
                    break;
                case "--cache" or "-c":
                    cache = Next(argv, ref i, a);
                    break;
                default:
                    if (a.StartsWith("--game=", StringComparison.Ordinal)) game = a["--game=".Length..];
                    else if (a.StartsWith("--cache=", StringComparison.Ordinal)) cache = a["--cache=".Length..];
                    else rest.Add(a);
                    break;
            }
        }
        return (game, cache, rest);
    }

    private static string Next(string[] argv, ref int i, string flag)
    {
        if (i + 1 >= argv.Length) throw new CliError($"{flag} requires a value");
        return argv[++i];
    }
}
