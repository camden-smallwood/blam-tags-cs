namespace BlamTags;

/// <summary>Decode failure for <see cref="Animation"/> / animation codecs —
/// the genuinely-malformed cases (truncated headers, bad codec byte, no
/// payload). Recognized-but-undecodable animated streams are reported on the
/// clip via <see cref="AnimatedStreamStatus"/> instead of throwing.</summary>
public sealed class AnimationException(string message) : Exception(message);

/// <summary>Engine-version flavor of the <c>data sizes</c> struct.</summary>
public enum SizeLayout { H3, Reach }

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

    public bool MovementTypeMismatch =>
        FrameInfoType is { } a && MovementType is { } b && a != b;

    /// <summary>Decode the blob into an <see cref="AnimationClip"/>.</summary>
    public AnimationClip Decode() => AnimationCodec.Decode(this);
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

            var (checksum, codecFrameCount, movementType, dataSizes, codecByte, blob) =
                ResolveMember(groupMemberTable, resourceGroup, resourceGroupMember);

            // Inline payload — older layouts store animation data / data sizes
            // directly on the animation block element.
            if (blob.Length == 0 && dataSizes is null)
            {
                var inline = ReadInlineAnimationData(metadata);
                if (inline is not null) { blob = inline; codecByte = blob.Length > 0 ? blob[0] : null; }
                dataSizes = ReadPackedDataSizes(metadata);
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
}

public enum MovementKind { None, DxDy, DxDyDyaw, DxDyDzDyaw, DxDyDzDangleAxis }

public static class MovementKindExtensions
{
    public static int BytesPerFrame(this MovementKind k) => k switch
    {
        MovementKind.None => 0,
        MovementKind.DxDy => 8,
        MovementKind.DxDyDyaw => 12,
        MovementKind.DxDyDzDyaw => 16,
        MovementKind.DxDyDzDangleAxis => 24,
        _ => 0,
    };

    public static MovementKind FromSchemaName(string name) => name switch
    {
        "dx,dy" => MovementKind.DxDy,
        "dx,dy,dyaw" => MovementKind.DxDyDyaw,
        "dx,dy,dz,dyaw" => MovementKind.DxDyDzDyaw,
        "dx,dy,dz,dangle_axis" => MovementKind.DxDyDzDangleAxis,
        _ => MovementKind.None,
    };
}

public struct MovementFrame
{
    public float Dx, Dy, Dz, Dyaw;
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
