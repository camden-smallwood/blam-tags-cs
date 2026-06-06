using System.Buffers.Binary;
using System.Text;

namespace BlamTags;

/// <summary>
/// Parsed form of a <c>tgrf</c> tag-reference chunk payload. A null
/// reference (payload shorter than the 4-byte group tag) has a null
/// <see cref="GroupTagAndName"/>; a resolved reference carries the group
/// tag plus the UTF-8 tag path.
/// </summary>
public sealed class TagReferenceData
{
    public (uint GroupTag, string Name)? GroupTagAndName { get; init; }

    /// <summary>Parse a <c>tgrf</c> payload (header already consumed). The
    /// 4-byte group-tag prefix is read in the file's wire endian; bad UTF-8
    /// in the path is decoded lossily.</summary>
    public static TagReferenceData FromBytes(ReadOnlySpan<byte> payload, Endian endian)
    {
        if (payload.Length < 4)
            return new TagReferenceData { GroupTagAndName = null };
        uint groupTag = endian == Endian.Le
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
            : BinaryPrimitives.ReadUInt32BigEndian(payload);
        string name = Encoding.UTF8.GetString(payload[4..]);
        return new TagReferenceData { GroupTagAndName = (groupTag, name) };
    }

    /// <summary>Serialize back to a <c>tgrf</c> payload (caller writes the
    /// header). The group tag is always emitted little-endian.</summary>
    public byte[] ToBytes()
    {
        if (GroupTagAndName is null)
            return [];
        var (groupTag, name) = GroupTagAndName.Value;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        var bytes = new byte[4 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, groupTag);
        nameBytes.CopyTo(bytes.AsSpan(4));
        return bytes;
    }
}

/// <summary>
/// Parsed form of a <c>ti][</c> api-interop chunk payload. The canonical
/// 12-byte shape matches BCS's <c>{ descriptor, address, definition_address }</c>;
/// <see cref="Raw"/> preserves the payload verbatim so non-12-byte variants
/// still roundtrip byte-exactly.
/// </summary>
public sealed class ApiInteropData
{
    public required byte[] Raw { get; init; }
    public required Endian Endian { get; init; }

    public static ApiInteropData FromBytes(ReadOnlySpan<byte> payload, Endian endian) =>
        new() { Raw = payload.ToArray(), Endian = endian };

    public byte[] ToBytes() => (byte[])Raw.Clone();

    /// <summary>BCS's reset pattern on save: <c>{ 0, UINT_MAX, 0 }</c>, 12 bytes LE.</summary>
    public static ApiInteropData Reset()
    {
        var raw = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), uint.MaxValue);
        return new ApiInteropData { Raw = raw, Endian = Endian.Le };
    }

    public uint? Descriptor => U32At(0);
    public uint? Address => U32At(4);
    public uint? DefinitionAddress => U32At(8);

    private uint? U32At(int offset)
    {
        if (Raw.Length < 12)
            return null;
        var s = Raw.AsSpan(offset, 4);
        return Endian == Endian.Le
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
    }
}

/// <summary>
/// Parsed form of a <c>tgsi</c> string-id chunk payload (used for both
/// string-id and old-string-id). Empty content represents
/// <c>string_id::NONE</c>.
/// </summary>
public sealed class StringIdData
{
    public required string Value { get; init; }

    public static StringIdData FromBytes(ReadOnlySpan<byte> payload) =>
        new() { Value = Encoding.UTF8.GetString(payload) };

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Value);
}
