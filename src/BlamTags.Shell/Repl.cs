using System.Text;

namespace BlamTags.Shell;

/// <summary>
/// Interactive shell: one tag load, N edits, one save. Tag-bound commands
/// omit the file argument (the loaded path is injected); a nav stack scopes
/// paths. REPL-only verbs (open/close/save/revert/cd/back/exit-to/pwd/
/// help/exit) are handled directly. Mirrors the Rust <c>repl.rs</c>.
/// </summary>
public static class Repl
{
    public static int Run(CliContext ctx, string? initialTag)
    {
        if (initialTag is not null) ctx.Load(initialTag);
        ctx.ReplMode = true;

        Console.WriteLine("blam-tag-shell REPL — type `help` for commands, Ctrl-D to exit");

        while (true)
        {
            Console.Write(Prompt(ctx));
            string? line = Console.ReadLine();
            if (line is null) // EOF
            {
                if (ctx.Loaded?.Dirty == true) Console.Error.WriteLine("\nwarning: exiting with unsaved changes");
                else Console.WriteLine();
                break;
            }
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                if (HandleLine(ctx, trimmed)) break; // exit requested
            }
            catch (CliError e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
            }
        }
        return 0;
    }

    /// <returns>true to exit the REPL.</returns>
    private static bool HandleLine(CliContext ctx, string line)
    {
        var words = ShlexSplit(line) ?? throw new CliError("unbalanced quotes");
        if (words.Count == 0) return false;

        string verb = words[0];
        var rest = words.Skip(1).ToList();

        switch (verb)
        {
            case "exit" or "quit":
                if (rest.Count > 0 && rest[0] == "--force") return true;
                return ConfirmDiscardDirty(ctx, "exit");
            case "help" or "?":
                PrintHelp(ctx);
                return false;
            case "open":
                ReplOpen(ctx, rest);
                return false;
            case "close":
                ReplClose(ctx);
                return false;
            case "save":
                ReplSave(ctx, rest);
                return false;
            case "revert":
                ReplRevert(ctx);
                return false;
            case "edit-block" or "cd":
                ReplEditBlock(ctx, rest);
                return false;
            case "back":
                if (ctx.Nav.Count == 0) throw new CliError("already at the tag root");
                ctx.Nav.RemoveAt(ctx.Nav.Count - 1);
                return false;
            case "exit-to":
                ReplExitTo(ctx, rest);
                return false;
            case "pwd":
                Console.WriteLine(ctx.Nav.Count == 0 ? "/" : "/" + string.Join('/', ctx.Nav));
                return false;
            case "repl":
                throw new CliError("already in a REPL");
            default:
                DispatchVerb(ctx, verb, rest);
                return false;
        }
    }

    private static void DispatchVerb(CliContext ctx, string verb, List<string> rest)
    {
        // Tag-bound verbs take the loaded path as their first positional;
        // inject it so the user doesn't retype it.
        var argList = Cli.IsTagBound(verb) && ctx.Loaded is not null
            ? new List<string> { ctx.Loaded.Path }.Concat(rest).ToList()
            : rest;
        Cli.Dispatch(ctx, verb, new Args(argList));
    }

    private static void ReplOpen(CliContext ctx, List<string> rest)
    {
        if (rest.Count == 0) throw new CliError("usage: open <tag-path>");
        if (!ConfirmDiscardDirty(ctx, "open")) return;
        ctx.Load(rest[0]);
    }

    private static void ReplClose(CliContext ctx)
    {
        if (ctx.Loaded is null) throw new CliError("no tag loaded");
        if (!ConfirmDiscardDirty(ctx, "close")) return;
        ctx.Loaded = null;
        ctx.Nav.Clear();
    }

    private static void ReplSave(CliContext ctx, List<string> rest)
    {
        var loaded = ctx.LoadedOrThrow("save");
        string target = loaded.Save(rest.Count > 0 ? rest[0] : null);
        Console.WriteLine($"saved to {target}");
    }

    private static void ReplRevert(CliContext ctx)
    {
        var loaded = ctx.LoadedOrThrow("revert");
        ctx.Load(loaded.Path);
        Console.WriteLine("reverted to on-disk contents");
    }

    private static void ReplEditBlock(CliContext ctx, List<string> rest)
    {
        if (rest.Count == 0) throw new CliError("usage: edit-block <path>");
        string target = rest[0];
        bool absolute = target.StartsWith('/');
        string body = absolute ? target[1..] : target;
        var newSegments = body.Split('/').Where(s => s.Length != 0).ToList();
        if (newSegments.Count == 0) throw new CliError("empty edit-block target");

        var prospective = absolute ? newSegments : ctx.Nav.Concat(newSegments).ToList();
        string prospectivePath = string.Join('/', prospective);

        var root = ctx.LoadedOrThrow("edit-block").Tag.Root;
        var field = root.FieldPath(prospectivePath) ?? throw new CliError($"field '{prospectivePath}' not found");
        bool navigable = field.AsStruct() is not null || field.AsBlock() is not null
            || field.AsArray() is not null || field.AsResource()?.AsStruct() is not null;
        if (!navigable)
            throw new CliError($"field '{prospectivePath}' is not a navigable struct / block / array / pageable_resource");

        ctx.Nav.Clear();
        ctx.Nav.AddRange(prospective);
    }

    private static void ReplExitTo(CliContext ctx, List<string> rest)
    {
        if (rest.Count == 0) throw new CliError("usage: exit-to <segment|root|tag>");
        string target = rest[0];
        if (target is "root" or "tag" or "/")
        {
            ctx.Nav.Clear();
            return;
        }
        var saved = new List<string>(ctx.Nav);
        while (saved.Count > 0)
        {
            string last = saved[^1];
            if (last == target || last.Contains(target, StringComparison.Ordinal))
            {
                ctx.Nav.Clear();
                ctx.Nav.AddRange(saved);
                return;
            }
            saved.RemoveAt(saved.Count - 1);
        }
        throw new CliError($"no nav segment matching '{target}'");
    }

    private static string Prompt(CliContext ctx)
    {
        string game = ctx.Game ?? "blam";
        if (ctx.Loaded is null) return $"{game}> ";
        string name = Path.GetFileName(ctx.Loaded.Path);
        string dirty = ctx.Loaded.Dirty ? "*" : "";
        return ctx.Nav.Count == 0
            ? $"{game} :: {name}{dirty}> "
            : $"{game} :: {name}{dirty}/{string.Join('/', ctx.Nav)}> ";
    }

    private static bool ConfirmDiscardDirty(CliContext ctx, string action)
    {
        if (ctx.Loaded is not { Dirty: true } loaded) return true;
        Console.Write($"`{Path.GetFileName(loaded.Path)}` has unsaved changes. {action} anyway? [y/N] ");
        string? line = Console.ReadLine();
        return line?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static void PrintHelp(CliContext ctx)
    {
        Console.WriteLine("Session verbs:");
        Console.WriteLine("  open <path>         load a tag");
        Console.WriteLine("  close               close the current tag");
        Console.WriteLine("  save [path]         write the current tag (to `path` or back to the source)");
        Console.WriteLine("  revert              reload the current tag from disk, discarding edits");
        Console.WriteLine("  help                show this message");
        Console.WriteLine("  exit / quit         leave the REPL");
        Console.WriteLine();
        Console.WriteLine("Navigation:");
        Console.WriteLine("  edit-block <path>   push a sub-struct / block-element / array-element onto nav");
        Console.WriteLine("                      (aliased `cd`; leading `/` resets to absolute)");
        Console.WriteLine("  back                pop one level");
        Console.WriteLine("  exit-to <name>      pop until the named segment is the tail; `exit-to root` clears");
        Console.WriteLine("  pwd                 show the current nav path");
        Console.WriteLine();
        Console.WriteLine("Tag commands (omit the file arg, interpreted relative to nav):");
        Console.WriteLine("  header, inspect, get, set, flag, options, deps, export, check");
        Console.WriteLine();
        Console.WriteLine("Directory / corpus commands:");
        Console.WriteLine("  list <dir> [...]        walk a directory for tags");
        Console.WriteLine("  find <dir> <value>      deep value search");
        Console.WriteLine("  layout-diff <a> <b>     compare two tags' schemas");
        Console.WriteLine("  data-diff <a> <b>       compare two tags' values");
        Console.WriteLine();
        if (ctx.Loaded is { } l)
        {
            Console.WriteLine($"Loaded: {l.Path} ({(l.Dirty ? "unsaved changes" : "clean")})");
            if (ctx.Nav.Count != 0) Console.WriteLine($"Nav:    /{string.Join('/', ctx.Nav)}");
        }
        else
        {
            Console.WriteLine("No tag loaded.");
        }
    }

    /// <summary>POSIX-ish word split with single/double quotes and backslash
    /// escapes. Returns null on unbalanced quotes (mirrors shlex::split).</summary>
    private static List<string>? ShlexSplit(string line)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        bool inWord = false;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) { if (inWord) { words.Add(cur.ToString()); cur.Clear(); inWord = false; } i++; continue; }
            inWord = true;
            if (c == '\'')
            {
                i++;
                while (i < line.Length && line[i] != '\'') cur.Append(line[i++]);
                if (i >= line.Length) return null; // unbalanced
                i++;
            }
            else if (c == '"')
            {
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length && (line[i + 1] is '"' or '\\' or '$' or '`'))
                    { cur.Append(line[i + 1]); i += 2; }
                    else cur.Append(line[i++]);
                }
                if (i >= line.Length) return null; // unbalanced
                i++;
            }
            else if (c == '\\')
            {
                if (i + 1 < line.Length) { cur.Append(line[i + 1]); i += 2; }
                else return null;
            }
            else { cur.Append(c); i++; }
        }
        if (inWord) words.Add(cur.ToString());
        return words;
    }
}
