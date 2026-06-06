using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>header</c> — file-level metadata (group tag, version, checksum, build,
/// size, streams) without descending into the tag body.
/// </summary>
public static class HeaderCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        bool json = args.TakeFlag("--json");
        string file = args.Positional(0) ?? throw new CliError("header: missing <file>");
        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("header");
        long fileSize = new FileInfo(loaded.Path).Length;
        var tag = loaded.Tag;
        string groupStr = GroupTag.Format(tag.Group.Tag);

        var streams = new List<string> { "tag!" };
        if (tag.DependencyList is not null) streams.Add("want");
        if (tag.ImportInfo is not null) streams.Add("info");
        if (tag.AssetDepotStorage is not null) streams.Add("assd");

        if (json)
        {
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["group"] = groupStr,
                ["group_version"] = tag.Group.Version,
                ["build"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = tag.Header.BuildVersion,
                    ["number"] = tag.Header.BuildNumber,
                },
                ["version"] = tag.Header.Version,
                ["checksum"] = $"0x{tag.Header.Checksum:X8}",
                ["file_size"] = fileSize,
                ["streams"] = new System.Text.Json.Nodes.JsonArray(streams.Select(s => (System.Text.Json.Nodes.JsonNode)s!).ToArray()),
            };
            Console.WriteLine(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("Tag File");
        Console.WriteLine($"  Group:         {groupStr}");
        Console.WriteLine($"  Group version: {tag.Group.Version}");
        Console.WriteLine($"  Build:         {tag.Header.BuildVersion}.{tag.Header.BuildNumber}");
        Console.WriteLine($"  Version:       {tag.Header.Version}");
        Console.WriteLine($"  Checksum:      0x{tag.Header.Checksum:X8}");
        Console.WriteLine($"  File size:     {fileSize} bytes");
        Console.WriteLine($"  Streams:       {string.Join(", ", streams)}");
        return 0;
    }
}
