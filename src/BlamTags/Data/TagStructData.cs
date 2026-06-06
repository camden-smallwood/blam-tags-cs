namespace BlamTags;

/// <summary>
/// A struct within a tag's data tree. Owns its sub-chunks (nested
/// structures + leaf sub-chunks); its <em>bytes</em> live in the enclosing
/// <see cref="TagBlockData.RawData"/> at an offset determined by path descent.
/// </summary>
internal sealed class TagStructData
{
    /// <summary>Index into <see cref="TagLayout.StructLayouts"/>.</summary>
    public required uint StructIndex { get; init; }
    /// <summary>Sub-chunks emitted inside this struct's <c>tgst</c> chunk, in
    /// emission order.</summary>
    public required List<TagSubChunkEntry> SubChunks { get; init; }

    private static readonly uint TgstSig = Tag.Of("tgst");

    /// <summary>Parse a <c>tgst</c> chunk: header + sub-chunks (+ trailing
    /// empty-tgst absorb). Raw bytes stay in the enclosing block.</summary>
    public static TagStructData Read(TagLayout layout, TagStructLayout definition, TagReader reader)
    {
        long headerOffset = reader.Position;
        var header = reader.ReadChunkHeader();
        long structOffset = reader.Position;
        if (header.Signature != TgstSig)
            throw TagReadException.BadChunkSignature(headerOffset, TgstSig, header.Signature);
        // tgst.version always equals tgst.size.
        if (header.Version != header.Size)
            throw TagReadException.ChunkSizeMismatch("tgst", structOffset,
                structOffset + header.Version, structOffset + header.Size);

        List<TagSubChunkEntry> subChunks;
        if (header.Size != 0)
        {
            subChunks = ReadSubChunks(layout, definition, reader);

            // Trailing empty-tgst absorb: MCC occasionally emits size=0 tgst
            // chunks at the end of a struct's content with no layout field.
            long endOffset = reader.Position;
            long expectedOffset = structOffset + header.Size;
            if (endOffset != expectedOffset)
            {
                bool nonEmptyTrailing = false;
                while (true)
                {
                    endOffset = reader.Position;
                    if (endOffset == expectedOffset)
                        break;
                    var trailer = reader.ReadChunkHeader();
                    if (trailer.Signature != TgstSig || trailer.Size != 0)
                    {
                        nonEmptyTrailing = true;
                        break;
                    }
                    if (trailer.Version != 0)
                        throw TagReadException.BadChunkVersion("trailing empty tgst", trailer.Version);
                    subChunks.Add(new TagSubChunkEntry
                    {
                        FieldIndex = null,
                        Content = new TagSubChunkContent.EmptyPlaceholder(),
                    });
                }
                if (nonEmptyTrailing)
                    throw TagReadException.ChunkSizeMismatch("tgst", structOffset, endOffset, expectedOffset);
            }
        }
        else
        {
            // Empty tgst: no sub-chunk bytes, but fixed-size containers still
            // need scaffolding to be navigable. NewDefault builds it.
            subChunks = NewDefault(layout, (int)definition.Index).SubChunks;
        }

        return new TagStructData { StructIndex = definition.Index, SubChunks = subChunks };
    }

    /// <summary>Write this struct as a <c>tgst</c> chunk (sub-chunk content
    /// only; raw bytes flow through the enclosing block).</summary>
    public void Write(TagLayout layout, TagWriter writer)
    {
        var content = new TagWriter();
        WriteSubChunks(SubChunks, layout, content);
        byte[] bytes = content.ToArray();
        uint size = (uint)bytes.Length;
        writer.WriteChunkHeader(TgstSig, size, size);
        writer.WriteBytes(bytes);
    }

    /// <summary>Walk a struct definition's fields, reading each
    /// sub-chunk-producing field's chunk from the stream.</summary>
    private static List<TagSubChunkEntry> ReadSubChunks(TagLayout layout, TagStructLayout definition, TagReader reader)
    {
        var subChunks = new List<TagSubChunkEntry>();
        int fieldIndex = (int)definition.FirstFieldIndex;

        while (true)
        {
            var field = layout.Fields[fieldIndex];

            switch (field.FieldType)
            {
                case TagFieldType.Terminator:
                    return subChunks;

                case TagFieldType.Struct:
                {
                    var nestedDefinition = layout.StructLayouts[(int)field.Definition];

                    // Placeholder-skip: MCC may emit size=0 tgst placeholder(s)
                    // before the real tgst when the nested struct expects
                    // sub-chunks.
                    uint expectedChildren = layout.GetStructExpectedChildren((int)field.Definition);
                    if (expectedChildren > 0)
                    {
                        while (true)
                        {
                            long ho = reader.Position;
                            var h = reader.ReadChunkHeader();
                            if (h.Signature != TgstSig)
                                throw TagReadException.BadChunkSignature(ho, TgstSig, h.Signature);
                            if (h.Size == 0)
                            {
                                if (h.Version != 0)
                                    throw TagReadException.BadChunkVersion("empty placeholder tgst", h.Version);
                                subChunks.Add(new TagSubChunkEntry
                                {
                                    FieldIndex = null,
                                    Content = new TagSubChunkContent.EmptyPlaceholder(),
                                });
                                continue;
                            }
                            reader.Seek((int)ho);
                            break;
                        }
                    }

                    var nested = Read(layout, nestedDefinition, reader);
                    subChunks.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fieldIndex,
                        Content = new TagSubChunkContent.StructContent(nested),
                    });
                    break;
                }

                case TagFieldType.Array:
                {
                    var arrayLayout = layout.ArrayLayouts[(int)field.Definition];
                    var elementDefinition = layout.StructLayouts[(int)arrayLayout.StructIndex];
                    var elements = new List<TagStructData>((int)arrayLayout.Count);
                    for (uint i = 0; i < arrayLayout.Count; i++)
                    {
                        var elementSubChunks = ReadSubChunks(layout, elementDefinition, reader);
                        elements.Add(new TagStructData
                        {
                            StructIndex = elementDefinition.Index,
                            SubChunks = elementSubChunks,
                        });
                    }
                    subChunks.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fieldIndex,
                        Content = new TagSubChunkContent.ArrayContent(elements),
                    });
                    break;
                }

                case TagFieldType.Block:
                {
                    var blockLayout = layout.BlockLayouts[(int)field.Definition];
                    var blockData = TagBlockData.Read(layout, blockLayout, reader);
                    subChunks.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fieldIndex,
                        Content = new TagSubChunkContent.BlockContent(blockData),
                    });
                    break;
                }

                case TagFieldType.TagReference:
                {
                    var (version, content) = reader.ReadChunkContent("tgrf");
                    if (version != 0) throw TagReadException.BadChunkVersion("tgrf", version);
                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.TagReferenceContent(content)));
                    break;
                }

                case TagFieldType.StringId:
                {
                    var (version, content) = reader.ReadChunkContent("tgsi");
                    if (version != 0) throw TagReadException.BadChunkVersion("tgsi (string_id)", version);
                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.StringIdContent(content)));
                    break;
                }

                case TagFieldType.OldStringId:
                {
                    var (version, content) = reader.ReadChunkContent("tgsi");
                    if (version != 0) throw TagReadException.BadChunkVersion("tgsi (old_string_id)", version);
                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.OldStringIdContent(content)));
                    break;
                }

                case TagFieldType.Data:
                {
                    var (version, content) = reader.ReadChunkContent("tgda");
                    if (version != 0) throw TagReadException.BadChunkVersion("tgda", version);
                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.DataContent(content)));
                    break;
                }

                case TagFieldType.PageableResource:
                {
                    var resourceLayout = layout.ResourceLayouts[(int)field.Definition];
                    var resourceStructDefinition = layout.StructLayouts[(int)resourceLayout.StructIndex];

                    var outerHeader = reader.ReadChunkHeader();
                    long outerContentOffset = reader.Position;

                    TagResourceChunk resource;
                    if (outerHeader.Signature == Tag.Of("tg\0c"))
                    {
                        if (outerHeader.Version != 0) throw TagReadException.BadChunkVersion("tg\\0c", outerHeader.Version);
                        resource = new TagResourceChunk.NullResource();
                    }
                    else if (outerHeader.Signature == Tag.Of("tgrc"))
                    {
                        if (outerHeader.Version != 0) throw TagReadException.BadChunkVersion("tgrc", outerHeader.Version);
                        var tgdtHeader = reader.ReadValidatedChunkHeader("tgdt");
                        byte[] exploded = reader.ReadBytes((int)tgdtHeader.Size);
                        var structData = Read(layout, resourceStructDefinition, reader);
                        resource = new TagResourceChunk.ExplodedResource(exploded, structData);
                    }
                    else if (outerHeader.Signature == Tag.Of("tgxc"))
                    {
                        byte[] payload = reader.ReadBytes((int)outerHeader.Size);
                        resource = new TagResourceChunk.XsyncResource(outerHeader.Version, payload);
                    }
                    else
                    {
                        throw TagReadException.UnknownSubChunkSignature("pageable resource", outerHeader.Signature);
                    }

                    long endOffset = reader.Position;
                    long expectedOffset = outerContentOffset + outerHeader.Size;
                    if (endOffset != expectedOffset)
                        throw TagReadException.ChunkSizeMismatch("pageable resource", outerContentOffset, endOffset, expectedOffset);

                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.ResourceContent(resource)));
                    break;
                }

                case TagFieldType.ApiInterop:
                {
                    var (version, content) = reader.ReadChunkContent("ti][");
                    if (version != 0) throw TagReadException.BadChunkVersion("ti][ (api_interop)", version);
                    subChunks.Add(Entry(fieldIndex, new TagSubChunkContent.ApiInteropContent(content)));
                    break;
                }

                default:
                {
                    // Primitives / pad / skip / custom / explanation / useless_pad.
                    var fieldType = layout.FieldTypes[(int)field.TypeIndex];
                    if (fieldType.NeedsSubChunk != 0)
                    {
                        string name = layout.GetString(fieldType.NameOffset) ?? "<bad name>";
                        throw TagReadException.UnsupportedFieldType(name);
                    }
                    break;
                }
            }

            fieldIndex++;
        }

        static TagSubChunkEntry Entry(int fieldIndex, TagSubChunkContent content) =>
            new() { FieldIndex = (uint)fieldIndex, Content = content };
    }

    /// <summary>Serialize a list of sub-chunk entries in stored order.</summary>
    private static void WriteSubChunks(List<TagSubChunkEntry> entries, TagLayout layout, TagWriter writer)
    {
        foreach (var entry in entries)
        {
            switch (entry.Content)
            {
                case TagSubChunkContent.EmptyPlaceholder:
                    writer.WriteChunkHeader(TgstSig, 0, 0);
                    break;
                case TagSubChunkContent.StructContent c:
                    c.Struct.Write(layout, writer);
                    break;
                case TagSubChunkContent.BlockContent c:
                    c.Block.Write(layout, writer);
                    break;
                case TagSubChunkContent.ArrayContent c:
                    // Array elements have no wrapping tgst; their sub-chunks
                    // flow inline into the parent's tgst content.
                    foreach (var element in c.Elements)
                        WriteSubChunks(element.SubChunks, layout, writer);
                    break;
                case TagSubChunkContent.TagReferenceContent c:
                    writer.WriteChunkContent(Tag.Of("tgrf"), 0, c.Payload);
                    break;
                case TagSubChunkContent.StringIdContent c:
                    writer.WriteChunkContent(Tag.Of("tgsi"), 0, c.Payload);
                    break;
                case TagSubChunkContent.OldStringIdContent c:
                    writer.WriteChunkContent(Tag.Of("tgsi"), 0, c.Payload);
                    break;
                case TagSubChunkContent.DataContent c:
                    writer.WriteChunkContent(Tag.Of("tgda"), 0, c.Payload);
                    break;
                case TagSubChunkContent.ApiInteropContent c:
                    writer.WriteChunkContent(Tag.Of("ti]["), 0, c.Payload);
                    break;
                case TagSubChunkContent.ResourceContent { Resource: TagResourceChunk.NullResource }:
                    writer.WriteChunkHeader(Tag.Of("tg\0c"), 0, 0);
                    break;
                case TagSubChunkContent.ResourceContent { Resource: TagResourceChunk.ExplodedResource ex }:
                {
                    var inner = new TagWriter();
                    inner.WriteChunkContent(Tag.Of("tgdt"), 0, ex.Exploded);
                    ex.StructData.Write(layout, inner);
                    writer.WriteChunkContent(Tag.Of("tgrc"), 0, inner.ToArray());
                    break;
                }
                case TagSubChunkContent.ResourceContent { Resource: TagResourceChunk.XsyncResource xs }:
                    writer.WriteChunkContent(Tag.Of("tgxc"), xs.Version, xs.Payload);
                    break;
                default:
                    throw new InvalidOperationException($"unhandled sub-chunk content {entry.Content.GetType().Name}");
            }
        }
    }

    /// <summary>Build a struct tree with default sub-chunks for every
    /// sub-chunk-bearing field. Allocates no raw bytes — the owning block
    /// provides them.</summary>
    public static TagStructData NewDefault(TagLayout layout, int structIndex)
    {
        var structLayout = layout.StructLayouts[structIndex];
        var subChunks = new List<TagSubChunkEntry>();
        int fieldIndex = (int)structLayout.FirstFieldIndex;

        while (true)
        {
            var field = layout.Fields[fieldIndex];
            if (field.FieldType == TagFieldType.Terminator)
                break;

            TagSubChunkContent? content = field.FieldType switch
            {
                TagFieldType.Struct =>
                    new TagSubChunkContent.StructContent(NewDefault(layout, (int)field.Definition)),
                TagFieldType.Block =>
                    new TagSubChunkContent.BlockContent(new TagBlockData
                    {
                        BlockIndex = layout.BlockLayouts[(int)field.Definition].Index,
                        Flags = 0,
                        RawData = [],
                        Endian = Endian.Le,
                        Elements = [],
                    }),
                TagFieldType.Array => BuildArrayDefault(layout, field),
                TagFieldType.TagReference => new TagSubChunkContent.TagReferenceContent([]),
                TagFieldType.StringId => new TagSubChunkContent.StringIdContent([]),
                TagFieldType.OldStringId => new TagSubChunkContent.OldStringIdContent([]),
                TagFieldType.Data => new TagSubChunkContent.DataContent([]),
                TagFieldType.ApiInterop => new TagSubChunkContent.ApiInteropContent(new byte[12]),
                TagFieldType.PageableResource => new TagSubChunkContent.ResourceContent(new TagResourceChunk.NullResource()),
                _ => null,
            };

            if (content is not null)
                subChunks.Add(new TagSubChunkEntry { FieldIndex = (uint)fieldIndex, Content = content });

            fieldIndex++;
        }

        return new TagStructData { StructIndex = structLayout.Index, SubChunks = subChunks };

        static TagSubChunkContent BuildArrayDefault(TagLayout layout, TagFieldLayout field)
        {
            var arrayLayout = layout.ArrayLayouts[(int)field.Definition];
            var elements = new List<TagStructData>((int)arrayLayout.Count);
            for (uint i = 0; i < arrayLayout.Count; i++)
                elements.Add(NewDefault(layout, (int)arrayLayout.StructIndex));
            return new TagSubChunkContent.ArrayContent(elements);
        }
    }

    /// <summary>Find a field index by name within this struct (case-sensitive),
    /// or null.</summary>
    public int? FindFieldByName(TagLayout layout, string name)
    {
        var structLayout = layout.StructLayouts[(int)StructIndex];
        int fieldIndex = (int)structLayout.FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fieldIndex];
            if (field.FieldType == TagFieldType.Terminator)
                return null;
            if (layout.GetString(field.NameOffset) == name)
                return fieldIndex;
            fieldIndex++;
        }
    }

    /// <summary>User-addressable field names in declaration order (skips
    /// padding / explanation / terminator / unknown and empty names).</summary>
    public IEnumerable<string> FieldNames(TagLayout layout)
    {
        var structLayout = layout.StructLayouts[(int)StructIndex];
        int fieldIndex = (int)structLayout.FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fieldIndex];
            if (field.FieldType == TagFieldType.Terminator)
                yield break;
            bool skip = field.FieldType is TagFieldType.Pad or TagFieldType.UselessPad
                or TagFieldType.Skip or TagFieldType.Explanation or TagFieldType.Unknown;
            if (!skip)
            {
                string? name = layout.GetString(field.NameOffset);
                if (!string.IsNullOrEmpty(name))
                    yield return name;
            }
            fieldIndex++;
        }
    }

    /// <summary>The sub-chunk content owned by a field, or null.</summary>
    public TagSubChunkContent? SubChunkFor(int fieldIndex)
    {
        foreach (var entry in SubChunks)
            if (entry.FieldIndex == (uint)fieldIndex)
                return entry.Content;
        return null;
    }

    /// <summary>Parse a single field's value (null for container / padding).</summary>
    public TagFieldData? ParseField(TagLayout layout, ReadOnlySpan<byte> structRaw, int fieldIndex, Endian endian) =>
        TagFieldCodec.Deserialize(layout, layout.Fields[fieldIndex], structRaw, SubChunkFor(fieldIndex), endian);

    /// <summary>Write a value back. Primitive values mutate
    /// <paramref name="structRaw"/>; sub-chunk-leaf values swap the matching
    /// entry's content (which must already exist).</summary>
    public void SetField(TagLayout layout, Span<byte> structRaw, int fieldIndex, TagFieldData value)
    {
        var newContent = TagFieldCodec.Serialize(layout.Fields[fieldIndex], value, structRaw);
        if (newContent is null)
            return;
        foreach (var entry in SubChunks)
        {
            if (entry.FieldIndex == (uint)fieldIndex)
            {
                entry.Content = newContent;
                return;
            }
        }
        throw new InvalidOperationException("SetField: sub-chunk entry missing for sub-chunk-bearing field");
    }

    /// <summary>Step into a nested struct field, returning its struct + the
    /// byte offset of its region within <paramref name="elementOffset"/>'s
    /// struct. Returns null if not a struct field or the sub-chunk is missing.</summary>
    public (TagStructData Struct, int Offset, int Size)? NestedStruct(TagLayout layout, int elementOffset, int fieldIndex)
    {
        var field = layout.Fields[fieldIndex];
        if (field.FieldType != TagFieldType.Struct)
            return null;
        if (SubChunkFor(fieldIndex) is not TagSubChunkContent.StructContent c)
            return null;
        int size = layout.StructLayouts[(int)c.Struct.StructIndex].Size;
        return (c.Struct, elementOffset + (int)field.Offset, size);
    }

    /// <summary>Deep copy — recursively clones sub-chunks and their payloads.</summary>
    public TagStructData DeepClone() => new()
    {
        StructIndex = StructIndex,
        SubChunks = SubChunks.Select(e => new TagSubChunkEntry
        {
            FieldIndex = e.FieldIndex,
            Content = CloneContent(e.Content),
        }).ToList(),
    };

    private static TagSubChunkContent CloneContent(TagSubChunkContent c) => c switch
    {
        TagSubChunkContent.StructContent s => new TagSubChunkContent.StructContent(s.Struct.DeepClone()),
        TagSubChunkContent.BlockContent b => new TagSubChunkContent.BlockContent(b.Block.DeepClone()),
        TagSubChunkContent.ArrayContent a => new TagSubChunkContent.ArrayContent(a.Elements.Select(x => x.DeepClone()).ToList()),
        TagSubChunkContent.TagReferenceContent x => new TagSubChunkContent.TagReferenceContent((byte[])x.Payload.Clone()),
        TagSubChunkContent.StringIdContent x => new TagSubChunkContent.StringIdContent((byte[])x.Payload.Clone()),
        TagSubChunkContent.OldStringIdContent x => new TagSubChunkContent.OldStringIdContent((byte[])x.Payload.Clone()),
        TagSubChunkContent.DataContent x => new TagSubChunkContent.DataContent((byte[])x.Payload.Clone()),
        TagSubChunkContent.ApiInteropContent x => new TagSubChunkContent.ApiInteropContent((byte[])x.Payload.Clone()),
        TagSubChunkContent.ResourceContent r => new TagSubChunkContent.ResourceContent(CloneResource(r.Resource)),
        TagSubChunkContent.EmptyPlaceholder => new TagSubChunkContent.EmptyPlaceholder(),
        _ => throw new InvalidOperationException($"unhandled content {c.GetType().Name}"),
    };

    private static TagResourceChunk CloneResource(TagResourceChunk r) => r switch
    {
        TagResourceChunk.NullResource => new TagResourceChunk.NullResource(),
        TagResourceChunk.ExplodedResource ex => new TagResourceChunk.ExplodedResource((byte[])ex.Exploded.Clone(), ex.StructData.DeepClone()),
        TagResourceChunk.XsyncResource xs => new TagResourceChunk.XsyncResource(xs.Version, (byte[])xs.Payload.Clone()),
        _ => throw new InvalidOperationException($"unhandled resource {r.GetType().Name}"),
    };
}
