using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>extract-animation</c> — decode animations from a
/// <c>.model_animation_graph</c>, the bundle <c>.model</c> (hlmt) that owns
/// one, or any object-inheriting tag (.biped, .vehicle, .scenery, …) that
/// points at a .model. Emits JMA-family text (<c>.JMM/.JMA/.JMT/.JMZ/.JMO/
/// .JMR/.JMW</c>), kind picked from the animation's metadata. The optional
/// <c>&lt;anim&gt;</c> selects one animation by index or name; omitted = all.
/// (The Rust verb's <c>--format json</c> diagnostic dump isn't ported.)
/// </summary>
public static class ExtractAnimationCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        bool flat = args.TakeFlag("--flat");
        string? format = args.TakeOption("--format");
        if (format is not null && !format.Equals("jma", StringComparison.OrdinalIgnoreCase))
            throw new CliError($"--format `{format}` not supported (only `jma` is ported)");

        string file = args.Positional(0) ?? throw new CliError("extract-animation: missing <file>");
        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("extract-animation");
        // After the injected/explicit file, an optional <anim> selector.
        string? anim = (ctx.ReplMode ? args.Positionals.FirstOrDefault() : args.Positionals.Skip(1).FirstOrDefault());

        string? tagsRoot = TagPaths.DeriveTagsRoot(loaded.Path)
            ?? throw new CliError("failed to derive tags root from input path — input must live under a `tags/` directory");

        var (jmadTag, renderModel) = ResolveInputs(ctx, loaded.Tag, tagsRoot);
        var animation = Animation.New(jmadTag);
        if (animation.IsEmpty)
            throw new CliError($"tag has no local animations (parent: {animation.Parent ?? "none"}) — nothing to extract");

        var skeleton = Skeleton.FromTag(jmadTag);
        if (skeleton.IsEmpty) throw new CliError("jmad has no skeleton nodes — JMA export needs a skeleton");

        var defaults = BuildDefaults(skeleton, jmadTag, renderModel);
        string stem = Path.GetFileNameWithoutExtension(loaded.Path);

        var groups = anim is null
            ? animation.Groups.ToList()
            : new List<AnimationGroup> { PickAnimation(animation, anim) };

        var target = OutputTarget.FromArgs(output);
        if (target is OutputTarget.ExactFile && groups.Count > 1)
            throw new CliError($"{groups.Count} animations selected; --output as a filename only works for a single animation.");

        // Resolve all destinations up front + detect post-sanitize collisions.
        var destinations = groups.Select(g => ResolveDestination(target, stem, g, flat)).ToList();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < destinations.Count; i++)
        {
            if (seen.TryGetValue(destinations[i], out int j))
                throw new CliError(
                    $"two animations resolve to the same output file `{destinations[i]}`: " +
                    $"[{groups[j].Index}] '{DisplayName(groups[j])}' and [{groups[i].Index}] '{DisplayName(groups[i])}'.");
            seen[destinations[i]] = i;
        }

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            AnimationClip clip;
            try { clip = group.Decode(); }
            catch (AnimationException e) { throw new CliError($"decode animation '{DisplayName(group)}': {e.Message}"); }
            WriteJma(group, clip, skeleton, defaults, stem, destinations[i]);
        }
        return 0;
    }

    //==== input resolution ====

    private static (TagFile Jmad, TagFile? RenderModel) ResolveInputs(CliContext ctx, TagFile tag, string tagsRoot)
    {
        string group = GroupTag.Format(tag.Group.Tag);
        switch (group)
        {
            case "jmad": return (tag, null);
            case "hlmt": return ResolveFromModel(ctx, tag, tagsRoot);
            default:
                string modelRel = FindObjectModelRef(tag)
                    ?? throw new CliError(
                        $"input group `{group}` has no `model` ref — pass a .model_animation_graph, a .model, or an object-inheriting tag");
                var modelTag = ReadTag(TagPaths.ResolveTagPath(tagsRoot, modelRel, "model"));
                return ResolveFromModel(ctx, modelTag, tagsRoot);
        }
    }

    private static (TagFile Jmad, TagFile? RenderModel) ResolveFromModel(CliContext ctx, TagFile modelTag, string tagsRoot)
    {
        string jmadRel = TagRef(modelTag.Root, "animation")
            ?? throw new CliError("`.model` has no `animation` ref — nothing to extract");
        var jmad = ReadTag(TagPaths.ResolveTagPath(tagsRoot, jmadRel, "model_animation_graph"));

        TagFile? renderModel = null;
        if (TagRef(modelTag.Root, "render model") is { } renderRel)
            renderModel = ReadTag(TagPaths.ResolveTagPath(tagsRoot, renderRel, "render_model"));
        return (jmad, renderModel);
    }

    private static string? FindObjectModelRef(TagFile tag)
    {
        string[] paths = ["unit/object/model", "item/object/model", "device/object/model", "object/model"];
        var root = tag.Root;
        foreach (var p in paths)
            if (root.FieldPath(p)?.Value is TagFieldData.TagReference tr
                && tr.Value.GroupTagAndName is { } gan && !string.IsNullOrEmpty(gan.Name))
                return gan.Name;
        return null;
    }

    private static IReadOnlyList<NodeTransform> BuildDefaults(Skeleton skeleton, TagFile jmad, TagFile? renderModel)
    {
        var byName = new Dictionary<string, NodeTransform>();

        var addl = jmad.Root.FieldPath("additional node data")?.AsBlock();
        if (addl is not null)
            for (int i = 0; i < addl.Count; i++)
            {
                var elem = addl.Element(i);
                if (elem is null) continue;
                string? name = elem.ReadStringId("node name");
                if (string.IsNullOrEmpty(name)) continue;
                byName[name] = new NodeTransform
                {
                    Translation = elem.ReadPoint3d("default translation"),
                    Rotation = elem.ReadQuat("default rotation"),
                    Scale = elem.ReadReal("default scale") ?? 1.0f,
                };
            }

        var rmNodes = renderModel?.Root.FieldPath("nodes")?.AsBlock();
        if (rmNodes is not null)
            for (int i = 0; i < rmNodes.Count; i++)
            {
                var elem = rmNodes.Element(i);
                if (elem is null) continue;
                string? name = elem.ReadStringId("name");
                if (string.IsNullOrEmpty(name)) continue;
                byName[name] = new NodeTransform
                {
                    Translation = elem.ReadPoint3d("default translation"),
                    Rotation = elem.ReadQuat("default rotation"),
                    Scale = 1.0f,
                };
            }

        return skeleton.Nodes
            .Select(node => byName.TryGetValue(node.Name, out var t) ? t : NodeTransform.Identity)
            .ToList();
    }

    //==== output ====

    private abstract record OutputTarget
    {
        public sealed record Root(string Dir) : OutputTarget;
        public sealed record ExactFile(string Path) : OutputTarget;
        public sealed record Default : OutputTarget;

        public static OutputTarget FromArgs(string? output)
        {
            if (output is null) return new Default();
            bool trailingSlash = output.EndsWith('/') || output.EndsWith(Path.DirectorySeparatorChar);
            if (trailingSlash || Directory.Exists(output)) return new Root(output);
            return HasKnownExtension(output) ? new ExactFile(output) : new Root(output);
        }
    }

    private static bool HasKnownExtension(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "jmm" or "jma" or "jmt" or "jmz" or "jmo" or "jmr" or "jmw" or "json";
    }

    private static string ResolveDestination(OutputTarget target, string stem, AnimationGroup group, bool flat)
    {
        string ext = JmaKindFor(group).Extension();
        string nested = $"{DefaultName(group)}.{ext}";
        string flatName = $"{stem}.{nested}";
        return (target, flat) switch
        {
            (OutputTarget.ExactFile e, _) => e.Path,
            (OutputTarget.Root r, true) => Path.Combine(r.Dir, flatName),
            (OutputTarget.Root r, false) => Path.Combine(r.Dir, stem, "animations", nested),
            (OutputTarget.Default, true) => flatName,
            _ => Path.Combine(stem, "animations", nested),
        };
    }

    private static JmaKind JmaKindFor(AnimationGroup g) =>
        JmaKindExtensions.FromMetadata(g.AnimationType, g.FrameInfoType, g.WorldRelative);

    private static void WriteJma(AnimationGroup group, AnimationClip clip, Skeleton skeleton,
        IReadOnlyList<NodeTransform> defaults, string actorName, string path)
    {
        var kind = JmaKindFor(group);
        // Overlay anims store deltas-from-rest; unflagged bones must stay at
        // identity so compose(rest × delta) produces the rest pose.
        var poseDefaults = kind.ComposesOverlay() ? null : defaults;
        var pose = clip.Pose(skeleton, poseDefaults);

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        using var fs = File.Create(path);
        JmaWriter.Write(pose, fs, skeleton, defaults, group.NodeListChecksum, kind, actorName, clip.Movement);

        int codecCount = clip.FrameCount;
        Console.WriteLine($"{path}: {codecCount + 1} frames ({codecCount}+1) × {skeleton.Count} bones [{kind.Extension()}]  movement={clip.Movement.Kind}");
    }

    //==== helpers ====

    private static AnimationGroup PickAnimation(Animation animation, string anim)
    {
        if (int.TryParse(anim, out int index))
            return animation.Get(index) ?? throw new CliError($"animation index {index} out of range (have {animation.Count})");
        return animation.Find(anim) ?? throw new CliError($"no animation named '{anim}'");
    }

    private static string DefaultName(AnimationGroup group) =>
        group.Name is { } n ? Sanitize(n) : $"anim_{group.Index}";

    private static string DisplayName(AnimationGroup group) => group.Name ?? $"[{group.Index}]";

    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

    private static string? TagRef(TagStruct root, string field)
    {
        string? p = root.ReadTagRefPath(field);
        return string.IsNullOrEmpty(p) ? null : p;
    }

    private static TagFile ReadTag(string path)
    {
        try { return TagFile.Read(path); }
        catch (Exception e) { throw new CliError($"failed to read {path}: {e.Message}"); }
    }
}
