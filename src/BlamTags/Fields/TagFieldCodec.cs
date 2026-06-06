using System.Buffers.Binary;
using System.Text;

namespace BlamTags;

/// <summary>
/// Parses a single field's value out of its struct's raw bytes (+ owning
/// sub-chunk) and serializes a <see cref="TagFieldData"/> back. Primitive
/// reads dispatch on endian; writes are always little-endian (the library
/// never serializes a big-endian tag back to disk). Mirrors the Rust
/// <c>deserialize_field</c> / <c>serialize_field</c>.
/// </summary>
internal static class TagFieldCodec
{
    private static readonly UTF8Encoding Utf8Lenient = new(false, throwOnInvalidBytes: false);

    /// <summary>Parse the value of a single field. Returns null for field
    /// types that carry no standalone value (containers, padding, etc.).</summary>
    public static TagFieldData? Deserialize(
        TagLayout layout, TagFieldLayout field, ReadOnlySpan<byte> raw,
        TagSubChunkContent? subChunk, Endian e)
    {
        int o = (int)field.Offset;

        switch (field.FieldType)
        {
            // No value / containers / not-modeled.
            case TagFieldType.Unknown:
            case TagFieldType.Pad:
            case TagFieldType.UselessPad:
            case TagFieldType.Skip:
            case TagFieldType.Explanation:
            case TagFieldType.Terminator:
            case TagFieldType.NonCacheRuntimeValue:
            case TagFieldType.Struct:
            case TagFieldType.Block:
            case TagFieldType.Array:
            case TagFieldType.PageableResource:
            case TagFieldType.VertexBuffer:
                return null;

            case TagFieldType.String:
                return new TagFieldData.String(DecodeNullPadded(raw.Slice(o, 32)));
            case TagFieldType.LongString:
                return new TagFieldData.LongString(DecodeNullPadded(raw.Slice(o, 256)));

            case TagFieldType.CharInteger: return new TagFieldData.CharInteger((sbyte)raw[o]);
            case TagFieldType.ShortInteger: return new TagFieldData.ShortInteger(I16(raw, o, e));
            case TagFieldType.LongInteger: return new TagFieldData.LongInteger(I32(raw, o, e));
            case TagFieldType.Int64Integer: return new TagFieldData.Int64Integer(I64(raw, o, e));
            case TagFieldType.ByteInteger: return new TagFieldData.ByteInteger(raw[o]);
            case TagFieldType.WordInteger: return new TagFieldData.WordInteger(U16(raw, o, e));
            case TagFieldType.DwordInteger: return new TagFieldData.DwordInteger(U32(raw, o, e));
            case TagFieldType.QwordInteger: return new TagFieldData.QwordInteger(U64(raw, o, e));
            case TagFieldType.Tag: return new TagFieldData.Tag(U32(raw, o, e));

            case TagFieldType.CharEnum:
            {
                sbyte v = (sbyte)raw[o];
                return new TagFieldData.CharEnum(v, ResolveEnumName(layout, field, v));
            }
            case TagFieldType.ShortEnum:
            {
                short v = I16(raw, o, e);
                return new TagFieldData.ShortEnum(v, ResolveEnumName(layout, field, v));
            }
            case TagFieldType.LongEnum:
            {
                int v = I32(raw, o, e);
                return new TagFieldData.LongEnum(v, ResolveEnumName(layout, field, v));
            }

            case TagFieldType.ByteFlags:
            {
                byte v = raw[o];
                return new TagFieldData.ByteFlags(v, ResolveFlagNames(layout, field, v, 8));
            }
            case TagFieldType.WordFlags:
            {
                ushort v = U16(raw, o, e);
                return new TagFieldData.WordFlags(v, ResolveFlagNames(layout, field, v, 16));
            }
            case TagFieldType.LongFlags:
            {
                int v = I32(raw, o, e);
                return new TagFieldData.LongFlags(v, ResolveFlagNames(layout, field, (uint)v, 32));
            }

            case TagFieldType.ByteBlockFlags: return new TagFieldData.ByteBlockFlags(raw[o]);
            case TagFieldType.WordBlockFlags: return new TagFieldData.WordBlockFlags(U16(raw, o, e));
            case TagFieldType.LongBlockFlags: return new TagFieldData.LongBlockFlags(I32(raw, o, e));

            case TagFieldType.CharBlockIndex: return new TagFieldData.CharBlockIndex((sbyte)raw[o]);
            case TagFieldType.CustomCharBlockIndex: return new TagFieldData.CustomCharBlockIndex((sbyte)raw[o]);
            case TagFieldType.ShortBlockIndex: return new TagFieldData.ShortBlockIndex(I16(raw, o, e));
            case TagFieldType.CustomShortBlockIndex: return new TagFieldData.CustomShortBlockIndex(I16(raw, o, e));
            case TagFieldType.LongBlockIndex: return new TagFieldData.LongBlockIndex(I32(raw, o, e));
            case TagFieldType.CustomLongBlockIndex: return new TagFieldData.CustomLongBlockIndex(I32(raw, o, e));

            case TagFieldType.Angle: return new TagFieldData.Angle(F32(raw, o, e));
            case TagFieldType.Real: return new TagFieldData.Real(F32(raw, o, e));
            case TagFieldType.RealSlider: return new TagFieldData.RealSlider(F32(raw, o, e));
            case TagFieldType.RealFraction: return new TagFieldData.RealFraction(F32(raw, o, e));

            case TagFieldType.Point2d:
                return new TagFieldData.Point2dValue(new Point2d(I16(raw, o, e), I16(raw, o + 2, e)));
            case TagFieldType.Rectangle2d:
                return new TagFieldData.Rectangle2dValue(new Rectangle2d(
                    I16(raw, o, e), I16(raw, o + 2, e), I16(raw, o + 4, e), I16(raw, o + 6, e)));
            case TagFieldType.RealPoint2d:
                return new TagFieldData.RealPoint2dValue(new RealPoint2d(F32(raw, o, e), F32(raw, o + 4, e)));
            case TagFieldType.RealPoint3d:
                return new TagFieldData.RealPoint3dValue(new RealPoint3d(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealVector2d:
                return new TagFieldData.RealVector2dValue(new RealVector2d(F32(raw, o, e), F32(raw, o + 4, e)));
            case TagFieldType.RealVector3d:
                return new TagFieldData.RealVector3dValue(new RealVector3d(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealQuaternion:
                return new TagFieldData.RealQuaternionValue(new RealQuaternion(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e), F32(raw, o + 12, e)));
            case TagFieldType.RealEulerAngles2d:
                return new TagFieldData.RealEulerAngles2dValue(new RealEulerAngles2d(F32(raw, o, e), F32(raw, o + 4, e)));
            case TagFieldType.RealEulerAngles3d:
                return new TagFieldData.RealEulerAngles3dValue(new RealEulerAngles3d(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealPlane2d:
                return new TagFieldData.RealPlane2dValue(new RealPlane2d(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealPlane3d:
                return new TagFieldData.RealPlane3dValue(new RealPlane3d(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e), F32(raw, o + 12, e)));

            case TagFieldType.RgbColor:
                return new TagFieldData.RgbColorValue(new RgbColor(U32(raw, o, e)));
            case TagFieldType.ArgbColor:
                return new TagFieldData.ArgbColorValue(new ArgbColor(U32(raw, o, e)));
            case TagFieldType.RealRgbColor:
                return new TagFieldData.RealRgbColorValue(new RealRgbColor(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealArgbColor:
                return new TagFieldData.RealArgbColorValue(new RealArgbColor(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e), F32(raw, o + 12, e)));
            case TagFieldType.RealHsvColor:
                return new TagFieldData.RealHsvColorValue(new RealHsvColor(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e)));
            case TagFieldType.RealAhsvColor:
                return new TagFieldData.RealAhsvColorValue(new RealAhsvColor(F32(raw, o, e), F32(raw, o + 4, e), F32(raw, o + 8, e), F32(raw, o + 12, e)));

            case TagFieldType.ShortIntegerBounds:
                return new TagFieldData.ShortIntegerBounds(new Bounds<short>(I16(raw, o, e), I16(raw, o + 2, e)));
            case TagFieldType.AngleBounds:
                return new TagFieldData.AngleBounds(new Bounds<float>(F32(raw, o, e), F32(raw, o + 4, e)));
            case TagFieldType.RealBounds:
                return new TagFieldData.RealBounds(new Bounds<float>(F32(raw, o, e), F32(raw, o + 4, e)));
            case TagFieldType.FractionBounds:
                return new TagFieldData.FractionBounds(new Bounds<float>(F32(raw, o, e), F32(raw, o + 4, e)));

            case TagFieldType.Custom:
            {
                int size = (int)layout.FieldTypes[(int)field.TypeIndex].Size;
                return new TagFieldData.Custom(raw.Slice(o, size).ToArray());
            }

            // Sub-chunk leaves — a missing sub-chunk returns null.
            case TagFieldType.StringId:
                return subChunk is TagSubChunkContent.StringIdContent sid
                    ? new TagFieldData.StringId(StringIdData.FromBytes(sid.Payload)) : null;
            case TagFieldType.OldStringId:
                return subChunk is TagSubChunkContent.OldStringIdContent osid
                    ? new TagFieldData.OldStringId(StringIdData.FromBytes(osid.Payload)) : null;
            case TagFieldType.TagReference:
                return subChunk is TagSubChunkContent.TagReferenceContent tr
                    ? new TagFieldData.TagReference(TagReferenceData.FromBytes(tr.Payload, e)) : null;
            case TagFieldType.Data:
                return subChunk is TagSubChunkContent.DataContent d
                    ? new TagFieldData.Data((byte[])d.Payload.Clone()) : null;
            case TagFieldType.ApiInterop:
                return subChunk is TagSubChunkContent.ApiInteropContent ai
                    ? new TagFieldData.ApiInterop(ApiInteropData.FromBytes(ai.Payload, e)) : null;

            default:
                return null;
        }
    }

    /// <summary>Serialize a value back to its on-disk form. Primitive/math
    /// variants write into <paramref name="raw"/> at the field offset (LE) and
    /// return null; sub-chunk-leaf variants return new content for the caller
    /// to swap in.</summary>
    public static TagSubChunkContent? Serialize(TagFieldLayout field, TagFieldData value, Span<byte> raw)
    {
        int o = (int)field.Offset;
        switch (value)
        {
            case TagFieldData.String s: EncodeNullPadded(s.Value, raw.Slice(o, 32)); return null;
            case TagFieldData.LongString s: EncodeNullPadded(s.Value, raw.Slice(o, 256)); return null;

            case TagFieldData.CharInteger v: raw[o] = (byte)v.Value; return null;
            case TagFieldData.ShortInteger v: WI16(raw, o, v.Value); return null;
            case TagFieldData.LongInteger v: WI32(raw, o, v.Value); return null;
            case TagFieldData.Int64Integer v: WI64(raw, o, v.Value); return null;
            case TagFieldData.ByteInteger v: raw[o] = v.Value; return null;
            case TagFieldData.WordInteger v: WU16(raw, o, v.Value); return null;
            case TagFieldData.DwordInteger v: WU32(raw, o, v.Value); return null;
            case TagFieldData.QwordInteger v: WU64(raw, o, v.Value); return null;
            case TagFieldData.Tag v: WU32(raw, o, v.Value); return null;

            case TagFieldData.CharEnum v: raw[o] = (byte)v.Value; return null;
            case TagFieldData.ShortEnum v: WI16(raw, o, v.Value); return null;
            case TagFieldData.LongEnum v: WI32(raw, o, v.Value); return null;

            case TagFieldData.ByteFlags v: raw[o] = v.Value; return null;
            case TagFieldData.WordFlags v: WU16(raw, o, v.Value); return null;
            case TagFieldData.LongFlags v: WI32(raw, o, v.Value); return null;

            case TagFieldData.ByteBlockFlags v: raw[o] = v.Value; return null;
            case TagFieldData.WordBlockFlags v: WU16(raw, o, v.Value); return null;
            case TagFieldData.LongBlockFlags v: WI32(raw, o, v.Value); return null;

            case TagFieldData.CharBlockIndex v: raw[o] = (byte)v.Value; return null;
            case TagFieldData.CustomCharBlockIndex v: raw[o] = (byte)v.Value; return null;
            case TagFieldData.ShortBlockIndex v: WI16(raw, o, v.Value); return null;
            case TagFieldData.CustomShortBlockIndex v: WI16(raw, o, v.Value); return null;
            case TagFieldData.LongBlockIndex v: WI32(raw, o, v.Value); return null;
            case TagFieldData.CustomLongBlockIndex v: WI32(raw, o, v.Value); return null;

            case TagFieldData.Angle v: WF32(raw, o, v.Value); return null;
            case TagFieldData.Real v: WF32(raw, o, v.Value); return null;
            case TagFieldData.RealSlider v: WF32(raw, o, v.Value); return null;
            case TagFieldData.RealFraction v: WF32(raw, o, v.Value); return null;

            case TagFieldData.Point2dValue v:
                WI16(raw, o, v.Value.X); WI16(raw, o + 2, v.Value.Y); return null;
            case TagFieldData.Rectangle2dValue v:
                WI16(raw, o, v.Value.Top); WI16(raw, o + 2, v.Value.Left);
                WI16(raw, o + 4, v.Value.Bottom); WI16(raw, o + 6, v.Value.Right); return null;
            case TagFieldData.RealPoint2dValue v:
                WF32(raw, o, v.Value.X); WF32(raw, o + 4, v.Value.Y); return null;
            case TagFieldData.RealPoint3dValue v:
                WF32(raw, o, v.Value.X); WF32(raw, o + 4, v.Value.Y); WF32(raw, o + 8, v.Value.Z); return null;
            case TagFieldData.RealVector2dValue v:
                WF32(raw, o, v.Value.I); WF32(raw, o + 4, v.Value.J); return null;
            case TagFieldData.RealVector3dValue v:
                WF32(raw, o, v.Value.I); WF32(raw, o + 4, v.Value.J); WF32(raw, o + 8, v.Value.K); return null;
            case TagFieldData.RealQuaternionValue v:
                WF32(raw, o, v.Value.I); WF32(raw, o + 4, v.Value.J); WF32(raw, o + 8, v.Value.K); WF32(raw, o + 12, v.Value.W); return null;
            case TagFieldData.RealEulerAngles2dValue v:
                WF32(raw, o, v.Value.Yaw); WF32(raw, o + 4, v.Value.Pitch); return null;
            case TagFieldData.RealEulerAngles3dValue v:
                WF32(raw, o, v.Value.Yaw); WF32(raw, o + 4, v.Value.Pitch); WF32(raw, o + 8, v.Value.Roll); return null;
            case TagFieldData.RealPlane2dValue v:
                WF32(raw, o, v.Value.I); WF32(raw, o + 4, v.Value.J); WF32(raw, o + 8, v.Value.D); return null;
            case TagFieldData.RealPlane3dValue v:
                WF32(raw, o, v.Value.I); WF32(raw, o + 4, v.Value.J); WF32(raw, o + 8, v.Value.K); WF32(raw, o + 12, v.Value.D); return null;

            case TagFieldData.RgbColorValue v: WU32(raw, o, v.Value.Packed); return null;
            case TagFieldData.ArgbColorValue v: WU32(raw, o, v.Value.Packed); return null;
            case TagFieldData.RealRgbColorValue v:
                WF32(raw, o, v.Value.Red); WF32(raw, o + 4, v.Value.Green); WF32(raw, o + 8, v.Value.Blue); return null;
            case TagFieldData.RealArgbColorValue v:
                WF32(raw, o, v.Value.Alpha); WF32(raw, o + 4, v.Value.Red); WF32(raw, o + 8, v.Value.Green); WF32(raw, o + 12, v.Value.Blue); return null;
            case TagFieldData.RealHsvColorValue v:
                WF32(raw, o, v.Value.Hue); WF32(raw, o + 4, v.Value.Saturation); WF32(raw, o + 8, v.Value.Value); return null;
            case TagFieldData.RealAhsvColorValue v:
                WF32(raw, o, v.Value.Alpha); WF32(raw, o + 4, v.Value.Hue); WF32(raw, o + 8, v.Value.Saturation); WF32(raw, o + 12, v.Value.Value); return null;

            case TagFieldData.ShortIntegerBounds v:
                WI16(raw, o, v.Value.Lower); WI16(raw, o + 2, v.Value.Upper); return null;
            case TagFieldData.AngleBounds v:
                WF32(raw, o, v.Value.Lower); WF32(raw, o + 4, v.Value.Upper); return null;
            case TagFieldData.RealBounds v:
                WF32(raw, o, v.Value.Lower); WF32(raw, o + 4, v.Value.Upper); return null;
            case TagFieldData.FractionBounds v:
                WF32(raw, o, v.Value.Lower); WF32(raw, o + 4, v.Value.Upper); return null;

            case TagFieldData.Custom v:
                v.Value.CopyTo(raw.Slice(o, v.Value.Length)); return null;

            // Sub-chunk leaves.
            case TagFieldData.StringId v: return new TagSubChunkContent.StringIdContent(v.Value.ToBytes());
            case TagFieldData.OldStringId v: return new TagSubChunkContent.OldStringIdContent(v.Value.ToBytes());
            case TagFieldData.TagReference v: return new TagSubChunkContent.TagReferenceContent(v.Value.ToBytes());
            case TagFieldData.Data v: return new TagSubChunkContent.DataContent((byte[])v.Value.Clone());
            case TagFieldData.ApiInterop v: return new TagSubChunkContent.ApiInteropContent(v.Value.ToBytes());

            default:
                throw new InvalidOperationException($"unhandled field value {value.GetType().Name}");
        }
    }

    // ---- enum / flags name resolution ----

    public static string? ResolveEnumName(TagLayout layout, TagFieldLayout field, long value)
    {
        if ((int)field.Definition >= layout.StringLists.Count)
            return null;
        var list = layout.StringLists[(int)field.Definition];
        if (value < 0 || (uint)value >= list.Count)
            return null;
        int offsetIndex = (int)(list.First + (uint)value);
        if (offsetIndex >= layout.StringOffsets.Count)
            return null;
        return layout.GetString(layout.StringOffsets[offsetIndex]);
    }

    public static List<(uint Bit, string Name)> ResolveFlagNames(TagLayout layout, TagFieldLayout field, ulong value, uint totalBits)
    {
        var names = new List<(uint, string)>();
        if ((int)field.Definition >= layout.StringLists.Count)
            return names;
        var list = layout.StringLists[(int)field.Definition];
        for (uint bit = 0; bit < totalBits; bit++)
        {
            if ((value & (1UL << (int)bit)) == 0) continue;
            if (bit >= list.Count) continue;
            int offsetIndex = (int)(list.First + bit);
            if (offsetIndex >= layout.StringOffsets.Count) continue;
            string? name = layout.GetString(layout.StringOffsets[offsetIndex]);
            if (name is not null) names.Add((bit, name));
        }
        return names;
    }

    /// <summary>Map a flag name to its bit index (case-sensitive), or null.</summary>
    public static uint? FindFlagBit(TagLayout layout, TagFieldLayout field, string name)
    {
        if ((int)field.Definition >= layout.StringLists.Count)
            return null;
        var list = layout.StringLists[(int)field.Definition];
        for (uint bit = 0; bit < list.Count; bit++)
        {
            int offsetIndex = (int)(list.First + bit);
            if (offsetIndex >= layout.StringOffsets.Count) continue;
            if (layout.GetString(layout.StringOffsets[offsetIndex]) == name)
                return bit;
        }
        return null;
    }

    /// <summary>Resolve a flag bit's display name via the field's string list,
    /// or empty string.</summary>
    public static string FlagNameFromBit(TagLayout layout, TagFieldLayout field, uint bit)
    {
        if ((int)field.Definition >= layout.StringLists.Count)
            return "";
        var list = layout.StringLists[(int)field.Definition];
        if (bit >= list.Count)
            return "";
        int offsetIndex = (int)(list.First + bit);
        if (offsetIndex >= layout.StringOffsets.Count)
            return "";
        return layout.GetString(layout.StringOffsets[offsetIndex]) ?? "";
    }

    /// <summary>Option names of an enum/flags field, in declaration order
    /// (empty string for missing entries).</summary>
    public static IEnumerable<string> FieldOptionNames(TagLayout layout, TagFieldLayout field)
    {
        if ((int)field.Definition >= layout.StringLists.Count)
            yield break;
        var list = layout.StringLists[(int)field.Definition];
        for (uint i = list.First; i < list.First + list.Count; i++)
        {
            if (i >= layout.StringOffsets.Count) { yield return ""; continue; }
            yield return layout.GetString(layout.StringOffsets[(int)i]) ?? "";
        }
    }

    // ---- primitive helpers ----

    private static short I16(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadInt16LittleEndian(r.Slice(o, 2)) : BinaryPrimitives.ReadInt16BigEndian(r.Slice(o, 2));
    private static ushort U16(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(o, 2)) : BinaryPrimitives.ReadUInt16BigEndian(r.Slice(o, 2));
    private static int I32(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadInt32LittleEndian(r.Slice(o, 4)) : BinaryPrimitives.ReadInt32BigEndian(r.Slice(o, 4));
    private static uint U32(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4)) : BinaryPrimitives.ReadUInt32BigEndian(r.Slice(o, 4));
    private static long I64(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadInt64LittleEndian(r.Slice(o, 8)) : BinaryPrimitives.ReadInt64BigEndian(r.Slice(o, 8));
    private static ulong U64(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(o, 8)) : BinaryPrimitives.ReadUInt64BigEndian(r.Slice(o, 8));
    private static float F32(ReadOnlySpan<byte> r, int o, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadSingleLittleEndian(r.Slice(o, 4)) : BinaryPrimitives.ReadSingleBigEndian(r.Slice(o, 4));

    private static void WI16(Span<byte> r, int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(r.Slice(o, 2), v);
    private static void WU16(Span<byte> r, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(r.Slice(o, 2), v);
    private static void WI32(Span<byte> r, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(r.Slice(o, 4), v);
    private static void WU32(Span<byte> r, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(r.Slice(o, 4), v);
    private static void WI64(Span<byte> r, int o, long v) => BinaryPrimitives.WriteInt64LittleEndian(r.Slice(o, 8), v);
    private static void WU64(Span<byte> r, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(r.Slice(o, 8), v);
    private static void WF32(Span<byte> r, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(r.Slice(o, 4), v);

    private static string DecodeNullPadded(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        return Utf8Lenient.GetString(bytes[..end]);
    }

    private static void EncodeNullPadded(string s, Span<byte> dest)
    {
        dest.Clear();
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        int n = System.Math.Min(bytes.Length, dest.Length);
        bytes.AsSpan(0, n).CopyTo(dest);
    }
}
