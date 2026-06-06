namespace BlamTags;

/// <summary>
/// One of the four top-level chunks in a tag file: <c>tag!</c> (main
/// payload), <c>want</c> (dependency list), <c>info</c> (import info), or
/// <c>assd</c> (asset depot storage). All share the same structure: a
/// <c>blay</c> block layout followed by a <c>bdat</c> root block data.
/// </summary>
internal sealed class TagStream
{
    /// <summary>The <c>blay</c> chunk — the schema used to interpret
    /// <see cref="Data"/>.</summary>
    public required TagLayout Layout { get; init; }
    /// <summary>The <c>bdat</c> chunk — the root block whose elements are the
    /// actual tag values.</summary>
    public required TagBlockData Data { get; init; }

    /// <summary>Read a stream chunk with the given expected outer signature.</summary>
    public static TagStream Read(uint chunkSignature, TagReader reader)
    {
        long chunkHeaderOffset = reader.Position;
        var chunkHeader = reader.ReadChunkHeader();
        if (chunkHeader.Signature != chunkSignature)
            throw TagReadException.BadChunkSignature(chunkHeaderOffset, chunkSignature, chunkHeader.Signature);
        if (chunkHeader.Version != 0)
            throw TagReadException.BadChunkVersion("tag stream", chunkHeader.Version);
        long chunkOffset = reader.Position;

        var layout = TagLayout.Read(reader);
        var rootBlockLayout = layout.BlockLayouts[(int)layout.Header.TagGroupBlockIndex];

        // bdat chunk — version is 1 (not 0), so check it ourselves.
        long bdatOffset = reader.Position;
        var bdatHeader = reader.ReadChunkHeader();
        if (bdatHeader.Signature != Tag.Of("bdat"))
            throw TagReadException.BadChunkSignature(bdatOffset, Tag.Of("bdat"), bdatHeader.Signature);
        if (bdatHeader.Version != 1)
            throw TagReadException.BadChunkVersion("bdat", bdatHeader.Version);
        long blockDataOffset = reader.Position;

        var data = TagBlockData.Read(layout, rootBlockLayout, reader);

        reader.CheckChunkEnd("bdat", blockDataOffset, bdatHeader.Size);
        reader.CheckChunkEnd("tag stream", chunkOffset, chunkHeader.Size);

        return new TagStream { Layout = layout, Data = data };
    }

    /// <summary>Build a new stream: the given layout + a root block with one
    /// zero-filled default element. Used by tag-from-schema creation.</summary>
    public static TagStream NewDefault(TagLayout layout)
    {
        uint rootBlockIndex = layout.Header.TagGroupBlockIndex;
        var data = TagBlockData.NewRootDefault(layout, rootBlockIndex);
        return new TagStream { Layout = layout, Data = data };
    }

    /// <summary>Write this stream. Payload is a <c>blay</c> chunk then a
    /// <c>bdat</c> chunk; the outer stream chunk and <c>blay</c> are version 0,
    /// <c>bdat</c> is version 1.</summary>
    public void Write(uint chunkSignature, TagWriter writer)
    {
        var streamBody = new TagWriter();
        Layout.Write(streamBody);

        var bdatBody = new TagWriter();
        Data.Write(Layout, bdatBody);
        streamBody.WriteChunkContent(Tag.Of("bdat"), 1, bdatBody.ToArray());

        writer.WriteChunkContent(chunkSignature, 0, streamBody.ToArray());
    }
}
