namespace BlamTags;

/// <summary>
/// Parsed per-field value — a closed set of case types, switched on with
/// C# pattern matching. Carries only <em>values</em>: container field types
/// (struct, block, array, pageable_resource) are navigated through the data
/// tree instead, so they have no case here.
/// </summary>
/// <remarks>
/// Enum and flags cases carry the raw integer <em>and</em> resolved name(s);
/// names are informational — serialization writes only the integer.
/// Sub-chunk-bearing cases (string-id, tag-reference, data, api-interop)
/// carry their parsed payload.
/// </remarks>
public abstract record TagFieldData
{
    private TagFieldData() { }

    // Strings (fixed-size, null-padded on the wire).
    public sealed record String(string Value) : TagFieldData;
    public sealed record LongString(string Value) : TagFieldData;

    // Sub-chunk leaves.
    public sealed record StringId(StringIdData Value) : TagFieldData;
    public sealed record OldStringId(StringIdData Value) : TagFieldData;
    public sealed record TagReference(TagReferenceData Value) : TagFieldData;
    public sealed record Data(byte[] Value) : TagFieldData;
    public sealed record ApiInterop(ApiInteropData Value) : TagFieldData;

    // Integers.
    public sealed record CharInteger(sbyte Value) : TagFieldData;
    public sealed record ShortInteger(short Value) : TagFieldData;
    public sealed record LongInteger(int Value) : TagFieldData;
    public sealed record Int64Integer(long Value) : TagFieldData;
    public sealed record ByteInteger(byte Value) : TagFieldData;
    public sealed record WordInteger(ushort Value) : TagFieldData;
    public sealed record DwordInteger(uint Value) : TagFieldData;
    public sealed record QwordInteger(ulong Value) : TagFieldData;
    public sealed record Tag(uint Value) : TagFieldData;

    // Enums: raw value + resolved variant name (null if out of range).
    public sealed record CharEnum(sbyte Value, string? Name) : TagFieldData;
    public sealed record ShortEnum(short Value, string? Name) : TagFieldData;
    public sealed record LongEnum(int Value, string? Name) : TagFieldData;

    // Flags: raw value + names of set bits (bit index + display name).
    public sealed record ByteFlags(byte Value, IReadOnlyList<(uint Bit, string Name)> Names) : TagFieldData;
    public sealed record WordFlags(ushort Value, IReadOnlyList<(uint Bit, string Name)> Names) : TagFieldData;
    public sealed record LongFlags(int Value, IReadOnlyList<(uint Bit, string Name)> Names) : TagFieldData;

    // Block flags: value only.
    public sealed record ByteBlockFlags(byte Value) : TagFieldData;
    public sealed record WordBlockFlags(ushort Value) : TagFieldData;
    public sealed record LongBlockFlags(int Value) : TagFieldData;

    // Block indices.
    public sealed record CharBlockIndex(sbyte Value) : TagFieldData;
    public sealed record CustomCharBlockIndex(sbyte Value) : TagFieldData;
    public sealed record ShortBlockIndex(short Value) : TagFieldData;
    public sealed record CustomShortBlockIndex(short Value) : TagFieldData;
    public sealed record LongBlockIndex(int Value) : TagFieldData;
    public sealed record CustomLongBlockIndex(int Value) : TagFieldData;

    // Floats.
    public sealed record Angle(float Value) : TagFieldData;
    public sealed record Real(float Value) : TagFieldData;
    public sealed record RealSlider(float Value) : TagFieldData;
    public sealed record RealFraction(float Value) : TagFieldData;

    // Math composites.
    public sealed record Point2dValue(Point2d Value) : TagFieldData;
    public sealed record Rectangle2dValue(Rectangle2d Value) : TagFieldData;
    public sealed record RealPoint2dValue(RealPoint2d Value) : TagFieldData;
    public sealed record RealPoint3dValue(RealPoint3d Value) : TagFieldData;
    public sealed record RealVector2dValue(RealVector2d Value) : TagFieldData;
    public sealed record RealVector3dValue(RealVector3d Value) : TagFieldData;
    public sealed record RealQuaternionValue(RealQuaternion Value) : TagFieldData;
    public sealed record RealEulerAngles2dValue(RealEulerAngles2d Value) : TagFieldData;
    public sealed record RealEulerAngles3dValue(RealEulerAngles3d Value) : TagFieldData;
    public sealed record RealPlane2dValue(RealPlane2d Value) : TagFieldData;
    public sealed record RealPlane3dValue(RealPlane3d Value) : TagFieldData;

    // Colors.
    public sealed record RgbColorValue(RgbColor Value) : TagFieldData;
    public sealed record ArgbColorValue(ArgbColor Value) : TagFieldData;
    public sealed record RealRgbColorValue(RealRgbColor Value) : TagFieldData;
    public sealed record RealArgbColorValue(RealArgbColor Value) : TagFieldData;
    public sealed record RealHsvColorValue(RealHsvColor Value) : TagFieldData;
    public sealed record RealAhsvColorValue(RealAhsvColor Value) : TagFieldData;

    // Bounds.
    public sealed record ShortIntegerBounds(Bounds<short> Value) : TagFieldData;
    public sealed record AngleBounds(Bounds<float> Value) : TagFieldData;
    public sealed record RealBounds(Bounds<float> Value) : TagFieldData;
    public sealed record FractionBounds(Bounds<float> Value) : TagFieldData;

    // Opaque.
    public sealed record Custom(byte[] Value) : TagFieldData;

    /// <summary>Read a single bit from a flags-shaped case (including block
    /// flags). Returns null for non-flags cases.</summary>
    public bool? FlagBit(int bit) => this switch
    {
        ByteFlags f => (f.Value & (1UL << bit)) != 0,
        WordFlags f => (f.Value & (1UL << bit)) != 0,
        LongFlags f => ((uint)f.Value & (1UL << bit)) != 0,
        ByteBlockFlags f => (f.Value & (1UL << bit)) != 0,
        WordBlockFlags f => (f.Value & (1UL << bit)) != 0,
        LongBlockFlags f => ((uint)f.Value & (1UL << bit)) != 0,
        _ => null,
    };

    /// <summary>Return a copy with bit <paramref name="bit"/> set/cleared, for
    /// flags-shaped cases (names left stale — re-parse for accurate names).
    /// Returns null for non-flags cases.</summary>
    public TagFieldData? WithFlagBit(int bit, bool on)
    {
        ulong mask = 1UL << bit;
        static ulong Apply(ulong raw, ulong m, bool on) => on ? raw | m : raw & ~m;
        return this switch
        {
            ByteFlags f => f with { Value = (byte)Apply(f.Value, mask, on) },
            WordFlags f => f with { Value = (ushort)Apply(f.Value, mask, on) },
            LongFlags f => f with { Value = (int)(uint)Apply((uint)f.Value, mask, on) },
            ByteBlockFlags f => f with { Value = (byte)Apply(f.Value, mask, on) },
            WordBlockFlags f => f with { Value = (ushort)Apply(f.Value, mask, on) },
            LongBlockFlags f => f with { Value = (int)(uint)Apply((uint)f.Value, mask, on) },
            _ => null,
        };
    }
}
