namespace BlamTags;

/// <summary>One node in the jmad's <c>skeleton nodes</c> block.</summary>
public readonly record struct SkeletonNode(string Name, short FirstChild, short NextSibling, short Parent);

/// <summary>jmad skeleton — the bone hierarchy animations target.</summary>
public sealed class Skeleton
{
    public List<SkeletonNode> Nodes { get; init; } = new();

    private static readonly string[] TopLevelNames = ["definitions", "resources"];

    public static Skeleton FromTag(TagFile tag)
    {
        var root = tag.Root;
        foreach (var prefix in TopLevelNames)
        {
            var block = root.FieldPath($"{prefix}/skeleton nodes")?.AsBlock();
            if (block is null) continue;
            var nodes = new List<SkeletonNode>(block.Count);
            for (int i = 0; i < block.Count; i++)
            {
                var elem = block.Element(i);
                if (elem is null) continue;
                nodes.Add(new SkeletonNode(
                    elem.ReadStringId("name") ?? "",
                    elem.ReadBlockIndex("first child node index"),
                    elem.ReadBlockIndex("next sibling node index"),
                    elem.ReadBlockIndex("parent node index")));
            }
            return new Skeleton { Nodes = nodes };
        }
        // Halo CE `model_animations` (antr): the skeleton is a root-level
        // `nodes` block whose fields drop the `index` suffix (`first child
        // node` / `next sibling node` / `parent node`) and store the name
        // as an inline string rather than a string-id.
        var ceBlock = root.FieldPath("nodes")?.AsBlock();
        if (ceBlock is not null)
        {
            var nodes = new List<SkeletonNode>(ceBlock.Count);
            for (int i = 0; i < ceBlock.Count; i++)
            {
                var elem = ceBlock.Element(i);
                if (elem is null) continue;
                short Idx(string n) => (short)(elem.ReadIntAny(n) ?? -1);
                nodes.Add(new SkeletonNode(
                    elem.ReadString("name") ?? elem.ReadStringId("name") ?? "",
                    Idx("first child node"),
                    Idx("next sibling node"),
                    Idx("parent node")));
            }
            return new Skeleton { Nodes = nodes };
        }
        return new Skeleton();
    }

    public int Count => Nodes.Count;
    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>Convert per-node OBJECT/model-space transforms into parent-LOCAL
    /// transforms (<c>local = parent_object⁻¹ · node_object</c>). Reach and
    /// Halo 4 store the rest pose (<c>additional node data</c>) in object space;
    /// the JMA family wants parent-local, so the caller converts before using
    /// them as defaults (Foundry's <c>world_to_local</c>). <paramref name="obj"/>
    /// must align with <see cref="Nodes"/>; nodes assume parent-before-child.</summary>
    public List<NodeTransform> ObjectToLocal(IReadOnlyList<NodeTransform> obj)
    {
        var world = new Matrix4[obj.Count];
        for (int i = 0; i < obj.Count; i++)
            world[i] = Matrix4.FromLocRotScale(obj[i].Translation, obj[i].Rotation, obj[i].Scale);
        var result = new List<NodeTransform>(Nodes.Count);
        for (int i = 0; i < Nodes.Count; i++)
        {
            int p = Nodes[i].Parent;
            var local = p >= 0 && p < world.Length ? world[p].Inverse() * world[i] : world[i];
            var (t, r, s) = local.Decompose();
            result.Add(new NodeTransform { Translation = t, Rotation = r, Scale = s });
        }
        return result;
    }
}

/// <summary>One bone's transform at one frame.</summary>
public struct NodeTransform
{
    public RealQuaternion Rotation;
    public RealPoint3d Translation;
    public float Scale;

    public static readonly NodeTransform Identity = new()
    {
        Rotation = new RealQuaternion(0, 0, 0, 1),
        Translation = default,
        Scale = 1.0f,
    };
}

/// <summary>One <c>object-space parent nodes</c> entry — a node whose
/// object-space orientation is pinned to a fixed transform (Reach/H4 3D
/// pose overlays, and some H2 replacement anims). Port of the Rust
/// <c>ObjectSpaceParentNode</c>.</summary>
public readonly record struct ObjectSpaceParentNode(
    short NodeIndex, RealPoint3d Translation, RealQuaternion Rotation, float Scale);

/// <summary>Per-frame, per-bone transform table — <c>Frames[frame][bone]</c>.</summary>
public sealed class Pose
{
    public List<List<NodeTransform>> Frames { get; init; } = new();

    /// <summary>Apply object-space parent-node corrections to this pose's
    /// frames and the leading <paramref name="reference"/> frame —
    /// Foundry's <c>_apply_object_space_base_corrections</c>, used for
    /// Reach/H4 3D pose overlays and H2 replacement anims. No-op when
    /// <paramref name="targets"/> is empty. Port of the Rust
    /// <c>Pose::apply_object_space_corrections</c>.</summary>
    public void ApplyObjectSpaceCorrections(
        List<NodeTransform> reference, Skeleton skeleton,
        IReadOnlyList<NodeTransform> @base, IReadOnlyList<ObjectSpaceParentNode> targets)
    {
        if (targets.Count == 0) return;
        int n = skeleton.Count;

        // (target_index, desired object-space matrix), shallow-to-deep.
        var corrections = new List<(int Index, Matrix4 Matrix)>();
        foreach (var t in targets)
        {
            if (t.NodeIndex < 0 || t.NodeIndex >= n) continue;
            int targetIndex = ObjectSpaceTargetIndex(t.NodeIndex, skeleton);
            corrections.Add((targetIndex, Matrix4.FromLocRotScale(t.Translation, t.Rotation, t.Scale)));
        }
        corrections.Sort((x, y) => NodeDepth(x.Index, skeleton).CompareTo(NodeDepth(y.Index, skeleton)));

        // Running base copy used to derive each target's delta (Foundry's
        // `reference_frame`) — corrected in lock-step so compounding matches.
        var correctionRef = new List<NodeTransform>(@base);
        while (correctionRef.Count < n) correctionRef.Add(NodeTransform.Identity);

        foreach (var (targetIndex, targetMatrix) in corrections)
        {
            var os = FrameObjectSpaceMatrices(correctionRef, skeleton);
            var delta = targetMatrix * os[targetIndex].Inverse();
            var descendants = DescendantIndices(targetIndex, skeleton);

            ApplyDeltaToFrame(correctionRef, skeleton, descendants, delta);
            ApplyDeltaToFrame(reference, skeleton, descendants, delta);
            foreach (var frame in Frames)
                ApplyDeltaToFrame(frame, skeleton, descendants, delta);
        }
    }

    // The node an object-space parent entry actually re-orients: the
    // targeted node's parent, or the node itself when it is a root.
    private static int ObjectSpaceTargetIndex(int nodeIndex, Skeleton skeleton)
    {
        int parent = skeleton.Nodes[nodeIndex].Parent;
        return parent < 0 || parent >= skeleton.Count ? nodeIndex : parent;
    }

    // Depth of `idx` in the hierarchy (root = 1).
    private static int NodeDepth(int idx, Skeleton skeleton)
    {
        int n = skeleton.Count, depth = 0, guard = 0;
        while (idx >= 0 && idx < n)
        {
            depth++;
            int parent = skeleton.Nodes[idx].Parent;
            if (parent < 0 || parent >= n || parent == idx) break;
            idx = parent;
            if (++guard > n) break;
        }
        return depth;
    }

    // All nodes whose parent-chain reaches `target` (including `target`),
    // shallow-to-deep.
    private static List<int> DescendantIndices(int target, Skeleton skeleton)
    {
        int n = skeleton.Count;
        var outList = new List<int>();
        for (int node = 0; node < n; node++)
        {
            int cur = node, guard = 0;
            while (true)
            {
                if (cur == target) { outList.Add(node); break; }
                int parent = skeleton.Nodes[cur].Parent;
                if (parent < 0 || parent >= n || parent == cur) break;
                cur = parent;
                if (++guard > n) break;
            }
        }
        outList.Sort((a, b) => NodeDepth(a, skeleton).CompareTo(NodeDepth(b, skeleton)));
        return outList;
    }

    // Forward-kinematic object-space matrix per bone: os[node] = os[parent] * local(node).
    private static Matrix4[] FrameObjectSpaceMatrices(IReadOnlyList<NodeTransform> frame, Skeleton skeleton)
    {
        int n = skeleton.Count;
        var os = new Matrix4?[n];
        for (int i = 0; i < n; i++) ResolveOs(i, frame, skeleton, os);
        var result = new Matrix4[n];
        for (int i = 0; i < n; i++) result[i] = os[i] ?? Matrix4.Identity;
        return result;
    }

    private static Matrix4 ResolveOs(int i, IReadOnlyList<NodeTransform> frame, Skeleton skeleton, Matrix4?[] os)
    {
        if (os[i] is { } cached) return cached;
        var t = i < frame.Count ? frame[i] : NodeTransform.Identity;
        var local = Matrix4.FromLocRotScale(t.Translation, t.Rotation, t.Scale);
        int parent = skeleton.Nodes[i].Parent;
        var m = parent >= 0 && parent < skeleton.Count && parent != i
            ? ResolveOs(parent, frame, skeleton, os) * local
            : local;
        os[i] = m;
        return m;
    }

    private static void ApplyDeltaToFrame(List<NodeTransform> frame, Skeleton skeleton, List<int> nodeIndices, Matrix4 delta)
    {
        var os = FrameObjectSpaceMatrices(frame, skeleton);
        foreach (int node in nodeIndices)
            if (node < os.Length) os[node] = delta * os[node];
        foreach (int node in nodeIndices)
        {
            if (node >= frame.Count) continue;
            int parent = skeleton.Nodes[node].Parent;
            var local = parent >= 0 && parent < os.Length
                ? os[parent].Inverse() * os[node]
                : os[node];
            var (translation, rotation, scale) = local.Decompose();
            frame[node] = new NodeTransform { Translation = translation, Rotation = rotation, Scale = scale };
        }
    }
}

/// <summary>Composes a decoded <see cref="AnimationClip"/> against a skeleton
/// via the per-component node-flag bitarrays — a port of <c>pose.rs</c>.</summary>
internal static class PoseComposer
{
    private enum SourceKind { Static, Animated, Identity }
    private readonly record struct TrackSource(SourceKind Kind, int Index);
    private readonly record struct BoneResolution(TrackSource Rotation, TrackSource Translation, TrackSource Scale);

    public static Pose Compose(AnimationClip clip, Skeleton skeleton, IReadOnlyList<NodeTransform>? defaults)
    {
        int bones = skeleton.Count;
        int framesN = System.Math.Max(1, (int)clip.FrameCount);
        var frames = new List<List<NodeTransform>>(framesN);

        var resolutions = new BoneResolution[bones];
        for (int b = 0; b < bones; b++) resolutions[b] = ResolveBone(b, clip.NodeFlags);

        for (int f = 0; f < framesN; f++)
        {
            var row = new List<NodeTransform>(bones);
            for (int b = 0; b < bones; b++)
            {
                var res = resolutions[b];
                var def = defaults is not null && b < defaults.Count ? defaults[b] : NodeTransform.Identity;
                var rotation = PickRotation(clip, res, f) ?? def.Rotation;
                var translation = PickTranslation(clip, res, f) ?? def.Translation;
                var scale = PickScale(clip, res, f) ?? def.Scale;
                row.Add(new NodeTransform { Rotation = rotation, Translation = translation, Scale = scale });
            }
            frames.Add(row);
        }
        return new Pose { Frames = frames };
    }

    /// <summary>Compose an <b>overlay</b> (delta) animation onto a base/
    /// rest pose, matching Foundry's <c>compose_overlay_animation</c> and
    /// the Rust <c>overlay_pose</c>. Returns the per-bone composition base
    /// (<c>Reference</c> — static-track value where static-flagged, else
    /// <paramref name="base"/>; this is also the leading frame the writer
    /// prepends) and the <c>frame_count</c> composed body frames. Animated
    /// components apply their per-frame delta on top of the reference
    /// (translation additive, rotation <c>reference * delta</c>, scale
    /// <b>multiplicative</b>); every other component holds the reference.</summary>
    public static (List<NodeTransform> Reference, Pose Body) OverlayPose(
        AnimationClip clip, Skeleton skeleton, IReadOnlyList<NodeTransform> @base)
    {
        int bones = skeleton.Count;
        int framesN = System.Math.Max(1, (int)clip.FrameCount);

        var resolutions = new BoneResolution[bones];
        for (int b = 0; b < bones; b++) resolutions[b] = ResolveBone(b, clip.NodeFlags);

        var reference = new List<NodeTransform>(bones);
        for (int b = 0; b < bones; b++)
        {
            var res = resolutions[b];
            var baseB = b < @base.Count ? @base[b] : NodeTransform.Identity;
            reference.Add(new NodeTransform
            {
                Rotation = res.Rotation.Kind == SourceKind.Static ? PickRotation(clip, res, 0) ?? baseB.Rotation : baseB.Rotation,
                Translation = res.Translation.Kind == SourceKind.Static ? PickTranslation(clip, res, 0) ?? baseB.Translation : baseB.Translation,
                Scale = res.Scale.Kind == SourceKind.Static ? PickScale(clip, res, 0) ?? baseB.Scale : baseB.Scale,
            });
        }

        var frames = new List<List<NodeTransform>>(framesN);
        for (int f = 0; f < framesN; f++)
        {
            var row = new List<NodeTransform>(bones);
            for (int b = 0; b < bones; b++)
            {
                var res = resolutions[b];
                var r = reference[b];
                var rotation = res.Rotation.Kind == SourceKind.Animated
                    ? r.Rotation.Mul(PickRotation(clip, res, f) ?? new RealQuaternion(0, 0, 0, 1))
                    : r.Rotation;
                RealPoint3d translation;
                if (res.Translation.Kind == SourceKind.Animated)
                {
                    var d = PickTranslation(clip, res, f) ?? default;
                    translation = new RealPoint3d(r.Translation.X + d.X, r.Translation.Y + d.Y, r.Translation.Z + d.Z);
                }
                else translation = r.Translation;
                var scale = res.Scale.Kind == SourceKind.Animated ? r.Scale * (PickScale(clip, res, f) ?? 1.0f) : r.Scale;
                row.Add(new NodeTransform { Rotation = rotation, Translation = translation, Scale = scale });
            }
            frames.Add(row);
        }

        return (reference, new Pose { Frames = frames });
    }

    /// <summary>Compose a <b>replacement</b> animation against a base/rest
    /// pose, matching Foundry's <c>compose_replacement_animation</c> and
    /// the Rust <c>replacement_pose</c>. Only <b>animated</b>-flagged
    /// components take the codec value (a full pose, not a delta); every
    /// other component — including <i>static</i>-flagged ones — takes the
    /// <paramref name="base"/> (rest) value.</summary>
    public static Pose ReplacementPose(AnimationClip clip, Skeleton skeleton, IReadOnlyList<NodeTransform> @base)
    {
        int bones = skeleton.Count;
        int framesN = System.Math.Max(1, (int)clip.FrameCount);

        var resolutions = new BoneResolution[bones];
        for (int b = 0; b < bones; b++) resolutions[b] = ResolveBone(b, clip.NodeFlags);

        var frames = new List<List<NodeTransform>>(framesN);
        for (int f = 0; f < framesN; f++)
        {
            var row = new List<NodeTransform>(bones);
            for (int b = 0; b < bones; b++)
            {
                var res = resolutions[b];
                var baseB = b < @base.Count ? @base[b] : NodeTransform.Identity;
                var rotation = res.Rotation.Kind == SourceKind.Animated ? PickRotation(clip, res, f) ?? baseB.Rotation : baseB.Rotation;
                var translation = res.Translation.Kind == SourceKind.Animated ? PickTranslation(clip, res, f) ?? baseB.Translation : baseB.Translation;
                var scale = res.Scale.Kind == SourceKind.Animated ? PickScale(clip, res, f) ?? baseB.Scale : baseB.Scale;
                row.Add(new NodeTransform { Rotation = rotation, Translation = translation, Scale = scale });
            }
            frames.Add(row);
        }
        return new Pose { Frames = frames };
    }

    private static BoneResolution ResolveBone(int bone, NodeFlags? flags)
    {
        if (flags is null)
            return new BoneResolution(
                new TrackSource(SourceKind.Static, bone),
                new TrackSource(SourceKind.Static, bone),
                new TrackSource(SourceKind.Static, bone));
        return new BoneResolution(
            PickSource(bone, flags.StaticRotation, flags.AnimatedRotation),
            PickSource(bone, flags.StaticTranslation, flags.AnimatedTranslation),
            PickSource(bone, flags.StaticScale, flags.AnimatedScale));
    }

    private static TrackSource PickSource(int bone, BitArray staticFlags, BitArray animatedFlags)
    {
        if (staticFlags.Bit(bone)) return new TrackSource(SourceKind.Static, staticFlags.PopcountBelow(bone));
        if (animatedFlags.Bit(bone)) return new TrackSource(SourceKind.Animated, animatedFlags.PopcountBelow(bone));
        return new TrackSource(SourceKind.Identity, 0);
    }

    private static RealQuaternion? PickRotation(AnimationClip clip, BoneResolution res, int frame) => res.Rotation.Kind switch
    {
        SourceKind.Static => res.Rotation.Index < clip.StaticTracks.Rotations.Count && clip.StaticTracks.Rotations[res.Rotation.Index].Count > 0
            ? clip.StaticTracks.Rotations[res.Rotation.Index][0] : null,
        SourceKind.Animated => AnimatedAt(clip.AnimatedTracks?.Rotations, res.Rotation.Index, frame),
        _ => null,
    };

    private static RealPoint3d? PickTranslation(AnimationClip clip, BoneResolution res, int frame) => res.Translation.Kind switch
    {
        SourceKind.Static => res.Translation.Index < clip.StaticTracks.Translations.Count && clip.StaticTracks.Translations[res.Translation.Index].Count > 0
            ? clip.StaticTracks.Translations[res.Translation.Index][0] : null,
        SourceKind.Animated => AnimatedAt(clip.AnimatedTracks?.Translations, res.Translation.Index, frame),
        _ => null,
    };

    private static float? PickScale(AnimationClip clip, BoneResolution res, int frame) => res.Scale.Kind switch
    {
        SourceKind.Static => res.Scale.Index < clip.StaticTracks.Scales.Count && clip.StaticTracks.Scales[res.Scale.Index].Count > 0
            ? clip.StaticTracks.Scales[res.Scale.Index][0] : null,
        SourceKind.Animated => AnimatedScaleAt(clip.AnimatedTracks?.Scales, res.Scale.Index, frame),
        _ => null,
    };

    private static T? AnimatedAt<T>(List<List<T>>? tracks, int index, int frame) where T : struct
    {
        if (tracks is null || index >= tracks.Count) return null;
        var f = tracks[index];
        if (f.Count == 0) return null;
        return f[System.Math.Min(frame, f.Count - 1)];
    }

    private static float? AnimatedScaleAt(List<List<float>>? tracks, int index, int frame)
    {
        if (tracks is null || index >= tracks.Count) return null;
        var f = tracks[index];
        if (f.Count == 0) return null;
        return f[System.Math.Min(frame, f.Count - 1)];
    }
}
