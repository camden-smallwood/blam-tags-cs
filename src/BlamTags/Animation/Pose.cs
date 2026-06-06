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
        return new Skeleton();
    }

    public int Count => Nodes.Count;
    public bool IsEmpty => Nodes.Count == 0;
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

/// <summary>Per-frame, per-bone transform table — <c>Frames[frame][bone]</c>.</summary>
public sealed class Pose
{
    public List<List<NodeTransform>> Frames { get; init; } = new();
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
