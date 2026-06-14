using System.Buffers.Binary;

namespace BlamTags;

/// <summary>Animation codec selector — the first byte of every animation's
/// codec stream. Slots 0..=8 are Halo 3; 9..=11 added Reach onward.</summary>
public enum Codec : byte
{
    NoCompression = 0,
    UncompressedStatic = 1,
    UncompressedAnimated = 2,
    EightByteQuantizedRotationOnly = 3,
    ByteKeyframeLightlyQuantized = 4,
    WordKeyframeLightlyQuantized = 5,
    ReverseByteKeyframeLightlyQuantized = 6,
    ReverseWordKeyframeLightlyQuantized = 7,
    BlendScreen = 8,
    Curve = 9,
    RevisedCurve = 10,
    SharedStatic = 11,
}

/// <summary>Codec dispatch + per-slot decoders for jmad animation blobs — a
/// port of the Rust <c>animation::codec</c> module.</summary>
public static class AnimationCodec
{
    public static Codec? FromByte(byte b) => b <= 11 ? (Codec)b : null;

    //==== headers ====

    private readonly record struct CodecHeader(
        byte TotalRotatedNodes, byte TotalTranslatedNodes, byte TotalScaledNodes,
        uint TranslationOffset, uint ScaleOffset)
    {
        public const int Size = 20;
        public static CodecHeader? FromBytes(ReadOnlySpan<byte> b)
        {
            if (b.Length < Size) return null;
            return new CodecHeader(b[1], b[2], b[3],
                BinaryPrimitives.ReadUInt32LittleEndian(b[12..16]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[16..20]));
        }
    }

    private readonly record struct FullframeHeader(
        CodecHeader Base, uint RotationStride, uint TranslationStride, uint ScaleStride)
    {
        public const int Size = 32;
        public static FullframeHeader? FromBytes(ReadOnlySpan<byte> b)
        {
            if (b.Length < Size || CodecHeader.FromBytes(b) is not { } bse) return null;
            return new FullframeHeader(bse,
                BinaryPrimitives.ReadUInt32LittleEndian(b[20..24]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[24..28]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[28..32]));
        }
    }

    private readonly record struct KeyframeHeader(
        CodecHeader Base, uint RotTimeOff, uint TransTimeOff, uint ScaleTimeOff,
        uint RotPayloadOff, uint TransPayloadOff, uint ScalePayloadOff)
    {
        public const int Size = 48;
        public static KeyframeHeader? FromBytes(ReadOnlySpan<byte> b)
        {
            if (b.Length < Size || CodecHeader.FromBytes(b) is not { } bse) return null;
            return new KeyframeHeader(bse,
                BinaryPrimitives.ReadUInt32LittleEndian(b[20..24]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[24..28]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[28..32]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[32..36]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[36..40]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[40..44]));
        }
    }

    //==== top-level decode ====

    public static AnimationClip Decode(AnimationGroup group)
    {
        byte[] blob = group.Blob;
        byte codecByte = group.CodecByte ?? throw new AnimationException("animation has no codec payload (empty blob)");
        Codec codec = FromByte(codecByte) ?? throw new AnimationException($"unknown codec byte 0x{codecByte:x2} (expected 0..=11)");

        int staticFirstSize = (int)(group.DataSizes?.Fields.FirstOrDefault().Value ?? 0);
        bool hasStatic = codec == Codec.UncompressedStatic && staticFirstSize > 0;
        AnimationTracks staticTracks = hasStatic
            ? DecodeUncompressedStatic(blob)
            // Halo 4: the static rest pose is a SharedStatic (codec 11) stream
            // of int16 indices into the graph-level shared pool.
            : DecodeSharedStatic(group) ?? new AnimationTracks { Codec = Codec.UncompressedStatic, FrameCount = 1 };

        ushort frameCount = (ushort)System.Math.Max(1, (int)(group.CodecFrameCount ?? group.FrameCount));
        SizeLayout layout = group.DataSizes?.Layout() ?? SizeLayout.H3;

        int staticSize = !hasStatic ? 0 : layout switch
        {
            SizeLayout.H3 => (int)(group.DataSizes?.Get("default_data") ?? 0),
            _ => staticFirstSize,
        };
        int animatedOffset = staticSize;
        int animatedBlobLen = layout switch
        {
            SizeLayout.Reach or SizeLayout.Halo2 => (int)(group.DataSizes is { } d && d.Fields.Count > 1 ? d.Fields[1].Value : 0),
            _ => System.Math.Max(0, blob.Length - animatedOffset),
        };

        AnimationTracks? animatedTracks = null;
        AnimatedStreamStatus animatedStatus = new AnimatedStreamStatus.NoAnimatedStream();
        int? animatedCodecSize = null;
        if (animatedOffset < blob.Length && animatedBlobLen != 0)
        {
            int animEnd = System.Math.Min(animatedOffset + animatedBlobLen, blob.Length);
            byte[] animBlob = blob[animatedOffset..animEnd];
            byte animByte = animBlob[0];
            (animatedTracks, animatedStatus) = FromByte(animByte) switch
            {
                null => (null, new AnimatedStreamStatus.Unknown(animByte)),
                Codec.EightByteQuantizedRotationOnly =>
                    TryAnimated(Codec.EightByteQuantizedRotationOnly, () => DecodeFullframe(animBlob, Codec.EightByteQuantizedRotationOnly, frameCount, true)),
                Codec.UncompressedAnimated =>
                    TryAnimated(Codec.UncompressedAnimated, () => DecodeFullframe(animBlob, Codec.UncompressedAnimated, frameCount, false)),
                Codec.BlendScreen =>
                    TryAnimated(Codec.BlendScreen, () => DecodeFullframe(animBlob, Codec.BlendScreen, frameCount, false)),
                Codec.ByteKeyframeLightlyQuantized =>
                    TryAnimated(Codec.ByteKeyframeLightlyQuantized, () => DecodeKeyframe(animBlob, Codec.ByteKeyframeLightlyQuantized, frameCount, 1)),
                Codec.ReverseByteKeyframeLightlyQuantized =>
                    TryAnimated(Codec.ReverseByteKeyframeLightlyQuantized, () => DecodeKeyframe(animBlob, Codec.ReverseByteKeyframeLightlyQuantized, frameCount, 1)),
                Codec.WordKeyframeLightlyQuantized =>
                    TryAnimated(Codec.WordKeyframeLightlyQuantized, () => DecodeKeyframe(animBlob, Codec.WordKeyframeLightlyQuantized, frameCount, 2)),
                Codec.ReverseWordKeyframeLightlyQuantized =>
                    TryAnimated(Codec.ReverseWordKeyframeLightlyQuantized, () => DecodeKeyframe(animBlob, Codec.ReverseWordKeyframeLightlyQuantized, frameCount, 2)),
                Codec.Curve =>
                    TryAnimated(Codec.Curve, () => DecodeCurve(animBlob, Codec.Curve, frameCount, false)),
                Codec.RevisedCurve =>
                    TryAnimated(Codec.RevisedCurve, () => DecodeCurve(animBlob, Codec.RevisedCurve, frameCount, true)),
                var other => (null, new AnimatedStreamStatus.Unsupported(other.Value)),
            };
            animatedCodecSize = layout switch
            {
                SizeLayout.Reach or SizeLayout.Halo2 => group.DataSizes is { } d && d.Fields.Count > 1 ? (int)d.Fields[1].Value : null,
                _ => animatedStatus is AnimatedStreamStatus.Decoded && FromByte(animByte) is { } c
                    ? AnimatedCodecStreamSize(animBlob, c) : null,
            };
        }

        NodeFlags? nodeFlags = null;
        if (group.DataSizes is { } ds)
        {
            int off; int staticTotal; int animatedTotal;
            if (layout is SizeLayout.Reach or SizeLayout.Halo2)
            {
                off = (int)ds.Fields.Take(2).Sum(f => f.Value);
                staticTotal = (int)(ds.Fields.Count > 2 ? ds.Fields[2].Value : 0);
                animatedTotal = (int)(ds.Fields.Count > 3 ? ds.Fields[3].Value : 0);
                nodeFlags = ReadNodeFlags(blob, off, staticTotal, animatedTotal);
            }
            else if (animatedCodecSize is { } acs)
            {
                off = staticSize + acs;
                staticTotal = (int)ds.Get("static_node_flags");
                animatedTotal = (int)ds.Get("animated_node_flags");
                nodeFlags = ReadNodeFlags(blob, off, staticTotal, animatedTotal);
            }
        }

        MovementData movement = new();
        if (group.DataSizes is { } ds2)
        {
            int off, size;
            if (layout is SizeLayout.Reach or SizeLayout.Halo2)
            {
                off = (int)ds2.Fields.Take(4).Sum(f => f.Value);
                size = (int)(ds2.Fields.Count > 4 ? ds2.Fields[4].Value : 0);
            }
            else
            {
                size = (int)ds2.Get("movement_data");
                off = staticSize + (animatedCodecSize ?? 0)
                    + (int)ds2.Get("static_node_flags") + (int)ds2.Get("animated_node_flags");
            }
            movement = ReadMovementAt(blob, off, size,
                group.MovementType ?? group.FrameInfoType, frameCount);
        }

        return new AnimationClip
        {
            FrameCount = frameCount,
            StaticTracks = staticTracks,
            AnimatedTracks = animatedTracks,
            AnimatedStatus = animatedStatus,
            NodeFlags = nodeFlags,
            Movement = movement,
        };
    }

    private static (AnimationTracks?, AnimatedStreamStatus) TryAnimated(Codec codec, Func<AnimationTracks> decode)
    {
        try { return (decode(), new AnimatedStreamStatus.Decoded()); }
        catch { return (null, new AnimatedStreamStatus.Unsupported(codec)); }
    }

    //==== movement + flags ====

    // Per-frame movement rotation deltas, built at read time so the writer can
    // accumulate them as quaternions (matching the Rust/Foundry fold). f32
    // sin/cos via the double path (correctly-rounded — matches Rust libm).
    private static RealQuaternion YawQuat(float yaw)
    {
        float half = yaw * 0.5f;
        return new RealQuaternion(0f, 0f, (float)System.Math.Sin(half), (float)System.Math.Cos(half));
    }

    private static RealQuaternion AngleAxisQuat(float x, float y, float z)
    {
        float angle = MathF.Sqrt(x * x + y * y + z * z);
        if (angle <= 1e-8f) return new RealQuaternion(0f, 0f, 0f, 1f);
        float half = angle * 0.5f;
        float s = (float)System.Math.Sin(half), c = (float)System.Math.Cos(half);
        float k = s / angle;
        return new RealQuaternion(x * k, y * k, z * k, c);
    }

    private static MovementData ReadMovementAt(byte[] blob, int offset, int movementBytes, string? frameInfoType, int frameCount)
    {
        MovementKind kind = frameInfoType is null ? MovementKind.None : MovementKindExtensions.FromSchemaName(frameInfoType);
        if (kind == MovementKind.None || movementBytes == 0) return new MovementData();
        int bpf = kind.BytesPerFrame();
        if (bpf == 0 || movementBytes % bpf != 0) return new MovementData();
        if ((long)offset + movementBytes > blob.Length) return new MovementData();
        int readCount = System.Math.Min(movementBytes / bpf, frameCount);
        var frames = new List<MovementFrame>(readCount);
        for (int i = 0; i < readCount; i++)
        {
            int off = offset + i * bpf;
            var f = new MovementFrame();
            switch (kind)
            {
                case MovementKind.DxDy:
                    f.Dx = F32(blob, off); f.Dy = F32(blob, off + 4); break;
                case MovementKind.DxDyDyaw:
                    f.Dx = F32(blob, off); f.Dy = F32(blob, off + 4); f.Dyaw = F32(blob, off + 8);
                    f.Rotation = YawQuat(f.Dyaw); break;
                case MovementKind.DxDyDzDyaw:
                    f.Dx = F32(blob, off); f.Dy = F32(blob, off + 4); f.Dz = F32(blob, off + 8); f.Dyaw = F32(blob, off + 12);
                    f.Rotation = YawQuat(f.Dyaw); break;
                case MovementKind.DxDyDzDangleAxis:
                    f.Dx = F32(blob, off); f.Dy = F32(blob, off + 4); f.Dz = F32(blob, off + 8);
                    f.Rotation = AngleAxisQuat(F32(blob, off + 12), F32(blob, off + 16), F32(blob, off + 20)); break;
                case MovementKind.XyzAbsolute:
                    // Absolute root position, no rotation.
                    f.Dx = F32(blob, off); f.Dy = F32(blob, off + 4); f.Dz = F32(blob, off + 8); break;
            }
            frames.Add(f);
        }
        return new MovementData { Kind = kind, Frames = frames };
    }

    private static NodeFlags? ReadNodeFlags(byte[] blob, int staticOff, int staticTotal, int animatedTotal)
    {
        if (staticTotal == 0 && animatedTotal == 0) return null;
        if (staticTotal % 3 != 0 || animatedTotal % 3 != 0) return null;
        long staticEnd = (long)staticOff + staticTotal;
        long animatedEnd = staticEnd + animatedTotal;
        if (animatedEnd > blob.Length) return null;
        int staticPer = staticTotal / 3;
        int animatedPer = animatedTotal / 3;
        var outFlags = new NodeFlags();
        if (staticPer > 0)
        {
            outFlags.StaticRotation = BitArray.FromBytes(blob.AsSpan(staticOff, staticPer));
            outFlags.StaticTranslation = BitArray.FromBytes(blob.AsSpan(staticOff + staticPer, staticPer));
            outFlags.StaticScale = BitArray.FromBytes(blob.AsSpan(staticOff + 2 * staticPer, staticPer));
        }
        if (animatedPer > 0)
        {
            int a = (int)staticEnd;
            outFlags.AnimatedRotation = BitArray.FromBytes(blob.AsSpan(a, animatedPer));
            outFlags.AnimatedTranslation = BitArray.FromBytes(blob.AsSpan(a + animatedPer, animatedPer));
            outFlags.AnimatedScale = BitArray.FromBytes(blob.AsSpan(a + 2 * animatedPer, animatedPer));
        }
        return outFlags;
    }

    private static int? AnimatedCodecStreamSize(byte[] blob, Codec codec)
    {
        switch (codec)
        {
            case Codec.UncompressedStatic or Codec.UncompressedAnimated
                or Codec.EightByteQuantizedRotationOnly or Codec.BlendScreen:
            {
                if (FullframeHeader.FromBytes(blob) is not { } h) return null;
                int nRot = h.Base.TotalRotatedNodes, nTrans = h.Base.TotalTranslatedNodes, nScale = h.Base.TotalScaledNodes;
                int rotEnd = 32 + nRot * (int)h.RotationStride;
                int transEnd = nTrans > 0 ? (int)h.Base.TranslationOffset + nTrans * (int)h.TranslationStride : 0;
                int scaleEnd = nScale > 0 ? (int)h.Base.ScaleOffset + nScale * (int)h.ScaleStride : 0;
                return System.Math.Max(rotEnd, System.Math.Max(transEnd, scaleEnd));
            }
            case Codec.ByteKeyframeLightlyQuantized or Codec.WordKeyframeLightlyQuantized
                or Codec.ReverseByteKeyframeLightlyQuantized or Codec.ReverseWordKeyframeLightlyQuantized:
            {
                if (KeyframeHeader.FromBytes(blob) is not { } h) return null;
                int nRot = h.Base.TotalRotatedNodes, nTrans = h.Base.TotalTranslatedNodes, nScale = h.Base.TotalScaledNodes;
                int KeyCount(int start, int count)
                {
                    int sum = 0;
                    for (int i = start; i < start + count; i++)
                    {
                        int off = 48 + i * 4;
                        if (off + 4 > blob.Length) continue;
                        sum += (int)(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off, 4)) & 0xFFF);
                    }
                    return sum;
                }
                int rotKeys = KeyCount(0, nRot), transKeys = KeyCount(nRot, nTrans), scaleKeys = KeyCount(nRot + nTrans, nScale);
                int rotPe = (int)h.RotPayloadOff + rotKeys * 8;
                int transPe = (int)h.TransPayloadOff + transKeys * 12;
                int scalePe = (int)h.ScalePayloadOff + scaleKeys * 4;
                int timeSize = codec is Codec.ByteKeyframeLightlyQuantized or Codec.ReverseByteKeyframeLightlyQuantized ? 1 : 2;
                int rotTe = (int)h.RotTimeOff + rotKeys * timeSize;
                int transTe = (int)h.TransTimeOff + transKeys * timeSize;
                int scaleTe = (int)h.ScaleTimeOff + scaleKeys * timeSize;
                return Max6(rotPe, transPe, scalePe, rotTe, transTe, scaleTe);
            }
            default: return null;
        }
    }

    private static int Max6(int a, int b, int c, int d, int e, int f) =>
        System.Math.Max(a, System.Math.Max(b, System.Math.Max(c, System.Math.Max(d, System.Math.Max(e, f)))));

    /// <summary>Halo 4 SharedStatic (codec 11) static rest pose. The
    /// <c>compressed_static_pose</c> blob section (the LAST section) holds only
    /// int16 indices into the graph-level <see cref="SharedStaticPool"/>; values
    /// come from that pool. One static frame per component, in codec-node order.
    /// <c>null</c> when there's no pool / no section / not a SharedStatic header.
    /// RE'd from the H4 Xbox debug build (rotation index table @byte 32,
    /// translation @u32@12, scale @u32@16).</summary>
    private static AnimationTracks? DecodeSharedStatic(AnimationGroup group)
    {
        if (group.SharedStatic is not { } pool) return null;
        long cps = group.DataSizes?.Get("compressed_static_pose") ?? 0;
        if (cps <= 0 || cps > group.Blob.Length) return null;
        byte[] blob = group.Blob;
        int b = blob.Length - (int)cps; // section start (compressed_static_pose is the last section)
        if (cps < 32 || blob[b] != (byte)Codec.SharedStatic) return null;
        int nRot = blob[b + 1], nTrn = blob[b + 2], nScl = blob[b + 3];
        int trnOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(b + 12, 4));
        int sclOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(b + 16, 4));
        short Index(int o) => o >= 0 && b + o + 2 <= blob.Length
            ? BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(b + o, 2)) : (short)-1;

        var tracks = new AnimationTracks { Codec = Codec.SharedStatic, FrameCount = 1 };
        for (int k = 0; k < nRot; k++)
        {
            short idx = Index(32 + 2 * k);
            tracks.Rotations.Add([idx >= 0 && idx < pool.Rotations.Count ? pool.Rotations[idx] : new RealQuaternion(0, 0, 0, 1)]);
        }
        for (int k = 0; k < nTrn; k++)
        {
            short idx = Index(trnOff + 2 * k);
            tracks.Translations.Add([idx >= 0 && idx < pool.Translations.Count ? pool.Translations[idx] : default]);
        }
        for (int k = 0; k < nScl; k++)
        {
            short idx = Index(sclOff + 2 * k);
            tracks.Scales.Add([idx >= 0 && idx < pool.Scales.Count ? pool.Scales[idx] : 1.0f]);
        }
        return tracks;
    }

    //==== fullframe (slots 1/2/3/8) ====

    private static AnimationTracks DecodeUncompressedStatic(byte[] blob) =>
        DecodeFullframe(blob, Codec.UncompressedStatic, 1, true);

    private static AnimationTracks DecodeFullframe(byte[] blob, Codec codec, ushort frameCount, bool quat8Byte)
    {
        if (FullframeHeader.FromBytes(blob) is not { } header)
            throw new AnimationException($"{codec} header: need {FullframeHeader.Size} bytes, blob has {blob.Length}");

        int nRot = header.Base.TotalRotatedNodes, nTrans = header.Base.TotalTranslatedNodes, nScale = header.Base.TotalScaledNodes;
        int frames = frameCount;
        int quatSize = quat8Byte ? 8 : 16;

        int StrideOr(uint stored, int elemSize) => stored == 0 ? elemSize * frames : (int)stored;
        int rotStride = StrideOr(header.RotationStride, quatSize);
        int transStride = StrideOr(header.TranslationStride, 12);
        int scaleStride = StrideOr(header.ScaleStride, 4);

        int rotStart = FullframeHeader.Size;
        long rotEnd = (long)rotStart + (long)nRot * rotStride;
        if (rotEnd > blob.Length) throw Truncated(codec, rotEnd, blob.Length);
        int transStart = (int)header.Base.TranslationOffset;
        long transEnd = (long)transStart + (long)nTrans * transStride;
        if (transEnd > blob.Length) throw Truncated(codec, transEnd, blob.Length);
        int scaleStart = (int)header.Base.ScaleOffset;
        long scaleEnd = (long)scaleStart + (long)nScale * scaleStride;
        if (scaleEnd > blob.Length) throw Truncated(codec, scaleEnd, blob.Length);

        var rotations = new List<List<RealQuaternion>>(nRot);
        for (int node = 0; node < nRot; node++)
        {
            var fv = new List<RealQuaternion>(frames);
            for (int f = 0; f < frames; f++)
            {
                int off = rotStart + node * rotStride + f * quatSize;
                var q = quat8Byte
                    ? new RealQuaternion(I16Unit(blob, off), I16Unit(blob, off + 2), I16Unit(blob, off + 4), I16Unit(blob, off + 6))
                    : new RealQuaternion(F32(blob, off), F32(blob, off + 4), F32(blob, off + 8), F32(blob, off + 12));
                fv.Add(q.Normalized());
            }
            rotations.Add(fv);
        }
        var translations = new List<List<RealPoint3d>>(nTrans);
        for (int node = 0; node < nTrans; node++)
        {
            var fv = new List<RealPoint3d>(frames);
            for (int f = 0; f < frames; f++)
            {
                int off = transStart + node * transStride + f * 12;
                fv.Add(new RealPoint3d(F32(blob, off), F32(blob, off + 4), F32(blob, off + 8)));
            }
            translations.Add(fv);
        }
        var scales = new List<List<float>>(nScale);
        for (int node = 0; node < nScale; node++)
        {
            var fv = new List<float>(frames);
            for (int f = 0; f < frames; f++)
                fv.Add(F32(blob, scaleStart + node * scaleStride + f * 4));
            scales.Add(fv);
        }
        return new AnimationTracks { Codec = codec, FrameCount = frameCount, Rotations = rotations, Translations = translations, Scales = scales };
    }

    //==== keyframe (slots 4/5/6/7) ====

    private static AnimationTracks DecodeKeyframe(byte[] blob, Codec codec, ushort frameCount, int timeByteSize)
    {
        if (KeyframeHeader.FromBytes(blob) is not { } header)
            throw new AnimationException($"{codec} header: need {KeyframeHeader.Size} bytes, blob has {blob.Length}");
        int nRot = header.Base.TotalRotatedNodes, nTrans = header.Base.TotalTranslatedNodes, nScale = header.Base.TotalScaledNodes;
        int packedStart = KeyframeHeader.Size;
        int packedTotal = nRot + nTrans + nScale;
        long packedEnd = (long)packedStart + (long)packedTotal * 4;
        if (packedEnd > blob.Length) throw Truncated(codec, packedEnd, blob.Length);

        (uint TimeOff, uint KeyCount) ReadPacked(int idx)
        {
            uint pd = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(packedStart + idx * 4, 4));
            return (pd >> 12, pd & 0xFFF);
        }
        var rotPacks = Enumerable.Range(0, nRot).Select(ReadPacked).ToList();
        var transPacks = Enumerable.Range(nRot, nTrans).Select(ReadPacked).ToList();
        var scalePacks = Enumerable.Range(nRot + nTrans, nScale).Select(ReadPacked).ToList();

        var rotations = DecodeComponent(blob, codec, frameCount, timeByteSize,
            (int)header.RotTimeOff, (int)header.RotPayloadOff, 8, rotPacks,
            new RealQuaternion(0, 0, 0, 1),
            (b, off) => new RealQuaternion(I16Unit(b, off), I16Unit(b, off + 2), I16Unit(b, off + 4), I16Unit(b, off + 6)).Normalized(),
            (a, b, t) => a.Nlerp(b, t));
        var translations = DecodeComponent(blob, codec, frameCount, timeByteSize,
            (int)header.TransTimeOff, (int)header.TransPayloadOff, 12, transPacks,
            default(RealPoint3d),
            (b, off) => new RealPoint3d(F32(b, off), F32(b, off + 4), F32(b, off + 8)),
            (a, b, t) => new RealPoint3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t));
        var scales = DecodeComponent(blob, codec, frameCount, timeByteSize,
            (int)header.ScaleTimeOff, (int)header.ScalePayloadOff, 4, scalePacks,
            1.0f,
            (b, off) => F32(b, off),
            (a, b, t) => a + (b - a) * t);

        return new AnimationTracks { Codec = codec, FrameCount = frameCount, Rotations = rotations, Translations = translations, Scales = scales };
    }

    private static List<List<T>> DecodeComponent<T>(
        byte[] blob, Codec codec, ushort frameCount, int timeByteSize,
        int timeTableStart, int payloadTableStart, int elementSize,
        List<(uint TimeOff, uint KeyCount)> nodePacks, T identity,
        Func<byte[], int, T> readElement, Func<T, T, float, T> interpolate)
    {
        var outList = new List<List<T>>();
        foreach (var (timeOff, keyCountU) in nodePacks)
        {
            int keyCount = (int)keyCountU;
            int framesCount = frameCount;
            if (keyCount == 0)
            {
                outList.Add(Enumerable.Repeat(identity, framesCount).ToList());
                continue;
            }
            int timeStart = timeTableStart + (int)timeOff * timeByteSize;
            int timeEnd = timeStart + keyCount * timeByteSize;
            int payloadStart = payloadTableStart + (int)timeOff * elementSize;
            int payloadEnd = payloadStart + keyCount * elementSize;
            if (timeEnd > blob.Length || payloadEnd > blob.Length)
                throw Truncated(codec, System.Math.Max(timeEnd, payloadEnd), blob.Length);

            uint ReadTime(int i)
            {
                int off = timeStart + i * timeByteSize;
                return timeByteSize == 1 ? blob[off] : BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off, 2));
            }
            T ReadValue(int keyIdx) => readElement(blob, payloadStart + keyIdx * elementSize);

            var frames = new List<T>(framesCount);
            if (keyCount == 1)
            {
                var v = ReadValue(0);
                for (int i = 0; i < framesCount; i++) frames.Add(v);
                outList.Add(frames);
                continue;
            }
            for (uint frameIdx = 0; frameIdx < framesCount; frameIdx++)
            {
                int bracket = 0;
                for (int i = 0; i < keyCount; i++)
                {
                    if (ReadTime(i) <= frameIdx) bracket = i; else break;
                }
                if (bracket == keyCount - 1) { frames.Add(ReadValue(bracket)); continue; }
                float tA = ReadTime(bracket);
                float tB = ReadTime(bracket + 1);
                float t = tB > tA ? (frameIdx - tA) / (tB - tA) : 0.0f;
                frames.Add(interpolate(ReadValue(bracket), ReadValue(bracket + 1), t));
            }
            outList.Add(frames);
        }
        return outList;
    }

    //==== curve (slots 9/10) ====

    private sealed class Cursor(byte[] data)
    {
        private readonly byte[] _data = data;
        public int Pos;

        public void Seek(int off)
        {
            if (off > _data.Length) throw Truncated(Codec.Curve, off, _data.Length);
            Pos = off;
        }
        public void Skip(int delta) => Pos = delta >= 0 ? Pos + delta : System.Math.Max(0, Pos - (-delta));
        public byte ReadU8()
        {
            if (Pos >= _data.Length) throw Truncated(Codec.Curve, Pos + 1, _data.Length);
            return _data[Pos++];
        }
        public ushort ReadU16()
        {
            if (Pos + 2 > _data.Length) throw Truncated(Codec.Curve, Pos + 2, _data.Length);
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Pos, 2));
            Pos += 2; return v;
        }
        public short ReadS16() => (short)ReadU16();
        public uint ReadU32()
        {
            if (Pos + 4 > _data.Length) throw Truncated(Codec.Curve, Pos + 4, _data.Length);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Pos, 4));
            Pos += 4; return v;
        }
        public float ReadF32() => BitConverter.UInt32BitsToSingle(ReadU32());
    }

    private static AnimationTracks DecodeCurve(byte[] blob, Codec codec, ushort frameCount, bool revised)
    {
        var c = new Cursor(blob);
        if (blob.Length < 32) throw new AnimationException($"{codec} header: need 32 bytes, blob has {blob.Length}");
        c.Skip(12);
        int translationDataOffset = (int)c.ReadU32();
        int scaleDataOffset = (int)c.ReadU32();
        int payloadDataOffset = (int)c.ReadU32();
        _ = c.ReadU32(); // total_compressed_size
        c.ReadU32(); // reserved

        int nRot = blob[1], nTrans = blob[2], nScale = blob[3];
        int frames = frameCount;

        var rotationOffsets = new List<int>(nRot);
        for (int i = 0; i < nRot; i++) rotationOffsets.Add((int)c.ReadU32());
        var rotations = new List<List<RealQuaternion>>(nRot);
        foreach (int nodeOff in rotationOffsets)
        {
            c.Seek(payloadDataOffset + nodeOff);
            rotations.Add(ReadCurveRotationNode(c, frames, revised));
        }

        var translations = new List<List<RealPoint3d>>(nTrans);
        if (nTrans > 0)
        {
            c.Seek(payloadDataOffset + translationDataOffset);
            var transOffsets = new List<int>(nTrans);
            for (int i = 0; i < nTrans; i++) transOffsets.Add((int)c.ReadU32());
            foreach (int nodeOff in transOffsets)
            {
                c.Seek(payloadDataOffset + nodeOff);
                translations.Add(ReadCurveTranslationNode(c, frames));
            }
        }

        var scales = new List<List<float>>(nScale);
        if (nScale > 0)
        {
            c.Seek(payloadDataOffset + scaleDataOffset);
            var scaleOffsets = new List<int>(nScale);
            for (int i = 0; i < nScale; i++) scaleOffsets.Add((int)c.ReadU32());
            foreach (int nodeOff in scaleOffsets)
            {
                c.Seek(payloadDataOffset + nodeOff);
                scales.Add(ReadCurveScaleNode(c, frames));
            }
        }

        return new AnimationTracks { Codec = codec, FrameCount = frameCount, Rotations = rotations, Translations = translations, Scales = scales };
    }

    private static List<RealQuaternion> ReadCurveRotationNode(Cursor c, int frames, bool revised)
    {
        c.ReadU16();
        int keyCount = c.ReadU16();
        byte flags = c.ReadU8();
        c.ReadU8();
        c.ReadS16();
        var keyframes = (flags & 1) == 0 ? ReadCurveKeyframeDeltas(c, keyCount) : new List<uint>();

        RealQuaternion ReadQuat()
        {
            short v3 = c.ReadS16(), v4 = c.ReadS16(), v5 = c.ReadS16();
            return revised
                ? DecompressRevisedQuat(v3, v4, v5)
                : DecompressCurveQuat(v3 / (float)short.MaxValue, v4 / (float)short.MaxValue, v5 / (float)short.MaxValue);
        }

        var outList = new List<RealQuaternion>(frames);
        RealQuaternion p1 = new(0, 0, 0, 1), p2 = new(0, 0, 0, 1);
        byte[] tangentBytes = new byte[4];
        uint currentKf = 0, nextKf = 0;
        int keyframeIndex = 0;
        for (uint frameIndex = 0; frameIndex < frames; frameIndex++)
        {
            RealQuaternion q;
            if ((flags & 1) != 0) q = ReadQuat();
            else
            {
                if (keyframeIndex < keyframes.Count && keyframes[keyframeIndex] == frameIndex && frameIndex < frames - 1)
                {
                    p1 = ReadQuat();
                    tangentBytes = [c.ReadU8(), c.ReadU8(), c.ReadU8(), c.ReadU8()];
                    p2 = ReadQuat();
                    currentKf = keyframes[keyframeIndex];
                    nextKf = keyframeIndex + 1 < keyframes.Count ? keyframes[keyframeIndex + 1] : currentKf + 1;
                    keyframeIndex++;
                    c.Skip(-6);
                }
                float span = System.Math.Max(1.0f, SatSub(nextKf, currentKf));
                float t = SatSub(frameIndex, currentKf) / span;
                var tan1 = CurveTangentQuat((tangentBytes[0] >> 4) - 7, (tangentBytes[1] >> 4) - 7, (tangentBytes[2] >> 4) - 7, (tangentBytes[3] >> 4) - 7, p1, p2);
                var tan2 = CurveTangentQuat((tangentBytes[0] & 0xF) - 7, (tangentBytes[1] & 0xF) - 7, (tangentBytes[2] & 0xF) - 7, (tangentBytes[3] & 0xF) - 7, p1, p2);
                q = CurvePositionQuat(t, tan1, tan2, p1, p2);
            }
            outList.Add(q);
        }
        return outList;
    }

    private static List<RealPoint3d> ReadCurveTranslationNode(Cursor c, int frames)
    {
        c.ReadU16();
        int keyCount = c.ReadU16();
        byte flags = c.ReadU8();
        c.ReadU8();
        c.ReadU16();
        float offsetX = c.ReadF32(), offsetY = c.ReadF32(), offsetZ = c.ReadF32(), scale = c.ReadF32();
        var keyframes = (flags & 1) == 0 ? ReadCurveKeyframeDeltas(c, keyCount) : new List<uint>();

        var outList = new List<RealPoint3d>(frames);
        RealPoint3d p1 = default, p2 = default;
        byte[] tangentBytes = new byte[3];
        uint currentKf = 0, nextKf = 0;
        int keyframeIndex = 0;
        for (uint frameIndex = 0; frameIndex < frames; frameIndex++)
        {
            RealPoint3d v;
            if ((flags & 1) != 0)
                v = new RealPoint3d(c.ReadS16() / (float)short.MaxValue, c.ReadS16() / (float)short.MaxValue, c.ReadS16() / (float)short.MaxValue);
            else
            {
                if (keyframeIndex < keyframes.Count && keyframes[keyframeIndex] == frameIndex && frameIndex < frames - 1)
                {
                    float x1 = c.ReadS16() / (float)short.MaxValue, y1 = c.ReadS16() / (float)short.MaxValue, z1 = c.ReadS16() / (float)short.MaxValue;
                    tangentBytes = [c.ReadU8(), c.ReadU8(), c.ReadU8()];
                    float x2 = c.ReadS16() / (float)short.MaxValue, y2 = c.ReadS16() / (float)short.MaxValue, z2 = c.ReadS16() / (float)short.MaxValue;
                    p1 = new RealPoint3d(x1, y1, z1);
                    p2 = new RealPoint3d(x2, y2, z2);
                    currentKf = keyframes[keyframeIndex];
                    nextKf = keyframeIndex + 1 < keyframes.Count ? keyframes[keyframeIndex + 1] : currentKf + 1;
                    keyframeIndex++;
                    c.Skip(-6);
                }
                float span = System.Math.Max(1.0f, SatSub(nextKf, currentKf));
                float t = SatSub(frameIndex, currentKf) / span;
                var tan1 = CurveTangentVec((tangentBytes[0] >> 4) - 7, (tangentBytes[1] >> 4) - 7, (tangentBytes[2] >> 4) - 7, p1, p2);
                var tan2 = CurveTangentVec((tangentBytes[0] & 0xF) - 7, (tangentBytes[1] & 0xF) - 7, (tangentBytes[2] & 0xF) - 7, p1, p2);
                v = CurvePositionVec(t, tan1, tan2, p1, p2);
            }
            outList.Add(new RealPoint3d(scale * v.X + offsetX, scale * v.Y + offsetY, scale * v.Z + offsetZ));
        }
        return outList;
    }

    private static List<float> ReadCurveScaleNode(Cursor c, int frames)
    {
        c.ReadU16();
        int keyCount = c.ReadU16();
        byte flags = c.ReadU8();
        c.ReadU8();
        c.ReadU16();
        float offset = c.ReadF32(), scale = c.ReadF32();
        var keyframes = (flags & 1) == 0 ? ReadCurveKeyframeDeltas(c, keyCount) : new List<uint>();

        var outList = new List<float>(frames);
        float p1 = 0, p2 = 0;
        byte tangentByte = 0;
        uint currentKf = 0, nextKf = 0;
        int keyframeIndex = 0;
        for (uint frameIndex = 0; frameIndex < frames; frameIndex++)
        {
            float v;
            if ((flags & 1) != 0) v = c.ReadS16() / (float)short.MaxValue;
            else
            {
                if (keyframeIndex < keyframes.Count && keyframes[keyframeIndex] == frameIndex && frameIndex < frames - 1)
                {
                    p1 = c.ReadS16() / (float)short.MaxValue;
                    tangentByte = c.ReadU8();
                    p2 = c.ReadS16() / (float)short.MaxValue;
                    currentKf = keyframes[keyframeIndex];
                    nextKf = keyframeIndex + 1 < keyframes.Count ? keyframes[keyframeIndex + 1] : currentKf + 1;
                    keyframeIndex++;
                    c.Skip(-2);
                }
                float span = System.Math.Max(1.0f, SatSub(nextKf, currentKf));
                float t = SatSub(frameIndex, currentKf) / span;
                float tan1 = CurveTangentScalar((tangentByte >> 4) - 7, p1, p2);
                float tan2 = CurveTangentScalar((tangentByte & 0xF) - 7, p1, p2);
                v = CurvePositionScalar(t, tan1, tan2, p1, p2);
            }
            outList.Add(v * scale + offset);
        }
        return outList;
    }

    private static List<uint> ReadCurveKeyframeDeltas(Cursor c, int keyCount)
    {
        var keyframes = new List<uint>(keyCount + 1) { 0u };
        uint total = 0;
        for (int i = 0; i < keyCount; i++)
        {
            total += c.ReadU8();
            keyframes.Add(total);
        }
        return keyframes;
    }

    private static RealQuaternion DecompressCurveQuat(float i, float j, float w)
    {
        float k = MathF.Sqrt(MathF.Max(1.0f - i * i - j * j, 0.0f));
        if (w < 0.0f) k = -k;
        float wUnfolded = MathF.Abs(w) * 2.0f - 1.0f;
        float scale = MathF.Sqrt(MathF.Max(1.0f - wUnfolded * wUnfolded, 0.0f));
        return new RealQuaternion(i * scale, j * scale, k * scale, wUnfolded).Normalized();
    }

    private static RealQuaternion DecompressRevisedQuat(short v3, short v4, short v5)
    {
        const float sqrtHalf = 0.707_106_77f;
        float i = ((v3 & ~1) / (float)short.MaxValue) * sqrtHalf;
        float j = ((v4 & ~1) / (float)short.MaxValue) * sqrtHalf;
        float k = ((v5 & ~1) / (float)short.MaxValue) * sqrtHalf;
        float missing = MathF.Sqrt(MathF.Max(1.0f - i * i - j * j - k * k, 0.0f));
        if ((v3 & 1) != 0) missing = -missing;
        int componentIndex = (v5 & 1) | (2 * (v4 & 1));
        var output = new float[4];
        output[(componentIndex + 1) & 3] = i;
        output[(componentIndex + 2) & 3] = j;
        output[(componentIndex + 3) & 3] = k;
        output[componentIndex] = missing;
        return new RealQuaternion(output[0], output[1], output[2], output[3]).Normalized();
    }

    private static float CurveTangentScalar(int tangentSigned, float p1, float p2)
    {
        float t = tangentSigned / 7.0f;
        return MathF.Abs(t) * (t * 0.300_000_011_920_929f) + (p2 - p1);
    }

    private static RealQuaternion CurveTangentQuat(int it, int jt, int kt, int wt, RealQuaternion p1, RealQuaternion p2) =>
        new(CurveTangentScalar(it, p1.I, p2.I), CurveTangentScalar(jt, p1.J, p2.J), CurveTangentScalar(kt, p1.K, p2.K), CurveTangentScalar(wt, p1.W, p2.W));

    private static RealVector3d CurveTangentVec(int xt, int yt, int zt, RealPoint3d p1, RealPoint3d p2) =>
        new(CurveTangentScalar(xt, p1.X, p2.X), CurveTangentScalar(yt, p1.Y, p2.Y), CurveTangentScalar(zt, p1.Z, p2.Z));

    private static float CurvePositionScalar(float t, float tan1, float tan2, float p1, float p2)
    {
        float t2 = t * t, t3 = t2 * t;
        float h1 = 2.0f * t3 - 3.0f * t2 + 1.0f;
        float h2 = t3 - 2.0f * t2 + t;
        float h3 = 3.0f * t2 - 2.0f * t3;
        float h4 = t3 - t2;
        return h1 * p1 + h2 * tan1 + h3 * p2 + h4 * tan2;
    }

    private static RealQuaternion CurvePositionQuat(float t, RealQuaternion tan1, RealQuaternion tan2, RealQuaternion p1, RealQuaternion p2) =>
        new RealQuaternion(
            CurvePositionScalar(t, tan1.I, tan2.I, p1.I, p2.I),
            CurvePositionScalar(t, tan1.J, tan2.J, p1.J, p2.J),
            CurvePositionScalar(t, tan1.K, tan2.K, p1.K, p2.K),
            CurvePositionScalar(t, tan1.W, tan2.W, p1.W, p2.W)).Normalized();

    private static RealPoint3d CurvePositionVec(float t, RealVector3d tan1, RealVector3d tan2, RealPoint3d p1, RealPoint3d p2) =>
        new(CurvePositionScalar(t, tan1.I, tan2.I, p1.X, p2.X),
            CurvePositionScalar(t, tan1.J, tan2.J, p1.Y, p2.Y),
            CurvePositionScalar(t, tan1.K, tan2.K, p1.Z, p2.Z));

    //==== helpers ====

    private static float SatSub(uint a, uint b) => a > b ? a - b : 0.0f;

    private static float I16Unit(byte[] blob, int off) =>
        BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(off, 2)) / (float)short.MaxValue;

    private static float F32(byte[] blob, int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(off, 4));

    private static AnimationException Truncated(Codec codec, long wantEnd, int blobSize) =>
        new($"{codec} payload: slice ends at {wantEnd} but blob is {blobSize} bytes");
}
