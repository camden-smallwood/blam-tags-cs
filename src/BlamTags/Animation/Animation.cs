namespace BlamTags;

/// <summary>Decode failure for <see cref="Animation"/> / animation codecs —
/// the genuinely-malformed cases (truncated headers, bad codec byte, no
/// payload). Recognized-but-undecodable animated streams are reported on the
/// clip via <see cref="AnimatedStreamStatus"/> instead of throwing.</summary>
public sealed class AnimationException(string message) : Exception(message);

/// <summary>Engine-version flavor of the <c>data sizes</c> struct.</summary>
public enum SizeLayout { H3, Reach, Halo2 }

/// <summary>Per-animation <c>data sizes</c> breakdown (name/value list, since
/// the shape varies by engine). All values are byte counts.</summary>
public sealed class PackedDataSizes
{
    public List<(string Name, long Value)> Fields { get; init; } = new();

    public long Total() => Fields.Sum(f => f.Value);

    public long Get(string name)
    {
        foreach (var (n, v) in Fields) if (n == name) return v;
        return 0;
    }

    public SizeLayout Layout()
    {
        // Halo 2 is built by ReadH2DataSizes with a leading `h2_static_data`
        // marker; its sections are ordered identically to Reach
        // (codec/codec/flags/flags/movement/pill), so it decodes positionally.
        if (Fields.Count > 0 && Fields[0].Name == "h2_static_data") return SizeLayout.Halo2;
        string[] reachOnly = ["blend_screen_data", "object_space_offset_data", "ik_chain_event_data"];
        return Fields.Any(f => reachOnly.Contains(f.Name)) ? SizeLayout.Reach : SizeLayout.H3;
    }
}

/// <summary>One animation entry — header metadata from <c>animations[i]</c>
/// joined with the matching <c>group_members[m]</c> runtime payload.</summary>
public sealed class AnimationGroup
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public string? AnimationType { get; init; }
    public string? FrameInfoType { get; init; }
    public short FrameCount { get; init; }
    public sbyte NodeCount { get; init; }
    public int NodeListChecksum { get; init; }
    public short ResourceGroup { get; init; }
    public short ResourceGroupMember { get; init; }
    public int? Checksum { get; init; }
    public short? CodecFrameCount { get; init; }
    public string? MovementType { get; init; }
    public PackedDataSizes? DataSizes { get; init; }
    public byte? CodecByte { get; init; }
    public byte[] Blob { get; init; } = [];
    public bool WorldRelative { get; init; }
    public IReadOnlyList<ObjectSpaceParentNode> ObjectSpaceParents { get; init; } = [];

    /// <summary>Halo 4 graph-level shared-static value pool (<c>codec data/
    /// shared_static_codec</c>). The H4 static rest pose isn't in the
    /// per-animation blob — SharedStatic (codec 11) stores only int16 indices
    /// into this graph-shared pool. Shared across all groups of one tag;
    /// <c>null</c> for non-H4 / tags without it.</summary>
    public SharedStaticPool? SharedStatic { get; init; }

    public bool MovementTypeMismatch =>
        FrameInfoType is { } a && MovementType is { } b && a != b;

    /// <summary>Decode the blob into an <see cref="AnimationClip"/>.</summary>
    public AnimationClip Decode() => AnimationCodec.Decode(this);
}

/// <summary>Halo 4's graph-level shared-static value pool — decoded once from
/// <c>codec data/shared_static_codec/{rotations,translations,scale}</c>. The
/// per-animation <c>compressed_static_pose</c> codec stream (codec 11) holds
/// int16 indices into these. Rotations are 4×int16/0x7FFF quaternions;
/// translations/scales are raw f32. RE'd from the H4 Xbox debug build.</summary>
public sealed class SharedStaticPool
{
    public List<RealQuaternion> Rotations { get; } = [];
    public List<RealPoint3d> Translations { get; } = [];
    public List<float> Scales { get; } = [];
}

/// <summary>All animations in a jmad, paired with their resource-group
/// payloads. Construct via <see cref="New"/>.</summary>
public sealed class Animation
{
    private readonly List<AnimationGroup> _animations;
    private readonly string? _parent;

    private Animation(List<AnimationGroup> animations, string? parent)
    {
        _animations = animations;
        _parent = parent;
    }

    private static readonly string[] TopLevelNames = ["definitions", "resources"];

    public static Animation New(TagFile tag)
    {
        var root = tag.Root;
        var game = tag.GameOf();

        string? topPrefix = TopLevelNames.FirstOrDefault(name => root.FieldPath($"{name}/animations") is not null);
        if (topPrefix is null)
            throw new AnimationException("tag is not a recognizable model_animation_graph (missing `definitions/animations`)");

        var animationsBlock = root.FieldPath($"{topPrefix}/animations")?.AsBlock()
            ?? throw new AnimationException("tag is not a recognizable model_animation_graph (missing `definitions/animations`)");

        // Pre-walk tag resource groups → per-group list of group_member structs.
        var groupMemberTable = new List<List<TagStruct>?>();
        var resourceGroupsBlock = root.FieldPath("tag resource groups")?.AsBlock();
        if (resourceGroupsBlock is not null)
        {
            for (int r = 0; r < resourceGroupsBlock.Count; r++)
            {
                var elem = resourceGroupsBlock.Element(r);
                if (elem is null) { groupMemberTable.Add(null); continue; }
                var header = elem.Field("tag_resource")?.AsResource()?.AsStruct();
                if (header is null) { groupMemberTable.Add(null); continue; }
                var membersBlock = header.Field("group_members")?.AsBlock();
                var members = new List<TagStruct>();
                if (membersBlock is not null)
                    for (int i = 0; i < membersBlock.Count; i++)
                        if (membersBlock.Element(i) is { } m) members.Add(m);
                groupMemberTable.Add(members);
            }
        }

        // Halo 4 graph-level shared-static value pool (read once, shared).
        var sharedStatic = ReadSharedStaticPool(root);

        var animations = new List<AnimationGroup>(animationsBlock.Count);
        for (int i = 0; i < animationsBlock.Count; i++)
        {
            var anim = animationsBlock.Element(i);
            if (anim is null) continue;
            string? name = anim.ReadStringId("name");

            // Reach moved per-animation codec metadata into a nested
            // `shared animation data[0]`; H3 keeps it on the outer struct.
            var shared = anim.Field("shared animation data")?.AsBlock()?.Element(0);
            var metadata = shared is not null
                && (shared.Field("resource_group") is not null || shared.Field("frame count") is not null)
                ? shared : anim;

            string? animationType = metadata.ReadEnumName("animation type") ?? metadata.ReadEnumName("type");
            string? frameInfoType = metadata.ReadEnumName("frame info type");
            short frameCount = (short)(metadata.ReadIntAny("frame count") ?? 0);
            sbyte nodeCount = (sbyte)(metadata.ReadIntAny("node count") ?? 0);
            int nodeListChecksum = (int)(metadata.ReadIntAny("node list checksum") ?? 0);
            short resourceGroup = (short)(metadata.ReadIntAny("resource_group") ?? -1);
            short resourceGroupMember = (short)(metadata.ReadIntAny("resource_group_member") ?? -1);
            uint internalFlags = (uint)(metadata.ReadIntAny("internal flags") ?? 0);
            bool worldRelative = ((internalFlags >> 1) & 1) == 1;

            var objectSpaceParents = ReadObjectSpaceParents(metadata);

            var (checksum, codecFrameCount, movementType, dataSizes, codecByte, blob) =
                ResolveMember(groupMemberTable, resourceGroup, resourceGroupMember);

            // Inline payload — older layouts store animation data / data sizes
            // directly on the animation block element.
            if (blob.Length == 0 && dataSizes is null)
            {
                var inline = game == Game.Halo2 ? ReadH2AnimationData(metadata) : ReadInlineAnimationData(metadata);
                if (inline is not null) { blob = inline; codecByte = blob.Length > 0 ? blob[0] : null; }
                // Halo 2 stores section sizes as an unnamed 7-field struct
                // (pool-block v1-v5) or separate inline fields (v0); both need
                // positional/explicit-name handling, not the H3 named lookup.
                dataSizes = game == Game.Halo2 ? ReadH2DataSizes(metadata) : ReadPackedDataSizes(metadata);
                movementType ??= frameInfoType;
                checksum ??= (int?)metadata.ReadIntAny("production checksum");
                codecFrameCount ??= frameCount;
            }

            animations.Add(new AnimationGroup
            {
                Index = i,
                Name = name,
                AnimationType = animationType,
                FrameInfoType = frameInfoType,
                FrameCount = frameCount,
                NodeCount = nodeCount,
                NodeListChecksum = nodeListChecksum,
                ResourceGroup = resourceGroup,
                ResourceGroupMember = resourceGroupMember,
                Checksum = checksum,
                CodecFrameCount = codecFrameCount,
                MovementType = movementType,
                DataSizes = dataSizes,
                CodecByte = codecByte,
                Blob = blob,
                WorldRelative = worldRelative,
                ObjectSpaceParents = objectSpaceParents,
                SharedStatic = sharedStatic,
            });
        }

        string? parent = null;
        if (root.FieldPath($"{topPrefix}/parent animation graph")?.Value is TagFieldData.TagReference tr)
        {
            var gan = tr.Value.GroupTagAndName;
            if (gan is { } g && !string.IsNullOrEmpty(g.Name)) parent = g.Name;
        }

        return new Animation(animations, parent);
    }

    public int Count => _animations.Count;
    public bool IsEmpty => _animations.Count == 0;
    public IReadOnlyList<AnimationGroup> Groups => _animations;
    public AnimationGroup? Get(int index) => index >= 0 && index < _animations.Count ? _animations[index] : null;
    public AnimationGroup? Find(string name) => _animations.FirstOrDefault(a => a.Name == name);
    public string? Parent => _parent;
    public int UnresolvedCount => _animations.Count(a => a.Checksum is null);

    /// <summary>Resolve the composition base pose for an overlay/
    /// replacement <paramref name="group"/> — the first frame of the
    /// matching base animation its deltas were authored against. Returns
    /// <c>null</c> to mean "fall back to the rest/bind pose" (no scope,
    /// custom name, damage/transition state, <c>aim_spine</c> pose
    /// overlay, or no matching base found). Mirrors the Rust
    /// <c>Animation::overlay_base_pose</c> + Foundry's
    /// <c>_get_base_animation_candidates</c>.</summary>
    public IReadOnlyList<NodeTransform>? OverlayBasePose(
        AnimationGraph graph, AnimationGroup group, Skeleton skeleton, IReadOnlyList<NodeTransform> defaults)
    {
        if (group.Name is null) return null;
        var name = AnimationName.Parse(group.Name);
        if (!name.Valid || name.Custom || name.StateType != AnimationStateType.Action) return null;
        // POSE_OVERLAY_REST_BASE_STATES — `aim_spine` pose overlays
        // compose against rest, not a base animation.
        if (name.State == "aim_spine") return null;

        foreach (var state in AnimationName.BaseStateCandidates(name.State))
        {
            if (graph.FindAction(name.Mode, name.WeaponClass, name.WeaponType, name.Set, state) is not { } act)
                continue;
            if (!act.IsLocal || act.AnimationIndex < 0) continue;
            int idx = act.AnimationIndex;
            if (idx == group.Index) continue;
            if (Get(idx) is not { } baseGroup) continue;
            // Only base/none-type animations are valid composition bases.
            if (baseGroup.AnimationType is "overlay" or "replacement") continue;
            AnimationClip baseClip;
            try { baseClip = baseGroup.Decode(); }
            catch (AnimationException) { continue; }
            // Frame 0 of the base, posed against the rest defaults.
            var frames = baseClip.Pose(skeleton, defaults).Frames;
            if (frames.Count > 0) return frames[0];
        }
        return null;
    }

    private static (int? Checksum, short? CodecFrameCount, string? MovementType, PackedDataSizes? DataSizes, byte? CodecByte, byte[] Blob)
        ResolveMember(List<List<TagStruct>?> table, short rg, short rgm)
    {
        if (rg < 0 || rgm < 0) return (null, null, null, null, null, []);
        if (rg >= table.Count || table[rg] is not { } members) return (null, null, null, null, null, []);
        if (rgm >= members.Count) return (null, null, null, null, null, []);
        var member = members[rgm];

        int? checksum = (int?)member.ReadIntAny("animation_checksum");
        short? codecFrameCount = (short?)member.ReadIntAny("frame count");
        string? movementType = member.ReadEnumName("movement_data_type");
        var dataSizes = ReadPackedDataSizes(member);
        byte[] blob = member.Field("animation_data")?.AsData() ?? [];
        byte? codecByte = blob.Length > 0 ? blob[0] : null;
        return (checksum, codecFrameCount, movementType, dataSizes, codecByte, blob);
    }

    private static byte[]? ReadInlineAnimationData(TagStruct anim) =>
        anim.Field("animation data")?.AsData() ?? anim.Field("animation_data")?.AsData();

    /// <summary>Parse the <c>object-space parent nodes</c> block (empty for
    /// H3; populated for Reach/H4 pose overlays and H2 replacement anims).
    /// Component flags are ignored — we always apply the full orientation
    /// (int16 rotation x/y/z/w / 0x7FFF, real translation, real scale).
    /// Port of the Rust <c>read_object_space_parents</c>.</summary>
    private static IReadOnlyList<ObjectSpaceParentNode> ReadObjectSpaceParents(TagStruct metadata)
    {
        var block = metadata.Field("object-space parent nodes")?.AsBlock();
        if (block is null) return [];
        var outList = new List<ObjectSpaceParentNode>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var elem = block.Element(i);
            if (elem is null) continue;
            short nodeIndex = (short)(elem.ReadIntAny("node_index") ?? elem.ReadIntAny("node index") ?? -1);
            var orient = (elem.Field("parent orientation") ?? elem.Field("orientation"))?.AsStruct();
            if (orient is null) continue;
            float Q(string name) => (orient.ReadIntAny(name) ?? 0) / 32767.0f;
            var rotation = new RealQuaternion(Q("rotation x"), Q("rotation y"), Q("rotation z"), Q("rotation w"));
            rotation = System.MathF.Sqrt(rotation.LengthSquared()) <= 1e-6f ? new RealQuaternion(0, 0, 0, 1) : rotation.Normalized();
            outList.Add(new ObjectSpaceParentNode(
                nodeIndex,
                orient.ReadPoint3d("default translation"),
                rotation,
                orient.ReadReal("default scale") ?? 1.0f));
        }
        return outList;
    }

    private static PackedDataSizes? ReadPackedDataSizes(TagStruct member)
    {
        var s = member.Field("data sizes")?.AsStruct();
        if (s is null) return null;
        var fields = new List<(string, long)>();
        foreach (var f in s.Fields())
        {
            string name = f.Name;
            if (s.ReadIntAny(name) is { } v) fields.Add((name, v));
        }
        return new PackedDataSizes { Fields = fields };
    }

    /// <summary>Read a Halo 2 animation_pool_block element's <c>animation
    /// data</c> blob. Pool-block v5 (the base struct) stores the field with a
    /// NULL name (generator artifact); fall back to the element's sole
    /// <c>data</c>-typed field.</summary>
    private static byte[]? ReadH2AnimationData(TagStruct anim)
    {
        var d = anim.Field("animation data")?.AsData() ?? anim.Field("animation_data")?.AsData();
        if (d is not null) return d;
        foreach (var f in anim.Fields())
            if (f.AsData() is { } b) return b;
        return null;
    }

    /// <summary>Normalize a Halo 2 animation pool block's section sizes to the
    /// positional order the decoder expects (<c>[static_codec, animated_codec,
    /// static_flags, animated_flags, movement, pill]</c>, identical to Reach).
    /// v1-v5 carry an unnamed 7-field <c>data sizes</c> struct (order
    /// StaticNodeFlags(b) AnimatedNodeFlags(b) MovementData(s) PillOffsetData(s)
    /// StaticDataSize(s) UncompressedDataSize(i) CompressedDataSize(i)); v0
    /// stores the same as separate inline fields. <c>animated_codec =
    /// uncompressed + compressed</c>.</summary>
    /// <summary>Read Halo 4's graph-level shared-static value pool from
    /// <c>codec data/shared_static_codec/{rotations,translations,scale}</c>.
    /// <c>null</c> when absent (non-H4 / no shared-static). Rotations are
    /// 4×int16/0x7FFF quaternions; translations are (x,y,z) f32; scales f32.</summary>
    private static SharedStaticPool? ReadSharedStaticPool(TagStruct root)
    {
        const string Base = "codec data/shared_static_codec";
        var rotations = root.FieldPath($"{Base}/rotations")?.AsBlock();
        if (rotations is null) return null;
        var pool = new SharedStaticPool();
        foreach (var e in rotations.Elements())
        {
            float G(string n) => (e.ReadIntAny(n) ?? 0) / 32767.0f;
            var q = new RealQuaternion(G("i"), G("j"), G("k"), G("w"));
            pool.Rotations.Add(System.MathF.Sqrt(q.LengthSquared()) <= 1e-6f ? new RealQuaternion(0, 0, 0, 1) : q.Normalized());
        }
        if (root.FieldPath($"{Base}/translations")?.AsBlock() is { } tb)
            foreach (var e in tb.Elements())
                pool.Translations.Add(new RealPoint3d(e.ReadReal("x") ?? 0, e.ReadReal("y") ?? 0, e.ReadReal("z") ?? 0));
        if (root.FieldPath($"{Base}/scale")?.AsBlock() is { } sb)
            foreach (var e in sb.Elements())
                pool.Scales.Add(e.ReadReal("scale") ?? 1.0f);
        return pool;
    }

    private static PackedDataSizes? ReadH2DataSizes(TagStruct anim)
    {
        // The played/extracted animated codec stream is the COMPRESSED block
        // (laid out right after the static block, with the node flags directly
        // after it); the trailing UNCOMPRESSED block is an unplayed lossless
        // mirror — NOT part of the animated stream and NOT counted toward the
        // flag offset. Summing the two pushed the flag offset ~14 KB into the
        // mirror and scrambled every node's transform. Fall back to
        // uncompressed only when there is no compressed block.
        static long AnimatedStream(long uncompressed, long compressed) =>
            compressed > 0 ? compressed : uncompressed;

        PackedDataSizes Build(long staticData, long animated, long staticFlags, long animatedFlags, long movement, long pill) =>
            new()
            {
                Fields =
                [
                    ("h2_static_data", staticData), ("h2_animated_data", animated),
                    ("h2_static_flags", staticFlags), ("h2_animated_flags", animatedFlags),
                    ("h2_movement", movement), ("h2_pill", pill),
                ],
            };

        // v1-v5: the `data sizes` struct (named, or the element's sole struct
        // field when v5 leaves the name null), read positionally.
        var s = anim.Field("data sizes")?.AsStruct() ?? anim.Fields().Select(f => f.AsStruct()).FirstOrDefault(x => x is not null);
        if (s is not null)
        {
            var vals = new List<long>();
            foreach (var f in s.Fields())
                if (f.Value is { } v && IntValue(v) is { } iv) vals.Add(iv);
            if (vals.Count >= 7)
                return Build(vals[4], AnimatedStream(vals[5], vals[6]), vals[0], vals[1], vals[2], vals[3]);
        }

        // v0: separate inline size fields.
        long? staticFlags0 = anim.ReadIntAny("static node flag data size");
        long? animatedFlags0 = anim.ReadIntAny("animated node flag data size");
        if (staticFlags0 is null || animatedFlags0 is null) return null;
        return Build(
            anim.ReadIntAny("default_data size") ?? 0,
            AnimatedStream(anim.ReadIntAny("uncompressed_data size") ?? 0, anim.ReadIntAny("compressed_data size") ?? 0),
            staticFlags0.Value, animatedFlags0.Value, anim.ReadIntAny("movement_data size") ?? 0, 0);
    }

    /// <summary>Integer value from any integer-shaped <see cref="TagFieldData"/>
    /// (reads the unnamed Halo 2 <c>data sizes</c> fields by position).</summary>
    internal static long? IntValue(TagFieldData v) => v switch
    {
        TagFieldData.CharInteger x => x.Value,
        TagFieldData.ShortInteger x => x.Value,
        TagFieldData.LongInteger x => x.Value,
        TagFieldData.Int64Integer x => x.Value,
        TagFieldData.ByteInteger x => x.Value,
        TagFieldData.WordInteger x => x.Value,
        TagFieldData.DwordInteger x => x.Value,
        TagFieldData.QwordInteger x => (long)x.Value,
        _ => null,
    };
}

//================================================================
// Decoded data types (produced by the codec, consumed by pose + JMA)
//================================================================

/// <summary>One codec stream's decoded transforms, indexed
/// <c>[codecNode][frame]</c>. Translations are codec-native (no ×100).</summary>
public sealed class AnimationTracks
{
    public required Codec Codec { get; init; }
    public required ushort FrameCount { get; init; }
    public List<List<RealQuaternion>> Rotations { get; init; } = new();
    public List<List<RealPoint3d>> Translations { get; init; } = new();
    public List<List<float>> Scales { get; init; } = new();
}

/// <summary>Outcome of decoding the animated stream.</summary>
public abstract record AnimatedStreamStatus
{
    private AnimatedStreamStatus() { }
    public sealed record NoAnimatedStream : AnimatedStreamStatus;
    public sealed record Decoded : AnimatedStreamStatus;
    public sealed record Unsupported(Codec Codec) : AnimatedStreamStatus;
    public sealed record Unknown(byte Byte) : AnimatedStreamStatus;
}

/// <summary>A fully-decoded animation: static rest-pose stream + optional
/// per-frame animated stream, node-flag bitarrays, and movement.</summary>
public sealed class AnimationClip
{
    public required ushort FrameCount { get; init; }
    public required AnimationTracks StaticTracks { get; init; }
    public AnimationTracks? AnimatedTracks { get; init; }
    public required AnimatedStreamStatus AnimatedStatus { get; init; }
    public NodeFlags? NodeFlags { get; init; }
    public MovementData Movement { get; init; } = new();

    /// <summary>Compose static + animated tracks against the skeleton using the
    /// per-component flag bitarrays. <paramref name="defaults"/> supplies the
    /// rest pose for bones flagged neither static nor animated.</summary>
    public Pose Pose(Skeleton skeleton, IReadOnlyList<NodeTransform>? defaults) =>
        PoseComposer.Compose(this, skeleton, defaults);

    /// <summary>Compose an overlay (delta) animation onto <paramref name="base"/>.
    /// Returns the leading reference frame and the composed body.</summary>
    public (List<NodeTransform> Reference, Pose Body) OverlayPose(Skeleton skeleton, IReadOnlyList<NodeTransform> @base) =>
        PoseComposer.OverlayPose(this, skeleton, @base);

    /// <summary>Compose a replacement animation against <paramref name="base"/>.</summary>
    public Pose ReplacementPose(Skeleton skeleton, IReadOnlyList<NodeTransform> @base) =>
        PoseComposer.ReplacementPose(this, skeleton, @base);
}

public enum MovementKind { None, DxDy, DxDyDyaw, DxDyDzDyaw, DxDyDzDangleAxis, XyzAbsolute }

public static class MovementKindExtensions
{
    public static int BytesPerFrame(this MovementKind k) => k switch
    {
        MovementKind.None => 0,
        MovementKind.DxDy => 8,
        MovementKind.DxDyDyaw => 12,
        MovementKind.DxDyDzDyaw => 16,
        MovementKind.DxDyDzDangleAxis => 24,
        MovementKind.XyzAbsolute => 12,
        _ => 0,
    };

    /// <summary><c>true</c> when this kind drives the root bone's
    /// <i>position</i> absolutely (replacing the accumulator) rather than
    /// as a per-frame delta — <c>xyz_absolute</c> (Reach+).</summary>
    public static bool IsAbsolute(this MovementKind k) => k == MovementKind.XyzAbsolute;

    public static MovementKind FromSchemaName(string name) => name switch
    {
        "dx,dy" => MovementKind.DxDy,
        "dx,dy,dyaw" => MovementKind.DxDyDyaw,
        "dx,dy,dz,dyaw" => MovementKind.DxDyDzDyaw,
        "dx,dy,dz,dangle_axis" => MovementKind.DxDyDzDangleAxis,
        "xyz,absolute" or "xyz_absolute" or "x,y,z,absolute" => MovementKind.XyzAbsolute,
        _ => MovementKind.None,
    };
}

public struct MovementFrame
{
    public float Dx, Dy, Dz, Dyaw;
    /// <summary>Per-frame rotation delta as a quaternion (yaw for the dyaw
    /// kinds, angle-axis for dangle_axis, identity otherwise). The writer
    /// accumulates these as quaternions — matching the Rust/Foundry fold —
    /// rather than summing the scalar yaw, so the byte output matches.</summary>
    public RealQuaternion Rotation = new(0, 0, 0, 1);

    public MovementFrame() { }
}

public sealed class MovementData
{
    public MovementKind Kind { get; init; } = MovementKind.None;
    public List<MovementFrame> Frames { get; init; } = new();
}

/// <summary>Per-component node-flag bitarrays for static + animated streams.</summary>
public sealed class NodeFlags
{
    public BitArray StaticRotation { get; set; } = new();
    public BitArray StaticTranslation { get; set; } = new();
    public BitArray StaticScale { get; set; } = new();
    public BitArray AnimatedRotation { get; set; } = new();
    public BitArray AnimatedTranslation { get; set; } = new();
    public BitArray AnimatedScale { get; set; } = new();
}

/// <summary>Tightly-packed bit array (u32 words, little-endian on disk).</summary>
public sealed class BitArray
{
    public uint[] Words { get; init; } = [];

    public static BitArray FromBytes(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length / 4;
        var words = new uint[n];
        for (int i = 0; i < n; i++)
            words[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
        return new BitArray { Words = words };
    }

    /// <summary>Two-word bit array from a 64-bit mask (low dword = bits
    /// 0–31, high dword = bits 32–63) — used by the Halo CE antr path,
    /// whose node-flag masks are two <c>long_integer</c>s.</summary>
    public static BitArray FromU64(ulong mask) =>
        new() { Words = [(uint)(mask & 0xFFFFFFFF), (uint)(mask >> 32)] };

    public bool Bit(int index)
    {
        int w = index / 32, b = index % 32;
        return w < Words.Length && (Words[w] & (1u << b)) != 0;
    }

    public int PopcountBelow(int bound)
    {
        int fullWords = bound / 32;
        uint count = 0;
        for (int i = 0; i < fullWords && i < Words.Length; i++)
            count += (uint)System.Numerics.BitOperations.PopCount(Words[i]);
        if (fullWords < Words.Length)
        {
            int trailing = bound % 32;
            if (trailing > 0)
            {
                uint mask = (1u << trailing) - 1;
                count += (uint)System.Numerics.BitOperations.PopCount(Words[fullWords] & mask);
            }
        }
        return (int)count;
    }
}
