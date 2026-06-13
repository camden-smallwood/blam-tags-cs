namespace BlamTags;

/// <summary>What kind of tag this is: the 4-byte group tag (BE-packed) plus
/// group version.</summary>
public readonly record struct TagGroup(uint Tag, uint Version);

/// <summary>Wire-format shape of a <c>pageable_resource</c> field.</summary>
public enum TagResourceKind { Null, Exploded, Xsync }

/// <summary>Enum or flags option set surfaced for UI / value parsing.</summary>
public abstract record TagOptions
{
    private TagOptions() { }
    /// <summary>Enum field — one value picked from a named set.</summary>
    public sealed record Enum(IReadOnlyList<string> Names, long? Current) : TagOptions;
    /// <summary>Flags field — one entry per named bit with its current state.</summary>
    public sealed record Flags(IReadOnlyList<TagFlagOption> Items) : TagOptions;
}

/// <summary>One named bit in a flags field's declaration.</summary>
public readonly record struct TagFlagOption(string Name, uint Bit, bool IsSet);

/// <summary>
/// A struct instance — the unit fields hang off of (the root element, a
/// block/array element, or a nested struct field all map here). A single
/// mutable facade: edits via <see cref="TagField.Set"/> and the block
/// editors persist through the underlying byte buffer.
/// </summary>
public sealed class TagStruct
{
    internal TagLayout Layout { get; }
    internal TagStructData Data { get; }
    internal byte[] Buffer { get; }
    internal int Offset { get; }
    internal int Size { get; }
    internal Endian Endian { get; }

    internal TagStruct(TagLayout layout, StructRegion region, Endian endian)
    {
        Layout = layout;
        Data = region.Struct;
        Buffer = region.Buffer;
        Offset = region.Offset;
        Size = region.Size;
        Endian = endian;
    }

    internal StructRegion Region => new(Data, Buffer, Offset, Size);

    /// <summary>The schema side of this instance.</summary>
    public TagStructDefinition Definition => new(Layout, (int)Data.StructIndex);

    /// <summary>The struct type's display name.</summary>
    public string Name => Layout.GetString(Layout.StructLayouts[(int)Data.StructIndex].NameOffset) ?? "";

    /// <summary>Size in bytes of one instance of this struct.</summary>
    public int SizeBytes => Size;

    /// <summary>This struct element's raw fixed bytes (the slice of the
    /// enclosing block's buffer it occupies). Used to read fields the
    /// generated schema leaves unnamed — e.g. Halo 2 rigid-body shape
    /// references at a fixed offset. Mirrors the Rust <c>TagStruct::raw</c>.</summary>
    public ReadOnlySpan<byte> RawSpan => Buffer.AsSpan(Offset, Size);

    /// <summary>Walk fields in declaration order, skipping padding /
    /// explanation / terminator / unknown.</summary>
    public IEnumerable<TagField> Fields()
    {
        foreach (var f in FieldsAll())
        {
            var ft = f.FieldType;
            if (ft is TagFieldType.Pad or TagFieldType.UselessPad or TagFieldType.Skip
                or TagFieldType.Explanation or TagFieldType.Unknown)
                continue;
            yield return f;
        }
    }

    /// <summary>Walk every field, including padding (for layout tooling).</summary>
    public IEnumerable<TagField> FieldsAll()
    {
        int start = (int)Layout.StructLayouts[(int)Data.StructIndex].FirstFieldIndex;
        for (int i = start; Layout.Fields[i].FieldType != TagFieldType.Terminator; i++)
            yield return new TagField(this, i);
    }

    /// <summary>User-addressable field names in declaration order.</summary>
    public IEnumerable<string> FieldNames() => Data.FieldNames(Layout);

    /// <summary>Resolve a single field by name (case-sensitive, no descent).</summary>
    public TagField? Field(string name)
    {
        int? fieldIndex = Data.FindFieldByName(Layout, name);
        return fieldIndex is null ? null : new TagField(this, fieldIndex.Value);
    }

    /// <summary>Resolve a <c>/</c>-separated field path.</summary>
    public TagField? FieldPath(string path)
    {
        var hit = Navigation.LookupField(Layout, Region, path);
        if (hit is null) return null;
        var owner = new TagStruct(Layout, hit.Value.Region, Endian);
        return new TagField(owner, hit.Value.FieldIndex);
    }

    /// <summary>Walk a path to a struct (no terminal field lookup).</summary>
    public TagStruct? Descend(string path)
    {
        var region = Navigation.Descend(Layout, Region, path);
        return region is null ? null : new TagStruct(Layout, region.Value, Endian);
    }

    // ---- typed convenience readers (subset of the Rust surface) ----

    /// <summary>Read any integer-shaped field as <see cref="long"/> (null if
    /// missing or not integer-shaped). Note: <c>qword_integer</c> values above
    /// <see cref="long.MaxValue"/> wrap.</summary>
    public long? ReadIntAny(string name) => Field(name)?.Value switch
    {
        TagFieldData.CharInteger v => v.Value,
        TagFieldData.ShortInteger v => v.Value,
        TagFieldData.LongInteger v => v.Value,
        TagFieldData.Int64Integer v => v.Value,
        TagFieldData.ByteInteger v => v.Value,
        TagFieldData.WordInteger v => v.Value,
        TagFieldData.DwordInteger v => v.Value,
        TagFieldData.QwordInteger v => (long)v.Value,
        TagFieldData.CharBlockIndex v => v.Value,
        TagFieldData.ShortBlockIndex v => v.Value,
        TagFieldData.LongBlockIndex v => v.Value,
        TagFieldData.CustomCharBlockIndex v => v.Value,
        TagFieldData.CustomShortBlockIndex v => v.Value,
        TagFieldData.CustomLongBlockIndex v => v.Value,
        TagFieldData.CharEnum v => v.Value,
        TagFieldData.ShortEnum v => v.Value,
        TagFieldData.LongEnum v => v.Value,
        TagFieldData.ByteFlags v => v.Value,
        TagFieldData.WordFlags v => v.Value,
        TagFieldData.LongFlags v => v.Value,
        TagFieldData.ByteBlockFlags v => v.Value,
        TagFieldData.WordBlockFlags v => v.Value,
        TagFieldData.LongBlockFlags v => v.Value,
        _ => null,
    };

    /// <summary>Read a real-shaped field as <see cref="float"/>.</summary>
    public float? ReadReal(string name) => Field(name)?.Value switch
    {
        TagFieldData.Real v => v.Value,
        TagFieldData.RealFraction v => v.Value,
        TagFieldData.RealSlider v => v.Value,
        TagFieldData.Angle v => v.Value,
        _ => null,
    };

    /// <summary>Read a string-id (or old-string-id) field's resolved string
    /// (null if missing / empty).</summary>
    public string? ReadStringId(string name) => Field(name)?.Value switch
    {
        TagFieldData.StringId v => string.IsNullOrEmpty(v.Value.Value) ? null : v.Value.Value,
        TagFieldData.OldStringId v => string.IsNullOrEmpty(v.Value.Value) ? null : v.Value.Value,
        _ => null,
    };

    /// <summary>Read a name-bearing field as a string, handling both inline
    /// <c>string</c>/<c>long string</c> (classic CE/H2) and <c>string id</c>
    /// (gen3) forms. Null if missing/empty.</summary>
    public string? ReadString(string name) => Field(name)?.Value switch
    {
        TagFieldData.String v => string.IsNullOrEmpty(v.Value) ? null : v.Value,
        TagFieldData.LongString v => string.IsNullOrEmpty(v.Value) ? null : v.Value,
        TagFieldData.StringId v => string.IsNullOrEmpty(v.Value.Value) ? null : v.Value.Value,
        TagFieldData.OldStringId v => string.IsNullOrEmpty(v.Value.Value) ? null : v.Value.Value,
        _ => null,
    };

    /// <summary>Read an enum field's resolved variant name (any width).</summary>
    public string? ReadEnumName(string name) => Field(name)?.Value switch
    {
        TagFieldData.CharEnum v => v.Name,
        TagFieldData.ShortEnum v => v.Name,
        TagFieldData.LongEnum v => v.Name,
        _ => null,
    };

    /// <summary>Read a tag-reference field's relative path (null if missing or
    /// null ref).</summary>
    public string? ReadTagRefPath(string name) =>
        (Field(name)?.Value as TagFieldData.TagReference)?.Value.GroupTagAndName?.Name;

    /// <summary>Read a tag-reference field's (group tag, path) pair.</summary>
    public (uint Group, string Path)? ReadTagRefWithGroup(string name) =>
        (Field(name)?.Value as TagFieldData.TagReference)?.Value.GroupTagAndName;

    // ---- typed math readers (mirror Rust api.rs read_quat/read_point3d/etc.) ----
    // Each returns the type's zero/identity default when the field is missing,
    // and throws on a present-but-wrong-type field (the Rust side panics —
    // surfacing a code-vs-schema bug rather than silently defaulting).

    /// <summary>Read a <c>real_quaternion</c> field. Identity when missing;
    /// throws on type mismatch.</summary>
    public RealQuaternion ReadQuat(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealQuaternionValue v => v.Value,
        null => new RealQuaternion(0, 0, 0, 1),
        var other => throw MathTypeMismatch(name, "RealQuaternion", other),
    };

    /// <summary>Read a <c>real_point_3d</c> field. Zero when missing; throws on
    /// type mismatch (use <see cref="ReadVec3"/> for <c>real_vector_3d</c>).</summary>
    public RealPoint3d ReadPoint3d(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealPoint3dValue v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealPoint3d", other),
    };

    /// <summary>Read a 3-float field as a point, accepting either
    /// <c>real_point_3d</c> or <c>real_vector_3d</c> (H2/H3 declare node
    /// translations as the former, Halo CE as the latter). Zero when missing.</summary>
    public RealPoint3d ReadPointOrVec(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealPoint3dValue v => v.Value,
        TagFieldData.RealVector3dValue v => new RealPoint3d(v.Value.I, v.Value.J, v.Value.K),
        null => default,
        var other => throw MathTypeMismatch(name, "RealPoint3d/RealVector3d", other),
    };

    /// <summary>Read a <c>real_vector_3d</c> field. Zero when missing; throws on
    /// type mismatch.</summary>
    public RealVector3d ReadVec3(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealVector3dValue v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealVector3d", other),
    };

    /// <summary>Read a <c>real_point_2d</c> field. Zero when missing; throws on
    /// type mismatch.</summary>
    public RealPoint2d ReadPoint2d(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealPoint2dValue v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealPoint2d", other),
    };

    /// <summary>Read a block-index field as <see cref="short"/> with <c>-1</c>
    /// (none) default — treats all block-index widths as "index or sentinel."</summary>
    public short ReadBlockIndex(string name) => (short)(ReadIntAny(name) ?? -1);

    /// <summary>Read a <c>real_plane_3d</c> field. Zero when missing; throws on
    /// type mismatch.</summary>
    public RealPlane3d ReadPlane3d(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealPlane3dValue v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealPlane3d", other),
    };

    /// <summary>Read a <c>real_rgb_color</c> field. Zero when missing; throws on
    /// type mismatch.</summary>
    public RealRgbColor ReadRgb(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealRgbColorValue v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealRgbColor", other),
    };

    /// <summary>Read a <c>real_bounds</c> field. Zero bounds when missing;
    /// throws on type mismatch.</summary>
    public Bounds<float> ReadRealBounds(string name) => Field(name)?.Value switch
    {
        TagFieldData.RealBounds v => v.Value,
        null => default,
        var other => throw MathTypeMismatch(name, "RealBounds", other),
    };

    private static InvalidOperationException MathTypeMismatch(string name, string expected, TagFieldData? actual) =>
        new($"field '{name}' expected {expected} but was {actual?.GetType().Name ?? "null"}");
}

/// <summary>A resolved field within a <see cref="TagStruct"/>.</summary>
public sealed class TagField(TagStruct owner, int fieldIndex)
{
    internal TagStruct Owner { get; } = owner;
    internal int FieldIndex { get; } = fieldIndex;

    private TagFieldLayout FieldLayout => Owner.Layout.Fields[FieldIndex];

    /// <summary>The schema side of this field.</summary>
    public TagFieldDefinition Definition => new(Owner.Layout, FieldIndex);

    /// <summary>Field display name.</summary>
    public string Name => Owner.Layout.GetString(FieldLayout.NameOffset) ?? "";

    /// <summary>Canonical schema type name.</summary>
    public string TypeName =>
        Owner.Layout.GetString(Owner.Layout.FieldTypes[(int)FieldLayout.TypeIndex].NameOffset) ?? "";

    /// <summary>The field's schema type.</summary>
    public TagFieldType FieldType => FieldLayout.FieldType;

    /// <summary>The field's current value. Null for container / padding
    /// fields — step in via <see cref="AsStruct"/> / <see cref="AsBlock"/> /
    /// <see cref="AsArray"/> / <see cref="AsResource"/>.</summary>
    public TagFieldData? Value =>
        Owner.Data.ParseField(Owner.Layout, Owner.Buffer.AsSpan(Owner.Offset, Owner.Size), FieldIndex, Owner.Endian);

    /// <summary>Write a value. Throws for container fields (mutate those via
    /// the step-in accessors).</summary>
    public void Set(TagFieldData value)
    {
        if (FieldType is TagFieldType.Struct or TagFieldType.Block
            or TagFieldType.Array or TagFieldType.PageableResource)
            throw new InvalidOperationException($"field '{Name}' is a container and is not directly assignable");
        Owner.Data.SetField(Owner.Layout, Owner.Buffer.AsSpan(Owner.Offset, Owner.Size), FieldIndex, value, Owner.Endian);
    }

    /// <summary>Step into a struct field (null if not a struct or missing).</summary>
    public TagStruct? AsStruct()
    {
        if (FieldType != TagFieldType.Struct) return null;
        var nested = Owner.Data.NestedStruct(Owner.Layout, Owner.Offset, FieldIndex);
        if (nested is null) return null;
        var (s, off, size) = nested.Value;
        return new TagStruct(Owner.Layout, new StructRegion(s, Owner.Buffer, off, size), Owner.Endian);
    }

    /// <summary>Step into a block field (null if not a block or missing).</summary>
    public TagBlock? AsBlock()
    {
        if (FieldType != TagFieldType.Block) return null;
        return Owner.Data.SubChunkFor(FieldIndex) is TagSubChunkContent.BlockContent c
            ? new TagBlock(Owner.Layout, c.Block) : null;
    }

    /// <summary>Step into a fixed-count array field (null if not an array or
    /// missing).</summary>
    public TagArray? AsArray()
    {
        if (FieldType != TagFieldType.Array) return null;
        if (Owner.Data.SubChunkFor(FieldIndex) is not TagSubChunkContent.ArrayContent c) return null;
        int arrayOffset = Owner.Offset + (int)FieldLayout.Offset;
        return new TagArray(Owner.Layout, FieldLayout.Definition, Owner.Buffer, arrayOffset, c.Elements, Owner.Endian);
    }

    /// <summary>Step into a pageable-resource field (null if not a resource or
    /// missing).</summary>
    public TagResource? AsResource()
    {
        if (FieldType != TagFieldType.PageableResource) return null;
        return Owner.Data.SubChunkFor(FieldIndex) is TagSubChunkContent.ResourceContent c
            ? new TagResource(Owner.Layout, c.Resource, Owner.Buffer, Owner.Offset, FieldIndex, Owner.Endian)
            : null;
    }

    /// <summary>The raw bytes of a <c>data</c> field (null for non-data fields).</summary>
    public byte[]? AsData() =>
        FieldType == TagFieldType.Data && Owner.Data.SubChunkFor(FieldIndex) is TagSubChunkContent.DataContent c
            ? c.Payload : null;

    /// <summary>Enum/flags option set (null for other field types).</summary>
    public TagOptions? Options()
    {
        var field = FieldLayout;
        bool isEnum = field.FieldType is TagFieldType.CharEnum or TagFieldType.ShortEnum or TagFieldType.LongEnum;
        bool isFlags = field.FieldType is TagFieldType.ByteFlags or TagFieldType.WordFlags or TagFieldType.LongFlags;
        if (!isEnum && !isFlags) return null;

        var names = TagFieldCodec.FieldOptionNames(Owner.Layout, field).ToList();
        var value = Value;
        if (isEnum)
        {
            long? current = value switch
            {
                TagFieldData.CharEnum v => v.Value,
                TagFieldData.ShortEnum v => v.Value,
                TagFieldData.LongEnum v => v.Value,
                _ => null,
            };
            return new TagOptions.Enum(names, current);
        }
        var items = names.Select((name, bit) =>
            new TagFlagOption(name, (uint)bit, value?.FlagBit(bit) ?? false)).ToList();
        return new TagOptions.Flags(items);
    }

    /// <summary>Look up a single flag by name on a flags-typed field.</summary>
    public TagFlag? Flag(string name)
    {
        uint? bit = TagFieldCodec.FindFlagBit(Owner.Layout, FieldLayout, name);
        return bit is null ? null : new TagFlag(this, bit.Value);
    }
}

/// <summary>A variable-count block of same-typed elements. The byte-ownership
/// boundary — a block carries its own buffer. All structural edits funnel
/// through here.</summary>
public sealed class TagBlock
{
    internal TagLayout Layout { get; }
    internal TagBlockData Data { get; }

    internal TagBlock(TagLayout layout, TagBlockData data)
    {
        Layout = layout;
        Data = data;
    }

    private int ElementSizeBytes => Data.ElementSize(Layout);

    public TagBlockDefinition Definition => new(Layout, (int)Data.BlockIndex);
    public int Count => Data.Elements.Count;
    public bool IsEmpty => Count == 0;

    public TagStruct? Element(int index)
    {
        if (index < 0 || index >= Data.Elements.Count) return null;
        int size = ElementSizeBytes;
        return new TagStruct(Layout, new StructRegion(Data.Elements[index], Data.RawData, index * size, size), Data.Endian);
    }

    public IEnumerable<TagStruct> Elements()
    {
        for (int i = 0; i < Data.Elements.Count; i++)
            yield return Element(i)!;
    }

    /// <summary>Append a default-initialized element; returns its new index.</summary>
    public int AddElement() => Data.AddElement(Layout);

    /// <summary>Insert a default element at <paramref name="index"/> (0..=Count).</summary>
    public void InsertElement(int index)
    {
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        Data.InsertElement(Layout, index);
    }

    /// <summary>Duplicate element <paramref name="index"/> after it; returns the copy's index.</summary>
    public int DuplicateElement(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        Data.DuplicateElement(Layout, index);
        return index + 1;
    }

    public void DeleteElement(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        Data.RemoveElement(Layout, index);
    }

    public void SwapElements(int i, int j)
    {
        if (i < 0 || i >= Count) throw new ArgumentOutOfRangeException(nameof(i));
        if (j < 0 || j >= Count) throw new ArgumentOutOfRangeException(nameof(j));
        Data.SwapElements(Layout, i, j);
    }

    public void MoveElement(int from, int to)
    {
        if (from < 0 || from >= Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= Count) throw new ArgumentOutOfRangeException(nameof(to));
        Data.MoveElement(Layout, from, to);
    }

    public void Clear() => Data.Clear();
}

/// <summary>A fixed-count inline array. Elements live contiguously in the
/// enclosing struct's buffer at the array field's offset.</summary>
public sealed class TagArray
{
    internal TagLayout Layout { get; }
    internal uint ArrayLayoutIndex { get; }
    internal byte[] Buffer { get; }
    internal int ArrayOffset { get; }
    internal List<TagStructData> ElementsData { get; }
    internal Endian Endian { get; }

    internal TagArray(
        TagLayout layout, uint arrayLayoutIndex, byte[] buffer, int arrayOffset,
        List<TagStructData> elements, Endian endian)
    {
        Layout = layout;
        ArrayLayoutIndex = arrayLayoutIndex;
        Buffer = buffer;
        ArrayOffset = arrayOffset;
        ElementsData = elements;
        Endian = endian;
    }

    private int ElementSizeBytes =>
        Layout.StructLayouts[(int)Layout.ArrayLayouts[(int)ArrayLayoutIndex].StructIndex].Size;

    public TagArrayDefinition Definition => new(Layout, (int)ArrayLayoutIndex);
    public int Count => ElementsData.Count;
    public bool IsEmpty => Count == 0;

    public TagStruct? Element(int index)
    {
        if (index < 0 || index >= ElementsData.Count) return null;
        int size = ElementSizeBytes;
        return new TagStruct(Layout, new StructRegion(ElementsData[index], Buffer, ArrayOffset + index * size, size), Endian);
    }

    public IEnumerable<TagStruct> Elements()
    {
        for (int i = 0; i < ElementsData.Count; i++)
            yield return Element(i)!;
    }

    public void Swap(int i, int j)
    {
        if (i < 0 || i >= Count) throw new ArgumentOutOfRangeException(nameof(i));
        if (j < 0 || j >= Count) throw new ArgumentOutOfRangeException(nameof(j));
        if (i == j) return;
        (ElementsData[i], ElementsData[j]) = (ElementsData[j], ElementsData[i]);
        int size = ElementSizeBytes;
        int lo = System.Math.Min(i, j) * size + ArrayOffset;
        int hi = System.Math.Max(i, j) * size + ArrayOffset;
        var tmp = Buffer.AsSpan(lo, size).ToArray();
        Array.Copy(Buffer, hi, Buffer, lo, size);
        tmp.CopyTo(Buffer.AsSpan(hi));
    }
}

/// <summary>Read view onto a pageable-resource field.</summary>
public sealed class TagResource
{
    internal TagLayout Layout { get; }
    internal TagResourceChunk Chunk { get; }
    internal byte[] ParentBuffer { get; }
    internal int ParentOffset { get; }
    internal int FieldIndex { get; }
    internal Endian Endian { get; }

    internal TagResource(
        TagLayout layout, TagResourceChunk chunk, byte[] parentBuffer, int parentOffset, int fieldIndex, Endian endian)
    {
        Layout = layout;
        Chunk = chunk;
        ParentBuffer = parentBuffer;
        ParentOffset = parentOffset;
        FieldIndex = fieldIndex;
        Endian = endian;
    }

    public TagResourceKind Kind => Chunk switch
    {
        TagResourceChunk.NullResource => TagResourceKind.Null,
        TagResourceChunk.ExplodedResource => TagResourceKind.Exploded,
        TagResourceChunk.XsyncResource => TagResourceKind.Xsync,
        _ => TagResourceKind.Null,
    };

    public TagResourceDefinition Definition => new(Layout, (int)Layout.Fields[FieldIndex].Definition);

    /// <summary>The 8 inline bytes for this field as they appear on disk
    /// (engine-internal; preserved verbatim through roundtrip).</summary>
    public byte[] InlineBytes()
    {
        int offset = ParentOffset + (int)Layout.Fields[FieldIndex].Offset;
        return ParentBuffer.AsSpan(offset, 8).ToArray();
    }

    /// <summary>The resource header as a walkable struct (Exploded only).</summary>
    public TagStruct? AsStruct()
    {
        if (Chunk is not TagResourceChunk.ExplodedResource ex) return null;
        int size = Layout.StructLayouts[(int)ex.StructData.StructIndex].Size;
        if (ex.Exploded.Length < size) return null;
        return new TagStruct(Layout, new StructRegion(ex.StructData, ex.Exploded, 0, size), Endian);
    }

    /// <summary>The raw <c>tgdt</c> payload of an Exploded resource.</summary>
    public byte[]? ExplodedPayload => (Chunk as TagResourceChunk.ExplodedResource)?.Exploded;

    /// <summary>The opaque XSync payload.</summary>
    public byte[]? XsyncPayload => (Chunk as TagResourceChunk.XsyncResource)?.Payload;
}

/// <summary>A single flag bit addressed by name.</summary>
public sealed class TagFlag(TagField field, uint bit)
{
    internal TagField Field { get; } = field;

    public uint Bit { get; } = bit;

    public string Name => TagFieldCodec.FlagNameFromBit(Field.Owner.Layout, Field.Owner.Layout.Fields[Field.FieldIndex], Bit);

    public bool IsSet => Field.Value?.FlagBit((int)Bit) ?? false;

    /// <summary>Set or clear this bit.</summary>
    public void Set(bool on)
    {
        var value = Field.Value;
        var updated = value?.WithFlagBit((int)Bit, on);
        if (updated is not null)
            Field.Set(updated);
    }

    /// <summary>Toggle and return the new state.</summary>
    public bool Toggle()
    {
        bool next = !IsSet;
        Set(next);
        return next;
    }
}
