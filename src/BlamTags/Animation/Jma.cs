using System.Globalization;
using System.Text;

namespace BlamTags;

/// <summary>JMA-family file kind, picked from the animation's
/// <c>animation type</c> × <c>frame info type</c> × world-relative flag.</summary>
public enum JmaKind { Jmm, Jma, Jmt, Jmz, Jmo, Jmr, Jmw }

public static class JmaKindExtensions
{
    public static string Extension(this JmaKind k) => k switch
    {
        JmaKind.Jmm => "JMM", JmaKind.Jma => "JMA", JmaKind.Jmt => "JMT", JmaKind.Jmz => "JMZ",
        JmaKind.Jmo => "JMO", JmaKind.Jmr => "JMR", JmaKind.Jmw => "JMW", _ => "JMM",
    };

    /// <summary>Pick the kind from per-animation metadata. JMW = base + the
    /// world-relative bit (not a separate <c>animation type</c> value).</summary>
    public static JmaKind FromMetadata(string? animationType, string? frameInfoType, bool worldRelative)
    {
        switch (animationType ?? "base")
        {
            case "overlay": return JmaKind.Jmo;
            case "replacement": return JmaKind.Jmr;
        }
        if (worldRelative) return JmaKind.Jmw;
        return (frameInfoType ?? "none") switch
        {
            "dx,dy" => JmaKind.Jma,
            "dx,dy,dyaw" => JmaKind.Jmt,
            "dx,dy,dz,dyaw" or "dx,dy,dz,dangle_axis" => JmaKind.Jmz,
            _ => JmaKind.Jmm,
        };
    }

    public static bool FoldsMovement(this JmaKind k) => k is JmaKind.Jma or JmaKind.Jmt or JmaKind.Jmz;
    public static bool PrependsRestPose(this JmaKind k) => k is JmaKind.Jmo or JmaKind.Jmr;
    public static bool AppendsHeldFrame(this JmaKind k) => k is JmaKind.Jmm or JmaKind.Jma or JmaKind.Jmt or JmaKind.Jmz or JmaKind.Jmw;
}

/// <summary>Serializes a composed <see cref="Pose"/> into a JMA-family text
/// file (version 16392) — a port of <c>jma.rs</c>.</summary>
public static class JmaWriter
{
    public static void Write(
        Pose pose, Stream stream, Skeleton skeleton, IReadOnlyList<NodeTransform> defaults,
        int nodeListChecksum, JmaKind kind, string actorName, MovementData? movement)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = false };
        Write(w, pose, skeleton, defaults, nodeListChecksum, kind, actorName, movement);
        w.Flush();
    }

    public static string ToText(
        Pose pose, Skeleton skeleton, IReadOnlyList<NodeTransform> defaults,
        int nodeListChecksum, JmaKind kind, string actorName, MovementData? movement)
    {
        var sb = new StringBuilder();
        using var w = new StringWriter(sb);
        Write(w, pose, skeleton, defaults, nodeListChecksum, kind, actorName, movement);
        return sb.ToString();
    }

    private static void Write(
        TextWriter w, Pose pose, Skeleton skeleton, IReadOnlyList<NodeTransform> defaults,
        int nodeListChecksum, JmaKind kind, string actorName, MovementData? movement)
    {
        void L(string s) { w.Write(s); w.Write('\n'); }

        int codecCount = pose.Frames.Count;
        int totalFrames = codecCount == 0 ? 0 : codecCount + 1;

        L("16392");
        L(N(totalFrames));
        L("30");
        L("1");
        L(actorName);
        L(N(skeleton.Count));
        L(N(nodeListChecksum));

        foreach (var node in skeleton.Nodes)
        {
            L(node.Name);
            L(N(node.FirstChild));
            L(N(node.NextSibling));
        }

        if (codecCount == 0) return;

        if (kind.PrependsRestPose())
            foreach (var transform in defaults)
                WriteTransform(w, transform);

        var accumulatedTranslation = default(RealPoint3d);
        var accumulatedRotation = new RealQuaternion(0, 0, 0, 1);
        bool absolute = movement?.Kind.IsAbsolute() ?? false;

        for (int frameIdx = 0; frameIdx < pose.Frames.Count; frameIdx++)
        {
            var frame = pose.Frames[frameIdx];
            for (int boneIdx = 0; boneIdx < frame.Count; boneIdx++)
                WriteTransform(w, ComposeFrameBone(frame[boneIdx], boneIdx, accumulatedTranslation, accumulatedRotation, kind));
            // Advance AFTER writing so frame 0 holds the rest pose and
            // movement begins accumulating from frame 1 (Foundry's
            // frame-0-is-rest / zero-prepend convention; matches the Rust writer).
            if (kind.FoldsMovement() && movement is not null && frameIdx < movement.Frames.Count)
                AdvanceMovement(ref accumulatedTranslation, ref accumulatedRotation, movement.Frames[frameIdx], absolute);
        }

        if (kind.AppendsHeldFrame())
        {
            var lastFrame = pose.Frames[codecCount - 1];
            for (int boneIdx = 0; boneIdx < lastFrame.Count; boneIdx++)
                WriteTransform(w, ComposeFrameBone(lastFrame[boneIdx], boneIdx, accumulatedTranslation, accumulatedRotation, kind));
        }
    }

    // Accumulate movement as a quaternion (matching the Rust/Foundry fold):
    // rotate this frame's local delta into world space by the running
    // rotation, add it, then compose the per-frame rotation delta. For
    // absolute movement (xyz_absolute) the translation is a per-frame
    // absolute position and no rotation is accumulated.
    private static void AdvanceMovement(ref RealPoint3d translation, ref RealQuaternion accumulatedRotation, MovementFrame local, bool absolute)
    {
        if (absolute)
        {
            translation = new RealPoint3d(local.Dx, local.Dy, local.Dz);
            return;
        }
        var world = accumulatedRotation.Rotate(new RealVector3d(local.Dx, local.Dy, local.Dz));
        translation = new RealPoint3d(translation.X + world.I, translation.Y + world.J, translation.Z + world.K);
        accumulatedRotation = accumulatedRotation.Mul(local.Rotation).Normalized();
    }

    // Fold accumulated movement into the root bone for the movement-bearing
    // base kinds (JMA/JMT/JMZ). Overlay/replacement composition is done
    // upstream (Animation.OverlayPose / ReplacementPose); the writer never
    // composes overlays itself.
    private static NodeTransform ComposeFrameBone(
        NodeTransform transform, int boneIdx,
        RealPoint3d accumulatedTranslation, RealQuaternion accumulatedRotation, JmaKind kind)
    {
        var t = transform.Translation;
        var q = transform.Rotation;

        if (kind.FoldsMovement() && boneIdx == 0)
        {
            t = new RealPoint3d(t.X + accumulatedTranslation.X, t.Y + accumulatedTranslation.Y, t.Z + accumulatedTranslation.Z);
            q = accumulatedRotation.Mul(q);
        }

        return new NodeTransform { Translation = t, Rotation = q, Scale = transform.Scale };
    }

    private static void WriteTransform(TextWriter w, NodeTransform t)
    {
        var p = t.Translation;
        WriteFloats(w, [p.X * 100.0f, p.Y * 100.0f, p.Z * 100.0f]);
        var q = t.Rotation;
        WriteFloats(w, [-q.I, -q.J, -q.K, q.W]);
        WriteFloats(w, [t.Scale]);
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static void WriteFloats(TextWriter w, float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i] == 0f ? 0f : values[i];
            // Match Rust's `{:.10}`: ±inf print as `inf`/`-inf` (C#'s "F10"
            // would emit `Infinity`); NaN prints `NaN` in both.
            string s = float.IsPositiveInfinity(v) ? "inf"
                : float.IsNegativeInfinity(v) ? "-inf"
                : v.ToString("F10", CultureInfo.InvariantCulture);
            w.Write(s);
            w.Write(i + 1 < values.Length ? '\t' : '\n');
        }
    }
}
