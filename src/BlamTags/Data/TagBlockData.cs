namespace BlamTags;

/// <summary>
/// A <c>tgbl</c> chunk: a variable-count array of struct elements.
/// <see cref="RawData"/> is a single concatenated buffer of length
/// <c>Elements.Count * elementSize</c>; element <c>i</c>'s bytes live at
/// <c>RawData[i*elementSize ..]</c>. Nested struct/array fields are offset
/// regions inside this buffer; nested blocks start fresh buffers.
/// </summary>
/// <remarks>
/// Two shapes, selected by <see cref="Flags"/> bit 0: <b>complex</b> (clear)
/// gives each element a <c>tgst</c> sub-chunk; <b>simple</b> (set) is element
/// bytes only, no per-element <c>tgst</c>.
/// </remarks>
internal sealed class TagBlockData
{
    public required uint BlockIndex { get; init; }
    /// <summary>Block flags. Bit 0 toggles simple vs complex; other bits are
    /// preserved verbatim for roundtrip.</summary>
    public required uint Flags { get; set; }
    /// <summary>Concatenated element bytes, in source-wire order.</summary>
    public required byte[] RawData { get; set; }
    /// <summary>Source byte order of <see cref="RawData"/>.</summary>
    public required Endian Endian { get; init; }
    /// <summary>Per-element struct trees. Simple-block elements have empty
    /// sub-chunks.</summary>
    public required List<TagStructData> Elements { get; init; }

    /// <summary>Classic Halo 2 block header (12/16 bytes: <c>4cc + version +
    /// count + size</c>) that precedes this block's elements on disk. The
    /// version selects the element FieldSet variant; the count is re-synced
    /// from <see cref="Elements"/> on encode. The root block carries one too.
    /// Null for Halo CE (headerless) and MCC blocks.</summary>
    public byte[]? ClassicBlockHeader { get; init; }

    /// <summary>Trailing bytes after the structured body that no layout field
    /// references (appended sample/cache data, Halo 2 root only). Preserved
    /// verbatim for a byte-exact round-trip. Null otherwise.</summary>
    public byte[]? ClassicTrailing { get; init; }

    /// <summary>Parse a <c>tgbl</c> chunk. Complex vs simple is decided by
    /// flags bit 0.</summary>
    public static TagBlockData Read(TagLayout layout, TagBlockLayout definition, TagReader reader)
    {
        var header = reader.ReadValidatedChunkHeader("tgbl");
        long blockOffset = reader.Position;

        uint elementCount = reader.ReadU32();
        uint flags = reader.ReadU32();

        var structLayout = layout.StructLayouts[(int)definition.StructIndex];
        int elementSize = structLayout.Size;

        byte[] rawData = reader.ReadBytes(elementSize * (int)elementCount);

        var elements = new List<TagStructData>((int)elementCount);
        if ((flags & 1) == 0)
        {
            // Complex block: per-element tgst sub-chunks.
            for (uint i = 0; i < elementCount; i++)
                elements.Add(TagStructData.Read(layout, structLayout, reader));
        }
        else
        {
            // Simple block: raw bytes only. Container fields still need
            // in-memory scaffolding (consumes no disk bytes).
            for (uint i = 0; i < elementCount; i++)
                elements.Add(TagStructData.NewDefault(layout, (int)structLayout.Index));
        }

        reader.CheckChunkEnd("tgbl", blockOffset, header.Size);

        return new TagBlockData
        {
            BlockIndex = definition.Index,
            Flags = flags,
            RawData = rawData,
            Endian = reader.Endian,
            Elements = elements,
        };
    }

    /// <summary>Write this block as a <c>tgbl</c> chunk.</summary>
    public void Write(TagLayout layout, TagWriter writer)
    {
        var body = new TagWriter();
        body.WriteU32((uint)Elements.Count);
        body.WriteU32(Flags);
        body.WriteBytes(RawData);

        if ((Flags & 1) == 0)
            foreach (var element in Elements)
                element.Write(layout, body);

        writer.WriteChunkContent(Tag.Of("tgbl"), 0, body.ToArray());
    }

    /// <summary>Size of one element's byte region. For a populated block the
    /// on-disk element size is authoritative as <c>RawData.Length /
    /// Elements.Count</c> — what the classic encoder uses, and essential for
    /// VERSIONED classic blocks whose elements aren't the schema's latest
    /// size. Falls back to the layout struct size for empty blocks.</summary>
    public int ElementSize(TagLayout layout)
    {
        if (Elements.Count > 0 && RawData.Length > 0)
            return RawData.Length / Elements.Count;
        int structIndex = (int)layout.BlockLayouts[(int)BlockIndex].StructIndex;
        return layout.StructLayouts[structIndex].Size;
    }

    /// <summary>A block with exactly one zero-filled default element. Used as
    /// the root block of a freshly created tag file.</summary>
    public static TagBlockData NewRootDefault(TagLayout layout, uint blockIndex)
    {
        var blockLayout = layout.BlockLayouts[(int)blockIndex];
        var structLayout = layout.StructLayouts[(int)blockLayout.StructIndex];
        int elementSize = structLayout.Size;

        return new TagBlockData
        {
            BlockIndex = blockIndex,
            Flags = 0,
            RawData = new byte[elementSize],
            Endian = Endian.Le,
            Elements = [TagStructData.NewDefault(layout, (int)blockLayout.StructIndex)],
        };
    }

    private int StructIndexOf(TagLayout layout) => (int)layout.BlockLayouts[(int)BlockIndex].StructIndex;

    /// <summary>Append a zero-initialized element. Returns its new index.</summary>
    public int AddElement(TagLayout layout)
    {
        int size = ElementSize(layout);
        var nr = new byte[RawData.Length + size];
        RawData.CopyTo(nr, 0);
        RawData = nr;
        Elements.Add(TagStructData.NewDefault(layout, StructIndexOf(layout)));
        return Elements.Count - 1;
    }

    /// <summary>Insert a zero-initialized element at <paramref name="index"/>.</summary>
    public void InsertElement(TagLayout layout, int index)
    {
        int size = ElementSize(layout);
        int at = index * size;
        var nr = new byte[RawData.Length + size];
        Array.Copy(RawData, 0, nr, 0, at);
        Array.Copy(RawData, at, nr, at + size, RawData.Length - at);
        RawData = nr;
        Elements.Insert(index, TagStructData.NewDefault(layout, StructIndexOf(layout)));
    }

    /// <summary>Deep-copy element <paramref name="index"/>, placing the copy
    /// immediately after it.</summary>
    public void DuplicateElement(TagLayout layout, int index)
    {
        int size = ElementSize(layout);
        int src = index * size;
        var copy = RawData.AsSpan(src, size).ToArray();
        int at = (index + 1) * size;
        var nr = new byte[RawData.Length + size];
        Array.Copy(RawData, 0, nr, 0, at);
        copy.CopyTo(nr.AsSpan(at));
        Array.Copy(RawData, at, nr, at + size, RawData.Length - at);
        RawData = nr;
        Elements.Insert(index + 1, Elements[index].DeepClone());
    }

    /// <summary>Remove the element at <paramref name="index"/>.</summary>
    public void RemoveElement(TagLayout layout, int index)
    {
        int size = ElementSize(layout);
        int at = index * size;
        var nr = new byte[RawData.Length - size];
        Array.Copy(RawData, 0, nr, 0, at);
        Array.Copy(RawData, at + size, nr, at, RawData.Length - at - size);
        RawData = nr;
        Elements.RemoveAt(index);
    }

    /// <summary>Swap elements <paramref name="i"/> and <paramref name="j"/>.</summary>
    public void SwapElements(TagLayout layout, int i, int j)
    {
        if (i == j) return;
        int size = ElementSize(layout);
        (Elements[i], Elements[j]) = (Elements[j], Elements[i]);
        int lo = System.Math.Min(i, j) * size;
        int hi = System.Math.Max(i, j) * size;
        var tmp = RawData.AsSpan(lo, size).ToArray();
        Array.Copy(RawData, hi, RawData, lo, size);
        tmp.CopyTo(RawData.AsSpan(hi));
    }

    /// <summary>Move element <paramref name="from"/> to final index
    /// <paramref name="to"/> (remove + insert semantics).</summary>
    public void MoveElement(TagLayout layout, int from, int to)
    {
        if (from == to) return;
        int size = ElementSize(layout);
        var bytes = RawData.AsSpan(from * size, size).ToArray();
        var removed = new byte[RawData.Length - size];
        Array.Copy(RawData, 0, removed, 0, from * size);
        Array.Copy(RawData, from * size + size, removed, from * size, RawData.Length - from * size - size);
        int dst = to * size;
        var nr = new byte[removed.Length + size];
        Array.Copy(removed, 0, nr, 0, dst);
        bytes.CopyTo(nr.AsSpan(dst));
        Array.Copy(removed, dst, nr, dst + size, removed.Length - dst);
        RawData = nr;
        var elem = Elements[from];
        Elements.RemoveAt(from);
        Elements.Insert(to, elem);
    }

    /// <summary>Remove all elements.</summary>
    public void Clear()
    {
        RawData = [];
        Elements.Clear();
    }

    /// <summary>Deep copy — clones the raw buffer and every element tree.</summary>
    public TagBlockData DeepClone() => new()
    {
        BlockIndex = BlockIndex,
        Flags = Flags,
        RawData = (byte[])RawData.Clone(),
        Endian = Endian,
        Elements = Elements.Select(e => e.DeepClone()).ToList(),
        ClassicBlockHeader = (byte[]?)ClassicBlockHeader?.Clone(),
        ClassicTrailing = (byte[]?)ClassicTrailing?.Clone(),
    };
}
