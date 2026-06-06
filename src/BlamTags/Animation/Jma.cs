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
    public static bool ComposesOverlay(this JmaKind k) => k is JmaKind.Jmo;
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
        float accumulatedYaw = 0.0f;

        for (int frameIdx = 0; frameIdx < pose.Frames.Count; frameIdx++)
        {
            if (kind.FoldsMovement())
            {
                var local = movement is not null && frameIdx < movement.Frames.Count ? movement.Frames[frameIdx] : default;
                AdvanceMovement(ref accumulatedTranslation, ref accumulatedYaw, local);
            }
            var frame = pose.Frames[frameIdx];
            for (int boneIdx = 0; boneIdx < frame.Count; boneIdx++)
                WriteTransform(w, ComposeFrameBone(frame[boneIdx], boneIdx, defaults, accumulatedTranslation, accumulatedYaw, kind));
        }

        if (kind.AppendsHeldFrame())
        {
            var lastFrame = pose.Frames[codecCount - 1];
            for (int boneIdx = 0; boneIdx < lastFrame.Count; boneIdx++)
                WriteTransform(w, ComposeFrameBone(lastFrame[boneIdx], boneIdx, defaults, accumulatedTranslation, accumulatedYaw, kind));
        }
    }

    // Rust's `f32::sin/cos` (movement-yaw fold) round to the correctly-rounded
    // f32, which `MathF.Sin/Cos` (a faster approximation) miss by ~1 ULP.
    // Computing in double and narrowing reproduces the correctly-rounded result
    // and matches the oracle on all but a handful of exact-boundary yaw values.
    private static float SinF(float x) => (float)System.Math.Sin(x);
    private static float CosF(float x) => (float)System.Math.Cos(x);

    private static void AdvanceMovement(ref RealPoint3d translation, ref float accumulatedYaw, MovementFrame local)
    {
        float cosY = CosF(accumulatedYaw), sinY = SinF(accumulatedYaw);
        float worldDx = local.Dx * cosY - local.Dy * sinY;
        float worldDy = local.Dx * sinY + local.Dy * cosY;
        translation = new RealPoint3d(translation.X + worldDx, translation.Y + worldDy, translation.Z + local.Dz);
        accumulatedYaw += local.Dyaw;
    }

    private static NodeTransform ComposeFrameBone(
        NodeTransform transform, int boneIdx, IReadOnlyList<NodeTransform> defaults,
        RealPoint3d accumulatedTranslation, float accumulatedYaw, JmaKind kind)
    {
        var t = transform.Translation;
        var q = transform.Rotation;
        var s = transform.Scale;

        if (kind.ComposesOverlay() && boneIdx < defaults.Count)
        {
            var bse = defaults[boneIdx];
            t = new RealPoint3d(
                bse.Translation.X + transform.Translation.X,
                bse.Translation.Y + transform.Translation.Y,
                bse.Translation.Z + transform.Translation.Z);
            q = bse.Rotation.Mul(transform.Rotation);
            s = bse.Scale + transform.Scale;
        }

        if (kind.FoldsMovement() && boneIdx == 0)
        {
            t = new RealPoint3d(t.X + accumulatedTranslation.X, t.Y + accumulatedTranslation.Y, t.Z + accumulatedTranslation.Z);
            q = YawQuat(accumulatedYaw).Mul(q);
        }

        return new NodeTransform { Translation = t, Rotation = q, Scale = s };
    }

    private static RealQuaternion YawQuat(float yaw)
    {
        float half = yaw * 0.5f;
        return new RealQuaternion(0.0f, 0.0f, SinF(half), CosF(half));
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
            w.Write(v.ToString("F10", CultureInfo.InvariantCulture));
            w.Write(i + 1 < values.Length ? '\t' : '\n');
        }
    }
}
