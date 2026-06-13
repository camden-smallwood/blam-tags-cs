namespace BlamTags;

/// <summary>
/// A located struct in the data tree: the struct's sub-chunk node plus the
/// byte region (a slice of some block's <c>RawData</c>) holding its raw
/// fields. Nested structs/arrays share the enclosing block's buffer at an
/// offset; nested blocks and exploded resources carry their own buffer.
/// Writes through <see cref="Buffer"/> persist because it's the same array
/// reference the data tree owns.
/// </summary>
internal readonly record struct StructRegion(TagStructData Struct, byte[] Buffer, int Offset, int Size);

/// <summary>Path-based navigation into a tag's data tree. Segment grammar:
/// <c>[Type:]name[[index]]</c>.</summary>
internal static class Navigation
{
    /// <summary>Resolve a <c>/</c>-separated field path: descend through the
    /// preceding segments, then locate the final field. Returns the
    /// terminal region + field index, or null.</summary>
    public static (StructRegion Region, int FieldIndex)? LookupField(TagLayout layout, StructRegion start, string path)
    {
        string[] segments = path.Split('/');
        if (segments.Length == 0)
            return null;

        var cur = start;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var next = DescendSegment(layout, cur, segments[i]);
            if (next is null) return null;
            cur = next.Value;
        }

        var (typeFilter, name, _) = ParseSegment(segments[^1]);
        int? fieldIndex = FindFieldInStruct(layout, cur.Struct, name, typeFilter);
        return fieldIndex is null ? null : (cur, fieldIndex.Value);
    }

    /// <summary>Walk every <c>/</c>-separated segment as an intermediate
    /// descent and return the struct region at the end.</summary>
    public static StructRegion? Descend(TagLayout layout, StructRegion start, string path)
    {
        var cur = start;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) continue;
            var next = DescendSegment(layout, cur, segment);
            if (next is null) return null;
            cur = next.Value;
        }
        return cur;
    }

    private static StructRegion? DescendSegment(TagLayout layout, StructRegion cur, string segment)
    {
        var (typeFilter, name, index) = ParseSegment(segment);
        int? fieldIndex = FindFieldInStruct(layout, cur.Struct, name, typeFilter);
        if (fieldIndex is null) return null;
        var field = layout.Fields[fieldIndex.Value];

        switch (field.FieldType)
        {
            case TagFieldType.Struct:
            {
                if (cur.Struct.SubChunkFor(fieldIndex.Value) is not TagSubChunkContent.StructContent c)
                    return null;
                int size = layout.StructLayouts[(int)c.Struct.StructIndex].Size;
                return new StructRegion(c.Struct, cur.Buffer, cur.Offset + (int)field.Offset, size);
            }
            case TagFieldType.Block:
            {
                if (cur.Struct.SubChunkFor(fieldIndex.Value) is not TagSubChunkContent.BlockContent c)
                    return null;
                var block = c.Block;
                int elementSize = layout.StructLayouts[(int)layout.BlockLayouts[(int)field.Definition].StructIndex].Size;
                int idx = index ?? 0;
                int start = idx * elementSize;
                if (start + elementSize > block.RawData.Length || idx >= block.Elements.Count)
                    return null;
                return new StructRegion(block.Elements[idx], block.RawData, start, elementSize);
            }
            case TagFieldType.Array:
            {
                if (cur.Struct.SubChunkFor(fieldIndex.Value) is not TagSubChunkContent.ArrayContent c)
                    return null;
                int elementSize = layout.StructLayouts[(int)layout.ArrayLayouts[(int)field.Definition].StructIndex].Size;
                int idx = index ?? 0;
                int rel = (int)field.Offset + idx * elementSize;
                if (rel + elementSize > cur.Size || idx >= c.Elements.Count)
                    return null;
                return new StructRegion(c.Elements[idx], cur.Buffer, cur.Offset + rel, elementSize);
            }
            case TagFieldType.PageableResource:
            {
                if (cur.Struct.SubChunkFor(fieldIndex.Value) is not TagSubChunkContent.ResourceContent
                    { Resource: TagResourceChunk.ExplodedResource ex })
                    return null;
                int size = layout.StructLayouts[(int)ex.StructData.StructIndex].Size;
                if (ex.Exploded.Length < size) return null;
                return new StructRegion(ex.StructData, ex.Exploded, 0, size);
            }
            default:
                return null;
        }
    }

    private static (string? TypeFilter, string Name, int? Index) ParseSegment(string segment)
    {
        string? typeFilter = null;
        string rest = segment;
        int colon = segment.IndexOf(':');
        if (colon >= 0)
        {
            typeFilter = segment[..colon];
            rest = segment[(colon + 1)..];
        }

        string name = rest;
        int? index = null;
        int open = rest.IndexOf('[');
        if (open >= 0)
        {
            int close = rest.IndexOf(']', open);
            if (close < 0) close = rest.Length;
            if (int.TryParse(rest.AsSpan(open + 1, close - open - 1), out int idx))
                index = idx;
            name = rest[..open];
        }
        return (typeFilter, name, index);
    }

    private static int? FindFieldInStruct(TagLayout layout, TagStructData structData, string name, string? typeFilter)
    {
        if (typeFilter is null)
            return structData.FindFieldByName(layout, name);

        var structLayout = layout.StructLayouts[(int)structData.StructIndex];
        int fieldIndex = (int)structLayout.FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fieldIndex];
            if (field.FieldType == TagFieldType.Terminator)
                return null;
            if (TagStructData.FieldNameMatches(layout.GetString(field.NameOffset), name))
            {
                uint typeNameOffset = layout.FieldTypes[(int)field.TypeIndex].NameOffset;
                string typeName = layout.GetString(typeNameOffset) ?? "";
                if (string.Equals(typeName, typeFilter, StringComparison.OrdinalIgnoreCase))
                    return fieldIndex;
            }
            fieldIndex++;
        }
    }
}
