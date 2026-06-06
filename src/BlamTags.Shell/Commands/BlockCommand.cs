namespace BlamTags.Shell.Commands;

/// <summary><c>block</c> — structural edits to a block field: count / add /
/// insert / duplicate / delete / clear / swap / move. <c>count</c> is
/// read-only; the rest mutate. (JSON output is only meaningful for count.)</summary>
public static class BlockCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        bool dryRun = args.TakeFlag("--dry-run");
        bool json = args.TakeFlag("--json");
        string file = args.Positional(0) ?? throw new CliError("block: missing <file>");
        string path = args.Positional(1) ?? throw new CliError("block: missing <path>");
        string action = args.Positional(2) ?? throw new CliError("block: missing <action>");
        int? index = ParseIndex(args.Positional(3));
        int? index2 = ParseIndex(args.Positional(4));

        ctx.EnsureLoaded(file);
        string resolved = ctx.ResolvePath(path);
        var loaded = ctx.LoadedOrThrow("block");
        var field = loaded.Tag.Root.FieldPath(resolved) ?? throw new CliError($"field '{resolved}' not found");
        var block = field.AsBlock() ?? throw new CliError($"field '{resolved}' is not a block");
        int len = block.Count;

        string preview;
        switch (action)
        {
            case "count":
                Console.WriteLine(json ? $"{{\"path\":\"{resolved}\",\"count\":{len}}}" : len.ToString());
                return 0;
            case "add":
                preview = $"add element at [{len}] to {resolved}"; break;
            case "insert":
                RequireInRange(index, len, inclusive: true, "insert requires an index argument");
                preview = $"insert element at {resolved}[{index}]"; break;
            case "duplicate":
                RequireInRange(index, len, inclusive: false, "duplicate requires an index argument");
                preview = $"duplicate {resolved}[{index}] -> [{index + 1}]"; break;
            case "delete":
                RequireInRange(index, len, inclusive: false, "delete requires an index argument");
                preview = $"delete {resolved}[{index}]"; break;
            case "clear":
                preview = $"clear {resolved} ({len} elements)"; break;
            case "swap":
                RequireInRange(index, len, inclusive: false, "swap requires two index arguments");
                RequireInRange(index2, len, inclusive: false, "swap requires two index arguments");
                preview = $"swap {resolved}[{index}] <-> [{index2}]"; break;
            case "move":
                RequireInRange(index, len, inclusive: false, "move requires from and to indices");
                RequireInRange(index2, len, inclusive: false, "move requires from and to indices");
                preview = $"move {resolved}[{index}] -> [{index2}]"; break;
            default:
                throw new CliError($"invalid action '{action}' (expected count, add, insert, duplicate, delete, clear, swap, move)");
        }

        if (dryRun)
        {
            Console.WriteLine($"(dry run) would {preview}");
            return 0;
        }

        switch (action)
        {
            case "add": block.AddElement(); break;
            case "insert": block.InsertElement(index!.Value); break;
            case "duplicate": block.DuplicateElement(index!.Value); break;
            case "delete": block.DeleteElement(index!.Value); break;
            case "clear": block.Clear(); break;
            case "swap": block.SwapElements(index!.Value, index2!.Value); break;
            case "move": block.MoveElement(index!.Value, index2!.Value); break;
        }
        loaded.Dirty = true;

        var (target, redirected) = loaded.Commit(output);
        Console.WriteLine(preview);
        if (redirected) Console.WriteLine($"saved to {target}");
        return 0;
    }

    private static int? ParseIndex(string? s)
    {
        if (s is null) return null;
        if (!int.TryParse(s, out int v) || v < 0) throw new CliError($"invalid index '{s}'");
        return v;
    }

    private static void RequireInRange(int? idx, int len, bool inclusive, string missingMsg)
    {
        if (idx is null) throw new CliError(missingMsg);
        bool ok = inclusive ? idx <= len : idx < len;
        if (!ok) throw new CliError($"index {idx} out of range (block has {len} elements)");
    }
}
