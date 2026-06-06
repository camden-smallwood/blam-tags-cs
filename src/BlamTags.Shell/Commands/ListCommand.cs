using System.Text.RegularExpressions;
using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>list</c> — walk a directory and emit matching tag paths, or a
/// per-group tally. Path-only (the extension is the group). (JSON output: TODO.)</summary>
public static class ListCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? group = args.TakeOption("--group");
        string? startsWith = args.TakeOption("--starts-with");
        string? contains = args.TakeOption("--contains");
        string? endsWith = args.TakeOption("--ends-with");
        string? regexPat = args.TakeOption("--regex");
        string? fromFile = args.TakeOption("--from-file");
        bool summary = args.TakeFlag("--summary");
        bool sortByCount = args.TakeFlag("--sort-by-count");
        bool json = args.TakeFlag("--json");
        if (json) throw new CliError("list --json is not yet implemented");
        string dir = args.Positional(0) ?? throw new CliError("list: missing <dir>");

        Regex? regex = regexPat is null ? null : new Regex(regexPat);

        List<string> candidates;
        if (fromFile is not null)
            candidates = File.ReadAllLines(fromFile).ToList();
        else
        {
            candidates = new List<string>();
            Walk(dir, candidates);
        }

        string? groupFilter = group is null ? null : ResolveGroup(ctx, group);

        var matched = new List<(string Path, string Ext)>();
        foreach (var path in candidates)
        {
            string ext = Path.GetExtension(path);
            if (ext.Length == 0) continue;
            ext = ext[1..]; // strip leading '.'

            if (groupFilter is not null && ext != groupFilter) continue;
            string fileName = Path.GetFileName(path);
            if (startsWith is not null && !fileName.StartsWith(startsWith, StringComparison.Ordinal)) continue;
            if (contains is not null && !path.Contains(contains, StringComparison.Ordinal)) continue;
            if (endsWith is not null && !fileName.EndsWith(endsWith, StringComparison.Ordinal)) continue;
            if (regex is not null && !regex.IsMatch(path)) continue;
            matched.Add((path, ext));
        }

        matched.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Path, b.Path);
            return c != 0 ? c : string.CompareOrdinal(a.Ext, b.Ext);
        });

        if (summary)
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var (_, ext) in matched)
                counts[ext] = counts.GetValueOrDefault(ext) + 1;
            var rows = counts.Select(kv => (Group: kv.Key, Count: kv.Value)).ToList();
            if (sortByCount)
                rows = rows.OrderByDescending(r => r.Count).ToList();
            Console.WriteLine($"{"GROUP",-32} {"COUNT",8}");
            Console.WriteLine(new string('-', 44));
            long total = 0;
            foreach (var (g, count) in rows)
            {
                Console.WriteLine($"{g,-32} {count,8}");
                total += count;
            }
            Console.WriteLine(new string('-', 44));
            Console.WriteLine($"{$"{rows.Count} types",-32} {total,8}");
        }
        else
        {
            foreach (var (path, _) in matched)
                Console.WriteLine(path);
        }
        return 0;
    }

    private static string ResolveGroup(CliContext ctx, string raw)
    {
        if (ctx.TagIndex.GroupTagFor(raw) is not null) return raw;
        if (raw.Length == 4 && GroupTag.Parse(raw) is { } tag && ctx.TagIndex.NameFor(tag) is { } name)
            return name;
        return raw;
    }

    private static void Walk(string dir, List<string> outPaths)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(entry)) { Walk(entry, outPaths); continue; }
            if (Path.GetFileName(entry) == ".DS_Store") continue;
            outPaths.Add(entry);
        }
    }
}
