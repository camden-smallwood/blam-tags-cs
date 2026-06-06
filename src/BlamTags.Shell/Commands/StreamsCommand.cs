namespace BlamTags.Shell.Commands;

/// <summary>Optional-stream management verbs: add/remove/rebuild the
/// <c>want</c> / <c>info</c> / <c>assd</c> streams. Load-modify-save.</summary>
public static class StreamsCommand
{
    public static int Run(CliContext ctx, string verb, Args args)
    {
        string? output = args.TakeOption("--output");
        string file = args.Positional(0) ?? throw new CliError($"{verb}: missing <file>");
        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow(verb);

        string message;
        switch (verb)
        {
            case "add-dependency-list":
                loaded.Tag.AddDependencyList(RequireSchema(ctx, "tag_dependency_list", "dependency-list"));
                message = "attached empty dependency-list stream";
                break;
            case "remove-dependency-list":
                loaded.Tag.RemoveDependencyList();
                message = "removed dependency-list stream";
                break;
            case "rebuild-dependency-list":
                loaded.Tag.RebuildDependencyList(RequireSchema(ctx, "tag_dependency_list", "dependency-list"));
                int count = loaded.Tag.DependencyList?.FieldPath("dependencies")?.AsBlock()?.Count ?? 0;
                message = $"rebuilt dependency-list ({count} entries)";
                break;
            case "add-import-info":
                loaded.Tag.AddImportInfo(RequireSchema(ctx, "tag_import_information", "import-info"));
                message = "attached empty import-info stream";
                break;
            case "remove-import-info":
                loaded.Tag.RemoveImportInfo();
                message = "removed import-info stream";
                break;
            case "add-asset-depot-storage":
                loaded.Tag.AddAssetDepotStorage(RequireSchema(ctx, "asset_depot_storage", "asset-depot-storage"));
                message = "attached empty asset-depot-storage stream";
                break;
            case "remove-asset-depot-storage":
                loaded.Tag.RemoveAssetDepotStorage();
                message = "removed asset-depot-storage stream";
                break;
            default:
                throw new CliError($"unknown stream verb {verb}");
        }

        loaded.Dirty = true;
        var (target, redirected) = loaded.Commit(output);
        Console.WriteLine(message);
        if (redirected) Console.WriteLine($"saved to {target}");
        return 0;
    }

    private static string RequireSchema(CliContext ctx, string stem, string kind)
    {
        string game = ctx.RequireGame(kind);
        string path = ctx.SchemaPath(stem);
        if (!File.Exists(path))
            throw new CliError($"{kind} schema not found at {path} (check that definitions/{game}/ is populated)");
        return path;
    }
}
