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

        // Halo CE `model_animations` (group `antr`) predates the gen3
        // codec-pack model entirely — route it through the classic decoder.
        if (GroupTag.Format(loaded.Tag.Group.Tag) == "antr")
            return RunCe(loaded.Tag, loaded.Path, anim, output, flat);

        string? tagsRoot = TagPaths.DeriveTagsRoot(loaded.Path)
            ?? throw new CliError("failed to derive tags root from input path — input must live under a `tags/` directory");

        var (jmadTag, renderModel) = ResolveInputs(ctx, loaded.Tag, tagsRoot);
        var animation = Animation.New(jmadTag);
        if (animation.IsEmpty)
            throw new CliError($"tag has no local animations (parent: {animation.Parent ?? "none"}) — nothing to extract");

        var skeleton = Skeleton.FromTag(jmadTag);
        if (skeleton.IsEmpty) throw new CliError("jmad has no skeleton nodes — JMA export needs a skeleton");

        bool objectSpace = AdditionalNodeDataIsObjectSpace(animation);
        var defaults = BuildDefaults(skeleton, jmadTag, renderModel, objectSpace);
        // Overlay/replacement deltas are authored against a *base* pose
        // (the matching locomotion/idle stance), resolved via the
        // content/modes[] graph — see Animation.OverlayBasePose.
        var graph = AnimationGraph.FromTag(jmadTag);
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
            WriteJma(group, clip, animation, graph, skeleton, defaults, stem, destinations[i]);
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

    private static IReadOnlyList<NodeTransform> BuildDefaults(Skeleton skeleton, TagFile jmad, TagFile? renderModel, bool objectSpaceAnimData)
    {
        // Lower priority: jmad's `additional node data`, per skeleton node.
        // Reach/H4 store these in object/model space; H2/H3 parent-local.
        var animByName = new Dictionary<string, NodeTransform>();
        var addl = jmad.Root.FieldPath("additional node data")?.AsBlock();
        if (addl is not null)
            for (int i = 0; i < addl.Count; i++)
            {
                var elem = addl.Element(i);
                if (elem is null) continue;
                string? name = elem.ReadStringId("node name");
                if (string.IsNullOrEmpty(name)) continue;
                animByName[name] = new NodeTransform
                {
                    Translation = elem.ReadPoint3d("default translation"),
                    Rotation = elem.ReadQuat("default rotation"),
                    Scale = elem.ReadReal("default scale") ?? 1.0f,
                };
            }
        var anim = skeleton.Nodes
            .Select(n => animByName.TryGetValue(n.Name, out var t) ? t : NodeTransform.Identity)
            .ToList();
        // Reach/H4 `additional node data` is object-space → convert to local
        // (Foundry's world_to_local). H2/H3 are already local — leave as-is.
        if (objectSpaceAnimData)
            anim = skeleton.ObjectToLocal(anim);

        // Higher priority: render_model `nodes[]` (always parent-local).
        var rmByName = new Dictionary<string, NodeTransform>();
        var rmNodes = renderModel?.Root.FieldPath("nodes")?.AsBlock();
        if (rmNodes is not null)
            for (int i = 0; i < rmNodes.Count; i++)
            {
                var elem = rmNodes.Element(i);
                if (elem is null) continue;
                string? name = elem.ReadStringId("name");
                if (string.IsNullOrEmpty(name)) continue;
                rmByName[name] = new NodeTransform
                {
                    Translation = elem.ReadPoint3d("default translation"),
                    Rotation = elem.ReadQuat("default rotation"),
                    Scale = 1.0f,
                };
            }

        return skeleton.Nodes
            .Select((node, i) => rmByName.TryGetValue(node.Name, out var t) ? t : anim[i])
            .ToList();
    }

    /// <summary>Whether a jmad's `additional node data` rest pose is in
    /// object/model space (Reach/H4) rather than parent-local (H2/H3).
    /// Detected by the Reach-style packed-data-sizes layout.</summary>
    private static bool AdditionalNodeDataIsObjectSpace(Animation animation) =>
        animation.Groups.Any(g => g.DataSizes?.Layout() == SizeLayout.Reach);

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

    //==== Halo CE (antr) path ====

    /// <summary>Halo CE <c>model_animations</c> (antr) extraction. CE stores
    /// each animation's frames inline (no gen3 codec pack / tgrc resource),
    /// with the skeleton in the tag's own <c>nodes</c> block and the rest
    /// pose carried by the static (<c>default data</c>) stream — so CE poses
    /// are self-contained and need no render_model. Overlays/replacements
    /// compose onto the skeleton rest pose (CE has no per-graph base
    /// resolution like gen3).</summary>
    private static int RunCe(TagFile tag, string path, string? anim, string? output, bool flat)
    {
        var animations = CeAnimation.ReadAll(tag);
        if (animations.Count == 0)
            throw new CliError("model_animations has no animations to extract");
        var skeleton = Skeleton.FromTag(tag);
        if (skeleton.IsEmpty)
            throw new CliError("model_animations has no nodes — JMA export needs a skeleton");
        // Halo CE `additional node data` is parent-local (no conversion).
        var defaults = BuildDefaults(skeleton, tag, null, false);

        var target = OutputTarget.FromArgs(output);
        string stem = Path.GetFileNameWithoutExtension(path);

        List<CeAnimation> groups;
        if (anim is null) groups = animations;
        else
        {
            var g = (int.TryParse(anim, out int idx) && idx >= 0 && idx < animations.Count ? animations[idx] : null)
                ?? animations.FirstOrDefault(a => a.Name == anim)
                ?? throw new CliError($"no animation named or indexed '{anim}' (use `list-animations` to see names)");
            groups = [g];
        }

        if (target is OutputTarget.ExactFile && groups.Count > 1)
            throw new CliError($"{groups.Count} animations selected; --output as a filename only works for a single animation.");

        foreach (var group in groups)
        {
            var clip = group.Decode();
            var kind = JmaKindExtensions.FromMetadata(group.AnimationType, group.FrameInfoType, group.WorldRelative);
            string name = group.Name ?? $"anim_{group.Index}";
            string filename = $"{Sanitize(name)}.{kind.Extension()}";
            string dest = CeDestination(target, stem, filename, flat);

            Pose pose;
            IReadOnlyList<NodeTransform> leading;
            switch (kind)
            {
                case JmaKind.Jmo:
                    (leading, pose) = clip.OverlayPose(skeleton, defaults);
                    break;
                case JmaKind.Jmr:
                    pose = clip.ReplacementPose(skeleton, defaults);
                    leading = defaults;
                    break;
                default:
                    pose = clip.Pose(skeleton, defaults);
                    leading = defaults;
                    break;
            }

            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            using var fs = File.Create(dest);
            JmaWriter.Write(pose, fs, skeleton, leading, group.NodeListChecksum, kind, stem, clip.Movement);
            Console.WriteLine($"{dest}: {pose.Frames.Count} frames × {skeleton.Count} bones [{kind.Extension()}]  movement={clip.Movement.Kind}");
        }
        return 0;
    }

    private static string CeDestination(OutputTarget target, string stem, string filename, bool flat)
    {
        string flatName = $"{stem}.{filename}";
        return (target, flat) switch
        {
            (OutputTarget.ExactFile e, _) => e.Path,
            (OutputTarget.Root r, true) => Path.Combine(r.Dir, flatName),
            (OutputTarget.Root r, false) => Path.Combine(r.Dir, stem, "animations", filename),
            (OutputTarget.Default, true) => flatName,
            _ => Path.Combine(stem, "animations", filename),
        };
    }

    private static void WriteJma(AnimationGroup group, AnimationClip clip, Animation animation,
        AnimationGraph graph, Skeleton skeleton, IReadOnlyList<NodeTransform> defaults, string actorName, string path)
    {
        var kind = JmaKindFor(group);
        // Overlay/replacement codec values are deltas authored against a
        // *base* pose — the matching locomotion/idle stance, not the bind
        // pose. Resolve that base's first frame (Foundry/TagTool both do
        // this); fall back to the rest pose when no base is found.
        var baseList = (kind is JmaKind.Jmo or JmaKind.Jmr)
            ? animation.OverlayBasePose(graph, group, skeleton, defaults) ?? defaults
            : defaults;

        // (body Pose, leading frame). Overlay composition happens here so
        // the writer just emits the leading frame + body verbatim. 3D pose
        // overlays (Reach/H4) and H2 replacement anims pin object-space
        // parent nodes — re-orient them + descendants after composition
        // (no-op when ObjectSpaceParents is empty, e.g. all H3 overlays).
        Pose pose;
        IReadOnlyList<NodeTransform> leading;
        switch (kind)
        {
            case JmaKind.Jmo:
            {
                var (reference, body) = clip.OverlayPose(skeleton, baseList);
                body.ApplyObjectSpaceCorrections(reference, skeleton, baseList, group.ObjectSpaceParents);
                (pose, leading) = (body, reference);
                break;
            }
            case JmaKind.Jmr:
            {
                var body = clip.ReplacementPose(skeleton, baseList);
                var reference = new List<NodeTransform>(baseList);
                body.ApplyObjectSpaceCorrections(reference, skeleton, baseList, group.ObjectSpaceParents);
                (pose, leading) = (body, reference);
                break;
            }
            default:
                pose = clip.Pose(skeleton, defaults);
                leading = defaults;
                break;
        }

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        using var fs = File.Create(path);
        JmaWriter.Write(pose, fs, skeleton, leading, group.NodeListChecksum, kind, actorName, clip.Movement);

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

    // Re-importable filenames: `:` (the jmad state separator) → space, the
    // filesystem-illegal set + control chars → '_'; every other char (letters,
    // digits, spaces, '_', '-') is kept verbatim. Matches the Rust oracle.
    private static string Sanitize(string name) =>
        new(name.Select(c => c switch
        {
            ':' => ' ',
            '/' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' => '_',
            _ when char.IsControl(c) => '_',
            _ => c,
        }).ToArray());

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
