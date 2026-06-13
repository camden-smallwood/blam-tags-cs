using System.Buffers.Binary;

namespace BlamTags;

/// <summary>Halo CE <c>model_animations</c> (group <c>antr</c>) animation
/// decode — a port of the Rust <c>animation/classic.rs</c>.
///
/// CE predates the gen3 codec-pack model: each animation in the root-level
/// <c>animations</c> block stores its frames as two raw blobs — <c>default
/// data</c> (static, shared) and <c>frame data</c> (<c>frame count</c>
/// consecutive frames of <c>frame size</c> bytes) — plus three 64-bit
/// node-flag masks. A flag bit decides per component (rotation/translation/
/// scale) whether that component is animated (set → per-frame) or static
/// (clear → once from <c>default data</c>). Components are node-major in
/// rotation → translation → scale order. When the <c>compressed data</c>
/// flag is set, <c>frame data</c> holds a keyframe-compressed block at
/// <c>offset to compressed data</c> (6-byte quaternions).</summary>
public sealed class CeAnimation
{
    /// <summary>Engine dequantization factor for 16-bit rotation components
    /// (× 0.000030518509, ≈ 1/32767).</summary>
    private const float RotScale = 0.000030518509f;

    public int Index { get; init; }
    public string? Name { get; init; }
    public string? AnimationType { get; init; }
    public string? FrameInfoType { get; init; }
    public bool WorldRelative { get; init; }
    public ushort FrameCount { get; init; }
    public int NodeCount { get; init; }
    public int NodeListChecksum { get; init; }
    private int FrameSize { get; init; }
    private bool Compressed { get; init; }
    private int CompressedOffset { get; init; }
    private bool Be { get; init; }
    private ulong RotationMask { get; init; }
    private ulong TranslationMask { get; init; }
    private ulong ScaleMask { get; init; }
    private byte[] DefaultData { get; init; } = [];
    private byte[] FrameData { get; init; } = [];
    private byte[] FrameInfo { get; init; } = [];

    /// <summary>Walk the root <c>animations</c> block of a Halo CE
    /// <c>model_animations</c> tag. Returns an empty list when there is no
    /// <c>animations</c> block.</summary>
    public static List<CeAnimation> ReadAll(TagFile tag)
    {
        var root = tag.Root;
        bool tagBe = tag.Endian == Endian.Be;
        var block = root.FieldPath("animations")?.AsBlock();
        var outList = new List<CeAnimation>(block?.Count ?? 0);
        if (block is null) return outList;
        for (int i = 0; i < block.Count; i++)
        {
            var e = block.Element(i);
            if (e is null) continue;
            int nodeCount = (int)System.Math.Max(0, e.ReadIntAny("node count") ?? 0);
            uint flags = (uint)(e.ReadIntAny("flags") ?? 0);
            bool compressed = (flags & 1) == 1;
            int compressedOffset = (int)System.Math.Max(0, e.ReadIntAny("offset to compressed data") ?? 0);
            byte[] frameData = e.Field("frame data")?.AsData() ?? [];
            outList.Add(new CeAnimation
            {
                Index = i,
                Name = e.ReadString("name") ?? e.ReadStringId("name"),
                AnimationType = e.ReadEnumName("type"),
                FrameInfoType = e.ReadEnumName("frame info type") is { } fi ? NormalizeFrameInfo(fi) : null,
                WorldRelative = ((flags >> 1) & 1) == 1,
                FrameCount = (ushort)System.Math.Max(0, e.ReadIntAny("frame count") ?? 0),
                NodeCount = nodeCount,
                NodeListChecksum = (int)(e.ReadIntAny("node list checksum") ?? 0),
                FrameSize = (int)System.Math.Max(0, e.ReadIntAny("frame size") ?? 0),
                Compressed = compressed,
                CompressedOffset = compressedOffset,
                Be = DetectBe(tagBe, compressed, frameData, compressedOffset),
                RotationMask = ReadFlagMask(e, "node rotation flag data"),
                TranslationMask = ReadFlagMask(e, "node transform flag data"),
                ScaleMask = ReadFlagMask(e, "node scale flag data"),
                DefaultData = e.Field("default data")?.AsData() ?? [],
                FrameData = frameData,
                FrameInfo = e.Field("frame info")?.AsData() ?? [],
            });
        }
        return outList;
    }

    /// <summary>Decode into the shared <see cref="AnimationClip"/>.</summary>
    public AnimationClip Decode() => Compressed ? DecodeCompressed() : DecodeUncompressed();

    private NodeFlags BuildNodeFlags()
    {
        ulong full = NodeCount >= 64 ? ulong.MaxValue : (1UL << NodeCount) - 1;
        (BitArray Anim, BitArray Static) Mk(ulong m) =>
            (BitArray.FromU64(m & full), BitArray.FromU64(~m & full));
        var (ar, sr) = Mk(RotationMask);
        var (at, st) = Mk(TranslationMask);
        var (asc, ssc) = Mk(ScaleMask);
        return new NodeFlags
        {
            StaticRotation = sr, StaticTranslation = st, StaticScale = ssc,
            AnimatedRotation = ar, AnimatedTranslation = at, AnimatedScale = asc,
        };
    }

    private AnimationClip DecodeUncompressed()
    {
        int frames = System.Math.Max(1, (int)FrameCount);
        int nAr = System.Numerics.BitOperations.PopCount(RotationMask);
        int nAt = System.Numerics.BitOperations.PopCount(TranslationMask);
        int nAsc = System.Numerics.BitOperations.PopCount(ScaleMask);
        bool be = Be;

        // Static tracks: walk `default data` once, node-major, appending the
        // components whose flag is CLEAR (rotation→translation→scale).
        var sRot = new List<RealQuaternion>();
        var sTrn = new List<RealPoint3d>();
        var sScl = new List<float>();
        int off = 0;
        for (int node = 0; node < NodeCount; node++)
        {
            if (!Bit(RotationMask, node)) sRot.Add(ReadQuat(DefaultData, ref off, be));
            if (!Bit(TranslationMask, node)) sTrn.Add(ReadPoint(DefaultData, ref off, be));
            if (!Bit(ScaleMask, node)) sScl.Add(ReadF32(DefaultData, ref off, be));
        }

        // Animated tracks: per frame, walk that frame's slice node-major,
        // appending the SET components.
        var aRot = NewTracks<RealQuaternion>(nAr, frames, new RealQuaternion(0, 0, 0, 1));
        var aTrn = NewTracks<RealPoint3d>(nAt, frames, default);
        var aScl = NewTracks<float>(nAsc, frames, 1.0f);
        for (int f = 0; f < frames; f++)
        {
            int fo = f * FrameSize;
            int ri = 0, ti = 0, si = 0;
            for (int node = 0; node < NodeCount; node++)
            {
                if (Bit(RotationMask, node)) { aRot[ri][f] = ReadQuat(FrameData, ref fo, be); ri++; }
                if (Bit(TranslationMask, node)) { aTrn[ti][f] = ReadPoint(FrameData, ref fo, be); ti++; }
                if (Bit(ScaleMask, node)) { aScl[si][f] = ReadF32(FrameData, ref fo, be); si++; }
            }
        }

        bool hasAnimated = nAr + nAt + nAsc > 0;
        return new AnimationClip
        {
            FrameCount = (ushort)frames,
            StaticTracks = new AnimationTracks
            {
                Codec = Codec.UncompressedStatic, FrameCount = 1,
                Rotations = VecOf(sRot), Translations = VecOf(sTrn), Scales = VecOf(sScl),
            },
            AnimatedTracks = hasAnimated ? new AnimationTracks
            {
                Codec = Codec.UncompressedAnimated, FrameCount = (ushort)frames,
                Rotations = aRot, Translations = aTrn, Scales = aScl,
            } : null,
            AnimatedStatus = hasAnimated ? new AnimatedStreamStatus.Decoded() : new AnimatedStreamStatus.NoAnimatedStream(),
            NodeFlags = BuildNodeFlags(),
            Movement = BuildMovement(),
        };
    }

    /// <summary>Per-frame root movement from the <c>frame info</c> blob.</summary>
    private MovementData BuildMovement()
    {
        var kind = FrameInfoType is null ? MovementKind.None : MovementKindExtensions.FromSchemaName(FrameInfoType);
        int bpf = kind.BytesPerFrame();
        if (bpf == 0 || FrameInfo.Length < bpf) return new MovementData();
        bool be = Be;
        int frames = System.Math.Min(FrameInfo.Length / bpf, System.Math.Max(1, (int)FrameCount));
        var outFrames = new List<MovementFrame>(frames);
        for (int f = 0; f < frames; f++)
        {
            int o = f * bpf;
            float dx = ReadF32(FrameInfo, ref o, be);
            float dy = ReadF32(FrameInfo, ref o, be);
            var frame = new MovementFrame { Dx = dx, Dy = dy };
            switch (kind)
            {
                case MovementKind.DxDyDyaw:
                    frame.Rotation = YawQuat(ReadF32(FrameInfo, ref o, be)); break;
                case MovementKind.DxDyDzDyaw:
                    frame.Dz = ReadF32(FrameInfo, ref o, be);
                    frame.Rotation = YawQuat(ReadF32(FrameInfo, ref o, be)); break;
            }
            outFrames.Add(frame);
        }
        return new MovementData { Kind = kind, Frames = outFrames };
    }

    /// <summary>Decode the keyframe-compressed block (6-byte quaternions /
    /// real_point3d translations / f32 scales, each with a per-node keyframe
    /// table) — port of the Rust <c>decode_compressed</c>.</summary>
    private AnimationClip DecodeCompressed()
    {
        int frames = System.Math.Max(1, (int)FrameCount);
        bool be = Be;
        byte[] blk = CompressedOffset <= FrameData.Length ? FrameData[CompressedOffset..] : [];
        int Tbl(int i) => (int)ReadDword(blk, i * 4, be);
        int n = NodeCount;

        var rot = new List<List<RealQuaternion>>();
        var trn = new List<List<RealPoint3d>>();
        var scl = new List<List<float>>();
        ulong rm = 0, tm = 0, sm = 0;

        for (int node = 0; node < n; node++)
        {
            // Rotation: header at dword 11 + node.
            uint hdr = ReadDword(blk, (11 + node) * 4, be);
            int kf = (int)(hdr & 0xFFF), ko = (int)(hdr >> 12);
            if (DecodeKeyframeTrack(blk, kf, Tbl(0) + 2 * ko, Tbl(2) + 6 * ko, frames, 6, be,
                    (b, o) => ReadQuat6(b, o, be)) is { } rTrack)
            { rot.Add(rTrack); rm |= 1UL << node; }
            // Translation: header at byte tbl(3) + 4*node.
            hdr = ReadDword(blk, Tbl(3) + 4 * node, be);
            kf = (int)(hdr & 0xFFF); ko = (int)(hdr >> 12);
            if (DecodeKeyframeTrack(blk, kf, Tbl(4) + 2 * ko, Tbl(6) + 12 * ko, frames, 12, be,
                    (b, o) => { int t = o; return ReadPoint(b, ref t, be); }) is { } tTrack)
            { trn.Add(tTrack); tm |= 1UL << node; }
            // Scale: header at byte tbl(7) + 4*node.
            hdr = ReadDword(blk, Tbl(7) + 4 * node, be);
            kf = (int)(hdr & 0xFFF); ko = (int)(hdr >> 12);
            if (DecodeKeyframeTrack(blk, kf, Tbl(8) + 2 * ko, Tbl(10) + 4 * ko, frames, 4, be,
                    (b, o) => { int t = o; return ReadF32(b, ref t, be); }) is { } sTrack)
            { scl.Add(sTrack); sm |= 1UL << node; }
        }

        var empty = new BitArray();
        return new AnimationClip
        {
            FrameCount = (ushort)frames,
            StaticTracks = new AnimationTracks { Codec = Codec.UncompressedStatic, FrameCount = 1 },
            AnimatedTracks = new AnimationTracks
            {
                Codec = Codec.UncompressedAnimated, FrameCount = (ushort)frames,
                Rotations = rot, Translations = trn, Scales = scl,
            },
            AnimatedStatus = new AnimatedStreamStatus.Decoded(),
            NodeFlags = new NodeFlags
            {
                StaticRotation = empty, StaticTranslation = empty, StaticScale = empty,
                AnimatedRotation = BitArray.FromU64(rm),
                AnimatedTranslation = BitArray.FromU64(tm),
                AnimatedScale = BitArray.FromU64(sm),
            },
            Movement = BuildMovement(),
        };
    }

    /// <summary>Decode one node's compressed keyframe track to a per-frame
    /// value vector, or <c>null</c> when the component is absent (computed
    /// offsets fall outside the block). Between keyframes the value is held
    /// from the last keyframe at or before the frame.</summary>
    private static List<T>? DecodeKeyframeTrack<T>(
        byte[] blk, int kf, int indexOff, int valueOff, int frames, int stride, bool be, Func<byte[], int, T> read)
    {
        if (kf == 0 || kf > frames) return null;
        if (indexOff + 2 * kf > blk.Length || valueOff + stride * kf > blk.Length) return null;
        int KeyIndex(int k) => ReadWord(blk, indexOff + 2 * k, be);
        T KeyVal(int k) => read(blk, valueOff + stride * k);
        var outList = new List<T>(frames);
        for (int f = 0; f < frames; f++)
        {
            int k = 0;
            while (k + 1 < kf && KeyIndex(k + 1) <= f) k++;
            outList.Add(KeyVal(System.Math.Min(k, kf - 1)));
        }
        return outList;
    }

    //==== helpers ====

    // Read both long_integers of a CE flag field (low dword = nodes 0–31,
    // high dword = nodes 32–63). The two dwords share the same field name,
    // so collect them by iterating fields.
    private static ulong ReadFlagMask(TagStruct elem, string name)
    {
        var words = new List<ulong>();
        foreach (var f in elem.Fields())
        {
            if (f.Name != name) continue;
            if (f.Value is { } v && Animation.IntValue(v) is { } iv)
                words.Add((uint)iv);
        }
        ulong lo = words.Count > 0 ? words[0] : 0;
        ulong hi = words.Count > 1 ? words[1] : 0;
        return lo | (hi << 32);
    }

    private static string NormalizeFrameInfo(string name) => name switch
    {
        "dx dy" => "dx,dy",
        "dx dy dyaw" => "dx,dy,dyaw",
        "dx dy dz dyaw" => "dx,dy,dz,dyaw",
        _ => name,
    };

    private static bool Bit(ulong mask, int node) => node < 64 && ((mask >> node) & 1) == 1;

    private static List<List<T>> VecOf<T>(List<T> v)
    {
        var outList = new List<List<T>>(v.Count);
        foreach (var x in v) outList.Add([x]);
        return outList;
    }

    private static List<List<T>> NewTracks<T>(int count, int frames, T fill)
    {
        var outList = new List<List<T>>(count);
        for (int i = 0; i < count; i++)
        {
            var f = new List<T>(frames);
            for (int j = 0; j < frames; j++) f.Add(fill);
            outList.Add(f);
        }
        return outList;
    }

    private static RealQuaternion YawQuat(float yaw)
    {
        float h = yaw * 0.5f;
        return new RealQuaternion(0f, 0f, (float)System.Math.Sin(h), (float)System.Math.Cos(h));
    }

    // Decide whether an animation's raw blobs are big-endian. Uncompressed
    // blobs follow the tag's structured endianness; a compressed block can
    // disagree (PC-authored Digsite content is LE in a BE tag). Bounds-check
    // the non-tag_be reading of the offset table's first three entries.
    private static bool DetectBe(bool tagBe, bool compressed, byte[] frameData, int compressedOffset)
    {
        if (!compressed) return tagBe;
        byte[] blk = compressedOffset <= frameData.Length ? frameData[compressedOffset..] : [];
        int n = blk.Length;
        if (n < 12) return tagBe;
        bool other = !tagBe;
        int Off(int i) => (int)ReadDword(blk, i * 4, other);
        bool otherInBounds = Off(0) < n && Off(1) < n && Off(2) < n;
        return otherInBounds ? other : tagBe;
    }

    //==== endian-parametric blob readers ====

    private static RealQuaternion ReadQuat(byte[] b, ref int off, bool be)
    {
        var q = new RealQuaternion(
            I16E(b, off, be) * RotScale,
            I16E(b, off + 2, be) * RotScale,
            I16E(b, off + 4, be) * RotScale,
            I16E(b, off + 6, be) * RotScale);
        off += 8;
        return System.MathF.Sqrt(q.LengthSquared()) <= 1e-6f ? new RealQuaternion(0, 0, 0, 1) : q.Normalized();
    }

    private static RealPoint3d ReadPoint(byte[] b, ref int off, bool be)
    {
        var p = new RealPoint3d(F32E(b, off, be), F32E(b, off + 4, be), F32E(b, off + 8, be));
        off += 12;
        return p;
    }

    private static float ReadF32(byte[] b, ref int off, bool be)
    {
        float v = F32E(b, off, be);
        off += 4;
        return v;
    }

    // 6-byte compressed quaternion: 4×12-bit signed components packed into
    // three 16-bit words, scaled by RotScale. `o` is a byte offset.
    private static RealQuaternion ReadQuat6(byte[] b, int o, bool be)
    {
        uint iiij = U16E(b, o, be);
        uint jjkk = U16E(b, o + 2, be);
        uint kwww = U16E(b, o + 4, be);
        static float Sx(uint v)
        {
            v &= 0xFFF;
            return (v & 0x800) != 0 ? (int)(v | 0xFFFFF000) : (int)v;
        }
        float i = Sx(iiij >> 4);
        float j = Sx(((iiij & 0xF) << 8) | (jjkk >> 8));
        float k = Sx(((jjkk & 0xFF) << 4) | (kwww >> 12));
        float w = Sx(kwww & 0xFFF);
        var q = new RealQuaternion(i * RotScale, j * RotScale, k * RotScale, w * RotScale);
        return System.MathF.Sqrt(q.LengthSquared()) <= 1e-6f ? new RealQuaternion(0, 0, 0, 1) : q.Normalized();
    }

    private static short I16E(byte[] b, int o, bool be) =>
        o + 2 <= b.Length ? (be ? BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o, 2)) : BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2))) : (short)0;
    private static ushort U16E(byte[] b, int o, bool be) =>
        o + 2 <= b.Length ? (be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2))) : (ushort)0;
    private static float F32E(byte[] b, int o, bool be) =>
        o + 4 <= b.Length ? (be ? BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o, 4)) : BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4))) : 0.0f;
    private static ushort ReadWord(byte[] b, int o, bool be) => U16E(b, o, be);
    private static uint ReadDword(byte[] b, int o, bool be) =>
        o + 4 <= b.Length ? (be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4))) : 0u;
}
