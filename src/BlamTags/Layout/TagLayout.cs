using System.Text;

namespace BlamTags;

/// <summary>
/// The full <c>blay</c> chunk: a tag's schema (structs, blocks, fields,
/// field types, string tables) plus its 24-byte payload header
/// (<see cref="RootDataSize"/>, <see cref="Guid"/>, <see cref="Version"/>).
/// The outer <c>blay</c> chunk header is always version 2, even when the
/// inner layout payload version (1/2/3/4) differs.
/// </summary>
/// <remarks>
/// Everything here is <em>schema</em>, not instance data. All name-bearing
/// records reference <see cref="StringData"/> by byte offset; resolve via
/// <see cref="GetString"/>.
/// </remarks>
public sealed partial class TagLayout
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Raw-data size of the root struct (one element of the root
    /// block). Schema-level sanity check; preserved for roundtrip.</summary>
    public required uint RootDataSize { get; set; }
    /// <summary>Stable 16-byte identifier for the tag group / root block type.</summary>
    public required byte[] Guid { get; init; }
    /// <summary>Layout payload version (1, 2, 3, or 4). Distinct from the
    /// outer <c>blay</c> chunk-header version (always 2).</summary>
    public required uint Version { get; init; }

    public required TagLayoutHeader Header { get; init; }
    /// <summary><c>str*</c> — concatenated null-terminated UTF-8 strings.</summary>
    public required byte[] StringData { get; set; }
    /// <summary><c>sz+x</c> — u32 offsets into <see cref="StringData"/>.</summary>
    public required List<uint> StringOffsets { get; init; }
    /// <summary><c>sz[]</c> — named string-list records.</summary>
    public required List<TagStringList> StringLists { get; init; }
    /// <summary><c>csbn</c> — custom block-index search-name offsets.</summary>
    public required List<uint> CustomBlockIndexSearchNameOffsets { get; init; }
    /// <summary><c>dtnm</c> — data-field type-name offsets.</summary>
    public required List<uint> DataDefinitionNameOffsets { get; init; }
    /// <summary><c>arr!</c> — array definitions.</summary>
    public required List<TagArrayLayout> ArrayLayouts { get; init; }
    /// <summary><c>tgft</c> — field-type registry.</summary>
    public required List<TagFieldTypeLayout> FieldTypes { get; init; }
    /// <summary><c>gras</c> — flat field definitions.</summary>
    public required List<TagFieldLayout> Fields { get; init; }
    /// <summary><c>blv2</c> — block definitions.</summary>
    public required List<TagBlockLayout> BlockLayouts { get; init; }
    /// <summary><c>rcv2</c> — resource definitions. Empty in v1.</summary>
    public required List<TagResourceLayout> ResourceLayouts { get; init; }
    /// <summary><c>]==[</c> — interop definitions. Empty in v1/v2.</summary>
    public required List<TagInteropLayout> InteropLayouts { get; init; }
    /// <summary><c>stv2</c>/<c>stv4</c> — struct definitions.</summary>
    public required List<TagStructLayout> StructLayouts { get; init; }

    /// <summary>
    /// Resolve a name offset into the UTF-8 string at that position in
    /// <see cref="StringData"/> (null-terminated). Returns <c>null</c> for an
    /// out-of-range offset or invalid UTF-8.
    /// </summary>
    public string? GetString(uint offset)
    {
        int start = (int)offset;
        if (start >= StringData.Length)
            return null;
        int end = start;
        while (end < StringData.Length && StringData[end] != 0)
            end++;
        try
        {
            return StrictUtf8.GetString(StringData, start, end - start);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Number of direct child chunks a struct produces when serialized — how
    /// many of its fields have <c>NeedsSubChunk</c>. Mirrors BCS's
    /// <c>_calculate_structure_expected_children_by_entry</c>.
    /// </summary>
    public uint GetStructExpectedChildren(int structIndex)
    {
        uint count = 0;
        int fieldIndex = (int)StructLayouts[structIndex].FirstFieldIndex;
        while (true)
        {
            var field = Fields[fieldIndex];
            if (field.FieldType == TagFieldType.Terminator)
                return count;
            if (FieldTypes[(int)field.TypeIndex].NeedsSubChunk != 0)
                count++;
            fieldIndex++;
        }
    }

    /// <summary>
    /// Compute <see cref="TagStructLayout.Size"/> and each field's
    /// <see cref="TagFieldLayout.Offset"/> for <paramref name="structIndex"/>,
    /// recursing into nested struct/array fields first so their sizes are
    /// known. Idempotent.
    /// </summary>
    public void ComputeStructLayout(int structIndex)
    {
        if (StructLayouts[structIndex].Size != 0)
            return;

        int size = 0;
        int fieldIndex = (int)StructLayouts[structIndex].FirstFieldIndex;

        bool done = false;
        while (!done)
        {
            // Record this field's START offset before accumulating its size.
            Fields[fieldIndex].Offset = (uint)size;

            var field = Fields[fieldIndex];

            if (field.FieldType == TagFieldType.Terminator)
            {
                done = true;
            }
            else if (field.FieldType == TagFieldType.Struct)
            {
                int childIndex = (int)field.Definition;
                ComputeStructLayout(childIndex);
                size += StructLayouts[childIndex].Size;
            }
            else if (field.FieldType == TagFieldType.Array)
            {
                var array = ArrayLayouts[(int)field.Definition];
                int arrayStructIndex = (int)array.StructIndex;
                ComputeStructLayout(arrayStructIndex);
                size += StructLayouts[arrayStructIndex].Size * (int)array.Count;
            }
            else if (field.FieldType is TagFieldType.Pad or TagFieldType.Skip)
            {
                size += (int)field.Definition;
            }
            else if (field.FieldType == TagFieldType.Custom)
            {
                // `tmpl` customs stash their parent-chain expansion size in
                // `Definition`; zero for all other custom subtypes.
                size += (int)field.Definition;
            }
            else
            {
                size += (int)FieldTypes[(int)field.TypeIndex].Size;
            }

            fieldIndex++;
        }

        StructLayouts[structIndex].Size = size;
    }

    private static bool IsV2Plus(uint v) => v is >= 2 and <= 4;

    /// <summary>
    /// Parse a <c>blay</c> chunk from a reader positioned at its header.
    /// After parsing all records, resolves each field's
    /// <see cref="TagFieldType"/> and computes every struct's size/offsets.
    /// </summary>
    internal static TagLayout Read(TagReader reader)
    {
        long blayHeaderOffset = reader.Position;
        var blayHeader = reader.ReadChunkHeader();
        if (blayHeader.Signature != Tag.Of("blay"))
            throw TagReadException.BadChunkSignature(blayHeaderOffset, Tag.Of("blay"), blayHeader.Signature);
        if (blayHeader.Version != 2)
            throw TagReadException.BadChunkVersion("blay", blayHeader.Version);

        long blayOffset = reader.Position;

        uint rootDataSize = reader.ReadU32();
        byte[] guid = reader.ReadGuid();
        uint version = reader.ReadU32();

        if (version is < 1 or > 4)
            throw TagReadException.UnsupportedLayoutVersion(version);

        var header = new TagLayoutHeader
        {
            TagGroupBlockIndex = IsV2Plus(version) ? reader.ReadU32() : 0,
            StringDataSize = reader.ReadU32(),
            StringOffsetCount = reader.ReadU32(),
            StringListCount = reader.ReadU32(),
            CustomBlockIndexSearchNamesCount = reader.ReadU32(),
            DataDefinitionNameCount = reader.ReadU32(),
            ArrayLayoutCount = reader.ReadU32(),
            FieldTypeCount = reader.ReadU32(),
            FieldCount = reader.ReadU32(),
            AggregateLayoutCount = version == 1 ? reader.ReadU32() : 0,
            StructLayoutCount = IsV2Plus(version) ? reader.ReadU32() : 0,
            BlockLayoutCount = IsV2Plus(version) ? reader.ReadU32() : 0,
            ResourceLayoutCount = IsV2Plus(version) ? reader.ReadU32() : 0,
            InteropLayoutCount = version is 3 or 4 ? reader.ReadU32() : 0,
        };

        // tgly wrapper (v2+). Its version field carries the layout version.
        long tglyOffset = 0;
        TagChunkHeader tglyHeader = default;
        bool hasTgly = version > 1;
        if (hasTgly)
        {
            long sigOffset = reader.Position;
            tglyHeader = reader.ReadChunkHeader();
            if (tglyHeader.Signature != Tag.Of("tgly"))
                throw TagReadException.BadChunkSignature(sigOffset, Tag.Of("tgly"), tglyHeader.Signature);
            if (tglyHeader.Version != version)
                throw TagReadException.BadChunkVersion("tgly", tglyHeader.Version);
            tglyOffset = reader.Position;
        }

        // str* — string data
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("str*");
            if (header.StringDataSize != h.Size)
                throw TagReadException.CountMismatch("str*", header.StringDataSize, h.Size);
        }
        byte[] stringData = reader.ReadBytes((int)header.StringDataSize);

        // sz+x — string offsets
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("sz+x");
            TagReader.CheckCountMatchesSize("sz+x", header.StringOffsetCount, h.Size, 4);
        }
        var stringOffsets = new List<uint>((int)header.StringOffsetCount);
        for (uint i = 0; i < header.StringOffsetCount; i++)
            stringOffsets.Add(reader.ReadU32());

        // sz[] — string lists
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("sz[]");
            TagReader.CheckCountMatchesSize("sz[]", header.StringListCount, h.Size, 12);
        }
        var stringLists = new List<TagStringList>((int)header.StringListCount);
        for (uint i = 0; i < header.StringListCount; i++)
            stringLists.Add(new TagStringList
            {
                Offset = reader.ReadU32(),
                Count = reader.ReadU32(),
                First = reader.ReadU32(),
            });

        // csbn — custom block-index search names
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("csbn");
            TagReader.CheckCountMatchesSize("csbn", header.CustomBlockIndexSearchNamesCount, h.Size, 4);
        }
        var customSearchNames = new List<uint>((int)header.CustomBlockIndexSearchNamesCount);
        for (uint i = 0; i < header.CustomBlockIndexSearchNamesCount; i++)
            customSearchNames.Add(reader.ReadU32());

        // dtnm — data definition names
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("dtnm");
            TagReader.CheckCountMatchesSize("dtnm", header.DataDefinitionNameCount, h.Size, 4);
        }
        var dataDefNames = new List<uint>((int)header.DataDefinitionNameCount);
        for (uint i = 0; i < header.DataDefinitionNameCount; i++)
            dataDefNames.Add(reader.ReadU32());

        // arr! — array definitions
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("arr!");
            TagReader.CheckCountMatchesSize("arr!", header.ArrayLayoutCount, h.Size, 12);
        }
        var arrayLayouts = new List<TagArrayLayout>((int)header.ArrayLayoutCount);
        for (uint i = 0; i < header.ArrayLayoutCount; i++)
            arrayLayouts.Add(new TagArrayLayout
            {
                NameOffset = reader.ReadU32(),
                Count = reader.ReadU32(),
                StructIndex = reader.ReadU32(),
            });

        // tgft — field types
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("tgft");
            TagReader.CheckCountMatchesSize("tgft", header.FieldTypeCount, h.Size, 12);
        }
        var fieldTypes = new List<TagFieldTypeLayout>((int)header.FieldTypeCount);
        for (uint i = 0; i < header.FieldTypeCount; i++)
            fieldTypes.Add(new TagFieldTypeLayout
            {
                NameOffset = reader.ReadU32(),
                Size = reader.ReadU32(),
                NeedsSubChunk = reader.ReadU32(),
            });

        // gras — field definitions
        if (version > 1)
        {
            var h = reader.ReadValidatedChunkHeader("gras");
            TagReader.CheckCountMatchesSize("gras", header.FieldCount, h.Size, 12);
        }
        var fields = new List<TagFieldLayout>((int)header.FieldCount);
        for (uint i = 0; i < header.FieldCount; i++)
            fields.Add(new TagFieldLayout
            {
                NameOffset = reader.ReadU32(),
                TypeIndex = reader.ReadU32(),
                Definition = reader.ReadU32(),
            });

        List<TagBlockLayout> blockLayouts;
        List<TagStructLayout> structLayouts;
        List<TagResourceLayout> resourceLayouts;
        List<TagInteropLayout> interopLayouts;

        if (version == 1)
        {
            // v1 aggregate records (28 bytes: guid[16] + name + max_count +
            // first_field_index) split into paired struct + block records.
            blockLayouts = new List<TagBlockLayout>((int)header.AggregateLayoutCount);
            structLayouts = new List<TagStructLayout>((int)header.AggregateLayoutCount);
            for (uint i = 0; i < header.AggregateLayoutCount; i++)
            {
                byte[] aggGuid = reader.ReadGuid();
                uint nameOffset = reader.ReadU32();
                uint maxCount = reader.ReadU32();
                uint firstFieldIndex = reader.ReadU32();

                structLayouts.Add(new TagStructLayout
                {
                    Index = i,
                    Guid = aggGuid,
                    NameOffset = nameOffset,
                    FirstFieldIndex = firstFieldIndex,
                    Size = 0,
                    Version = 0,
                });
                blockLayouts.Add(new TagBlockLayout
                {
                    Index = i,
                    NameOffset = nameOffset,
                    MaxCount = maxCount,
                    StructIndex = i,
                });
            }
            resourceLayouts = [];
            interopLayouts = [];
        }
        else
        {
            // blv2 — block definitions
            var blv2 = reader.ReadValidatedChunkHeader("blv2");
            TagReader.CheckCountMatchesSize("blv2", header.BlockLayoutCount, blv2.Size, 12);
            blockLayouts = new List<TagBlockLayout>((int)header.BlockLayoutCount);
            for (uint i = 0; i < header.BlockLayoutCount; i++)
                blockLayouts.Add(new TagBlockLayout
                {
                    Index = i,
                    NameOffset = reader.ReadU32(),
                    MaxCount = reader.ReadU32(),
                    StructIndex = reader.ReadU32(),
                });

            // rcv2 — resource definitions
            var rcv2 = reader.ReadValidatedChunkHeader("rcv2");
            TagReader.CheckCountMatchesSize("rcv2", header.ResourceLayoutCount, rcv2.Size, 12);
            resourceLayouts = new List<TagResourceLayout>((int)header.ResourceLayoutCount);
            for (uint i = 0; i < header.ResourceLayoutCount; i++)
                resourceLayouts.Add(new TagResourceLayout
                {
                    NameOffset = reader.ReadU32(),
                    Unknown = reader.ReadU32(),
                    StructIndex = reader.ReadU32(),
                });

            // ]==[ — interop definitions (v3/v4)
            interopLayouts = [];
            if (version is 3 or 4)
            {
                var interopHeader = reader.ReadValidatedChunkHeader("]==[");
                TagReader.CheckCountMatchesSize("]==[", header.InteropLayoutCount, interopHeader.Size, 24);
                interopLayouts.Capacity = (int)header.InteropLayoutCount;
                for (uint i = 0; i < header.InteropLayoutCount; i++)
                    interopLayouts.Add(new TagInteropLayout
                    {
                        NameOffset = reader.ReadU32(),
                        StructIndex = reader.ReadU32(),
                        Guid = reader.ReadGuid(),
                    });
            }

            // stv2 (v2/v3, 24 bytes) / stv4 (v4, 28 bytes) — struct definitions
            string structSig = version == 4 ? "stv4" : "stv2";
            uint structRecordSize = version == 4 ? 28u : 24u;
            var structHeader = reader.ReadValidatedChunkHeader(structSig);
            TagReader.CheckCountMatchesSize(structSig, header.StructLayoutCount, structHeader.Size, structRecordSize);
            structLayouts = new List<TagStructLayout>((int)header.StructLayoutCount);
            for (uint i = 0; i < header.StructLayoutCount; i++)
            {
                byte[] sguid = reader.ReadGuid();
                uint nameOffset = reader.ReadU32();
                uint firstFieldIndex = reader.ReadU32();
                uint sversion = version == 4 ? reader.ReadU32() : 0;
                structLayouts.Add(new TagStructLayout
                {
                    Index = i,
                    Guid = sguid,
                    NameOffset = nameOffset,
                    FirstFieldIndex = firstFieldIndex,
                    Size = 0,
                    Version = sversion,
                });
            }
        }

        if (hasTgly)
            reader.CheckChunkEnd("tgly", tglyOffset, tglyHeader.Size);
        reader.CheckChunkEnd("blay", blayOffset, blayHeader.Size);

        var layout = new TagLayout
        {
            RootDataSize = rootDataSize,
            Guid = guid,
            Version = version,
            Header = header,
            StringData = stringData,
            StringOffsets = stringOffsets,
            StringLists = stringLists,
            CustomBlockIndexSearchNameOffsets = customSearchNames,
            DataDefinitionNameOffsets = dataDefNames,
            ArrayLayouts = arrayLayouts,
            FieldTypes = fieldTypes,
            Fields = fields,
            BlockLayouts = blockLayouts,
            ResourceLayouts = resourceLayouts,
            InteropLayouts = interopLayouts,
            StructLayouts = structLayouts,
        };

        // Resolve each field's type-name string into a TagFieldType once.
        foreach (var field in layout.Fields)
        {
            uint typeNameOffset = layout.FieldTypes[(int)field.TypeIndex].NameOffset;
            string name = layout.GetString(typeNameOffset)
                ?? throw TagReadException.InvalidUtf8("layout field type name");
            field.FieldType = TagFieldTypeExtensions.FromName(name);
        }

        for (int i = 0; i < layout.StructLayouts.Count; i++)
            layout.ComputeStructLayout(i);

        return layout;
    }

    /// <summary>Write this layout as a <c>blay</c> chunk (header version 2).</summary>
    internal void Write(TagWriter writer)
    {
        var body = new TagWriter();
        body.WriteU32(RootDataSize);
        body.WriteBytes(Guid);
        body.WriteU32(Version);
        WriteBody(body);
        writer.WriteChunkContent(Tag.Of("blay"), 2, body.ToArray());
    }

    private void WriteBody(TagWriter writer)
    {
        uint v = Version;

        if (IsV2Plus(v))
            writer.WriteU32(Header.TagGroupBlockIndex);
        writer.WriteU32(Header.StringDataSize);
        writer.WriteU32(Header.StringOffsetCount);
        writer.WriteU32(Header.StringListCount);
        writer.WriteU32(Header.CustomBlockIndexSearchNamesCount);
        writer.WriteU32(Header.DataDefinitionNameCount);
        writer.WriteU32(Header.ArrayLayoutCount);
        writer.WriteU32(Header.FieldTypeCount);
        writer.WriteU32(Header.FieldCount);
        if (v == 1)
            writer.WriteU32(Header.AggregateLayoutCount);
        if (IsV2Plus(v))
        {
            writer.WriteU32(Header.StructLayoutCount);
            writer.WriteU32(Header.BlockLayoutCount);
            writer.WriteU32(Header.ResourceLayoutCount);
        }
        if (v is 3 or 4)
            writer.WriteU32(Header.InteropLayoutCount);

        if (v == 1)
        {
            WriteLayoutChunks(v, writer);
            return;
        }

        writer.WriteChunkHeader(Tag.Of("tgly"), v, ComputeLayoutSize(v));
        WriteLayoutChunks(v, writer);
    }

    private uint ComputeLayoutSize(uint v)
    {
        static uint Section(int count, int chunkSize) => (uint)(12 + count * chunkSize);
        int structRecordSize = v == 4 ? 28 : 24;

        uint size = 0;
        size += Section(StringData.Length, 1);
        size += Section(StringOffsets.Count, 4);
        size += Section(StringLists.Count, 12);
        size += Section(CustomBlockIndexSearchNameOffsets.Count, 4);
        size += Section(DataDefinitionNameOffsets.Count, 4);
        size += Section(ArrayLayouts.Count, 12);
        size += Section(FieldTypes.Count, 12);
        size += Section(Fields.Count, 12);
        size += Section(BlockLayouts.Count, 12);
        size += Section(ResourceLayouts.Count, 12);
        if (v is 3 or 4)
            size += Section(InteropLayouts.Count, 24);
        size += Section(StructLayouts.Count, structRecordSize);
        return size;
    }

    private void WriteLayoutChunks(uint v, TagWriter writer)
    {
        bool wrap = IsV2Plus(v);

        if (wrap) writer.WriteChunkHeader(Tag.Of("str*"), 0, (uint)StringData.Length);
        writer.WriteBytes(StringData);

        if (wrap) writer.WriteChunkHeader(Tag.Of("sz+x"), 0, (uint)(StringOffsets.Count * 4));
        foreach (var off in StringOffsets) writer.WriteU32(off);

        if (wrap) writer.WriteChunkHeader(Tag.Of("sz[]"), 0, (uint)(StringLists.Count * 12));
        foreach (var sl in StringLists)
        {
            writer.WriteU32(sl.Offset);
            writer.WriteU32(sl.Count);
            writer.WriteU32(sl.First);
        }

        if (wrap) writer.WriteChunkHeader(Tag.Of("csbn"), 0, (uint)(CustomBlockIndexSearchNameOffsets.Count * 4));
        foreach (var off in CustomBlockIndexSearchNameOffsets) writer.WriteU32(off);

        if (wrap) writer.WriteChunkHeader(Tag.Of("dtnm"), 0, (uint)(DataDefinitionNameOffsets.Count * 4));
        foreach (var off in DataDefinitionNameOffsets) writer.WriteU32(off);

        if (wrap) writer.WriteChunkHeader(Tag.Of("arr!"), 0, (uint)(ArrayLayouts.Count * 12));
        foreach (var a in ArrayLayouts)
        {
            writer.WriteU32(a.NameOffset);
            writer.WriteU32(a.Count);
            writer.WriteU32(a.StructIndex);
        }

        if (wrap) writer.WriteChunkHeader(Tag.Of("tgft"), 0, (uint)(FieldTypes.Count * 12));
        foreach (var ft in FieldTypes)
        {
            writer.WriteU32(ft.NameOffset);
            writer.WriteU32(ft.Size);
            writer.WriteU32(ft.NeedsSubChunk);
        }

        if (wrap) writer.WriteChunkHeader(Tag.Of("gras"), 0, (uint)(Fields.Count * 12));
        foreach (var f in Fields)
        {
            writer.WriteU32(f.NameOffset);
            writer.WriteU32(f.TypeIndex);
            writer.WriteU32(f.Definition);
        }

        if (v == 1)
        {
            // v1 aggregate records reconstructed from paired struct/block defs.
            for (int i = 0; i < StructLayouts.Count; i++)
            {
                var s = StructLayouts[i];
                var b = BlockLayouts[i];
                writer.WriteBytes(s.Guid);
                writer.WriteU32(s.NameOffset);
                writer.WriteU32(b.MaxCount);
                writer.WriteU32(s.FirstFieldIndex);
            }
            return;
        }

        writer.WriteChunkHeader(Tag.Of("blv2"), 0, (uint)(BlockLayouts.Count * 12));
        foreach (var b in BlockLayouts)
        {
            writer.WriteU32(b.NameOffset);
            writer.WriteU32(b.MaxCount);
            writer.WriteU32(b.StructIndex);
        }

        writer.WriteChunkHeader(Tag.Of("rcv2"), 0, (uint)(ResourceLayouts.Count * 12));
        foreach (var r in ResourceLayouts)
        {
            writer.WriteU32(r.NameOffset);
            writer.WriteU32(r.Unknown);
            writer.WriteU32(r.StructIndex);
        }

        if (v is 3 or 4)
        {
            writer.WriteChunkHeader(Tag.Of("]==["), 0, (uint)(InteropLayouts.Count * 24));
            foreach (var ix in InteropLayouts)
            {
                writer.WriteU32(ix.NameOffset);
                writer.WriteU32(ix.StructIndex);
                writer.WriteBytes(ix.Guid);
            }
        }

        (uint structSig, int structRecordSize) = v == 4
            ? (Tag.Of("stv4"), 28)
            : (Tag.Of("stv2"), 24);
        writer.WriteChunkHeader(structSig, 0, (uint)(StructLayouts.Count * structRecordSize));
        foreach (var s in StructLayouts)
        {
            writer.WriteBytes(s.Guid);
            writer.WriteU32(s.NameOffset);
            writer.WriteU32(s.FirstFieldIndex);
            if (v == 4)
                writer.WriteU32(s.Version);
        }
    }
}
