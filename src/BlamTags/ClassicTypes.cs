namespace BlamTags;

/// <summary>
/// Which classic engine a loose tag belongs to. Selects signature byte order
/// and a family of per-field encoding quirks. The four Halo 2 variants are
/// distinguished by the offset-60 engine word (<c>ambl</c>/<c>LAMB</c>/
/// <c>MLAB</c>/<c>BLM!</c>, stored little-endian) — each turns on a different
/// set of "legacy" read rules (HABT's <c>HAS_LEGACY_{HEADER,STRINGS,PADDING}</c>).
/// </summary>
public enum ClassicEngine
{
    /// <summary>Halo 1 / Combat Evolved (Anniversary). Big-endian signature
    /// words, inline 32-byte strings, no string_id table.</summary>
    HaloCe,
    /// <summary>Halo 2 V1 (<c>ambl</c>). 12-byte block/struct headers, 32-byte
    /// inline <c>old_string_id</c>, <c>useless_pad</c> at its real length.</summary>
    Halo2V1,
    /// <summary>Halo 2 V2 (<c>LAMB</c>). 16-byte headers, 32-byte inline
    /// <c>old_string_id</c>, <c>useless_pad</c> at its real length.</summary>
    Halo2V2,
    /// <summary>Halo 2 V3 (<c>MLAB</c>). 16-byte headers, modern (4-byte +
    /// trailing) <c>old_string_id</c>, <c>useless_pad</c> at its real length.</summary>
    Halo2V3,
    /// <summary>Halo 2 V4 / latest (<c>BLM!</c>). The modern MCC form: 16-byte
    /// headers, 4-byte + trailing <c>old_string_id</c>, zero-length useless_pad.</summary>
    Halo2V4,
}

/// <summary>The 64-byte classic tag-file header (parsed fields only).</summary>
public sealed class ClassicHeader
{
    /// <summary>Logical group tag (e.g. <c>bitm</c>), un-reversed.</summary>
    public required byte[] GroupTag { get; init; }
    /// <summary>Logical engine signature (e.g. <c>blam</c>, <c>BLM!</c>).</summary>
    public required byte[] Engine { get; init; }
    /// <summary>Tag-file format version word at offset 56.</summary>
    public required ushort Version { get; init; }
    /// <summary>Stored body checksum (offset 40). <c>0xFFFFFFFF</c> is the
    /// "unchecksummed" sentinel some HEK tags ship with.</summary>
    public required uint Checksum { get; init; }
}
