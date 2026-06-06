namespace BlamTags;

// Schema record types that make up a TagLayout. These describe the shape
// of a tag's payload; per-tag values live elsewhere (the data layer). All
// name-bearing records reference TagLayout.StringData by byte offset.

/// <summary>An <c>sz[]</c> entry: a named list of strings (enum/flag value
/// names), as a slice into <see cref="TagLayout.StringOffsets"/>.</summary>
public sealed class TagStringList
{
    /// <summary>Index into <see cref="TagLayout.StringData"/> of the list's
    /// display name.</summary>
    public required uint Offset { get; init; }
    /// <summary>Number of entries.</summary>
    public required uint Count { get; init; }
    /// <summary>Index into <see cref="TagLayout.StringOffsets"/> of the first
    /// entry; entries <c>[First .. First+Count]</c> are this list's strings.</summary>
    public required uint First { get; init; }
}

/// <summary>An <c>arr!</c> entry: a fixed-count inline array of a struct.</summary>
public sealed class TagArrayLayout
{
    public required uint NameOffset { get; init; }
    public required uint Count { get; init; }
    /// <summary>Index into <see cref="TagLayout.StructLayouts"/> of the
    /// element struct.</summary>
    public required uint StructIndex { get; init; }
}

/// <summary>A <c>tgft</c> entry: a field-type registry record, indexed by
/// <see cref="TagFieldLayout.TypeIndex"/>.</summary>
public sealed class TagFieldTypeLayout
{
    public required uint NameOffset { get; init; }
    /// <summary>Raw-data byte footprint of a field of this type.</summary>
    public required uint Size { get; init; }
    /// <summary>Nonzero if fields of this type emit a sub-chunk inside their
    /// containing <c>tgst</c>.</summary>
    public required uint NeedsSubChunk { get; init; }
}

/// <summary>A <c>gras</c> entry: one field within a struct definition.
/// Serialized form is 12 bytes (name offset, type index, definition); the
/// derived <see cref="FieldType"/> and <see cref="Offset"/> are computed at
/// read time and not on the wire.</summary>
public sealed class TagFieldLayout
{
    public required uint NameOffset { get; init; }
    public required uint TypeIndex { get; init; }
    /// <summary>Type-specific payload: a table index (struct/block/array/…),
    /// a byte count (pad/skip), the string-lists index (enum/flags), or a
    /// <c>tmpl</c> expansion size. Mutable — schema import patches tmpl
    /// expansions in place.</summary>
    public uint Definition { get; set; }
    /// <summary>Dispatch-ready resolved type. Set once during layout read.</summary>
    public TagFieldType FieldType { get; set; } = TagFieldType.Unknown;
    /// <summary>Byte offset of this field's raw data within its containing
    /// struct. Set after struct sizes are known.</summary>
    public uint Offset { get; set; }
}

/// <summary>A <c>blv2</c> entry (or half of a v1 <c>agro</c> record): names a
/// block whose elements are instances of a struct.</summary>
public sealed class TagBlockLayout
{
    public required uint Index { get; init; }
    public required uint NameOffset { get; init; }
    /// <summary>Element-count cap from the schema. Not enforced; preserved
    /// for roundtrip.</summary>
    public required uint MaxCount { get; init; }
    public required uint StructIndex { get; init; }
}

/// <summary>An <c>rcv2</c> entry: declares a pageable-resource field's shape.</summary>
public sealed class TagResourceLayout
{
    public required uint NameOffset { get; init; }
    /// <summary>Unknown purpose; preserved verbatim.</summary>
    public required uint Unknown { get; init; }
    public required uint StructIndex { get; init; }
}

/// <summary>A <c>]==[</c> entry (v3/v4 only): declares an api-interop field.</summary>
public sealed class TagInteropLayout
{
    public required uint NameOffset { get; init; }
    public required uint StructIndex { get; init; }
    /// <summary>Stable 16-byte identifier for the interop type across versions.</summary>
    public required byte[] Guid { get; init; }
}

/// <summary>An <c>stv2</c>/<c>stv4</c> entry (or half of a v1 <c>agro</c>
/// record): names a struct and points at its first field. <see cref="Size"/>
/// is derived at read time, not on the wire.</summary>
public sealed class TagStructLayout
{
    public required uint Index { get; init; }
    /// <summary>Stable 16-byte identifier for the struct type.</summary>
    public required byte[] Guid { get; init; }
    public required uint NameOffset { get; init; }
    /// <summary>Index into <see cref="TagLayout.Fields"/> of the first field;
    /// fields continue until a terminator-typed field.</summary>
    public required uint FirstFieldIndex { get; init; }
    /// <summary>Derived field-packed size in bytes. Zero until computed.</summary>
    public int Size { get; set; }
    /// <summary>Schema-version tag for the struct. Only present in V4
    /// layouts (trailing u32 of an <c>stv4</c> record); zero otherwise.</summary>
    public uint Version { get; init; }
}

/// <summary>Counts at the top of the <c>blay</c> payload — each field is the
/// length of the correspondingly named table on <see cref="TagLayout"/>.
/// Which fields are present depends on the layout version.</summary>
public sealed class TagLayoutHeader
{
    public uint TagGroupBlockIndex { get; set; }
    public uint StringDataSize { get; set; }
    public uint StringOffsetCount { get; set; }
    public uint StringListCount { get; set; }
    public uint CustomBlockIndexSearchNamesCount { get; set; }
    public uint DataDefinitionNameCount { get; set; }
    public uint ArrayLayoutCount { get; set; }
    public uint FieldTypeCount { get; set; }
    public uint FieldCount { get; set; }
    /// <summary>v1 only.</summary>
    public uint AggregateLayoutCount { get; set; }
    /// <summary>v2/v3/v4 only.</summary>
    public uint StructLayoutCount { get; set; }
    /// <summary>v2/v3/v4 only.</summary>
    public uint BlockLayoutCount { get; set; }
    /// <summary>v2/v3/v4 only.</summary>
    public uint ResourceLayoutCount { get; set; }
    /// <summary>v3/v4 only.</summary>
    public uint InteropLayoutCount { get; set; }
}
