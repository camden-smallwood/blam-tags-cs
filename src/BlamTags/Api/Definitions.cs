namespace BlamTags;

/// <summary>Root handle over a <see cref="TagLayout"/>, from
/// <see cref="TagFile.Definitions"/>.</summary>
public readonly struct TagDefinitions(TagLayout layout)
{
    private readonly TagLayout _layout = layout;

    /// <summary>The root struct definition — the tag group's top-level struct.</summary>
    public TagStructDefinition RootStruct()
    {
        int rootBlockIndex = (int)_layout.Header.TagGroupBlockIndex;
        int structIndex = (int)_layout.BlockLayouts[rootBlockIndex].StructIndex;
        return new TagStructDefinition(_layout, structIndex);
    }
}

/// <summary>A struct definition — a <see cref="TagLayout"/> + struct index.</summary>
public readonly struct TagStructDefinition(TagLayout layout, int structIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = structIndex;

    public string Name => _layout.GetString(_layout.StructLayouts[_index].NameOffset) ?? "";
    public int Size => _layout.StructLayouts[_index].Size;
    public byte[] Guid => _layout.StructLayouts[_index].Guid;
    public uint Version => _layout.StructLayouts[_index].Version;

    /// <summary>Field definitions in declaration order, stopping before the
    /// terminator (padding included — filter with <see cref="TagFieldDefinition.IsPadding"/>).</summary>
    public IEnumerable<TagFieldDefinition> Fields()
    {
        var layout = _layout;
        int start = (int)layout.StructLayouts[_index].FirstFieldIndex;
        for (int i = start; layout.Fields[i].FieldType != TagFieldType.Terminator; i++)
            yield return new TagFieldDefinition(layout, i);
    }
}

/// <summary>A field definition — one row in <see cref="TagLayout.Fields"/>.</summary>
public readonly struct TagFieldDefinition(TagLayout layout, int fieldIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = fieldIndex;

    public string Name => _layout.GetString(_layout.Fields[_index].NameOffset) ?? "";
    public string TypeName =>
        _layout.GetString(_layout.FieldTypes[(int)_layout.Fields[_index].TypeIndex].NameOffset) ?? "";
    public TagFieldType FieldType => _layout.Fields[_index].FieldType;
    public uint Offset => _layout.Fields[_index].Offset;

    public bool IsPadding => FieldType is TagFieldType.Pad or TagFieldType.UselessPad
        or TagFieldType.Skip or TagFieldType.Explanation or TagFieldType.Unknown or TagFieldType.Terminator;

    public TagStructDefinition? AsStruct() => FieldType == TagFieldType.Struct
        ? new TagStructDefinition(_layout, (int)_layout.Fields[_index].Definition) : null;
    public TagBlockDefinition? AsBlock() => FieldType == TagFieldType.Block
        ? new TagBlockDefinition(_layout, (int)_layout.Fields[_index].Definition) : null;
    public TagArrayDefinition? AsArray() => FieldType == TagFieldType.Array
        ? new TagArrayDefinition(_layout, (int)_layout.Fields[_index].Definition) : null;
    public TagResourceDefinition? AsResource() => FieldType == TagFieldType.PageableResource
        ? new TagResourceDefinition(_layout, (int)_layout.Fields[_index].Definition) : null;
    public TagApiInteropDefinition? AsApiInterop() => FieldType == TagFieldType.ApiInterop
        ? new TagApiInteropDefinition(_layout, (int)_layout.Fields[_index].Definition) : null;
}

/// <summary>A block definition.</summary>
public readonly struct TagBlockDefinition(TagLayout layout, int blockLayoutIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = blockLayoutIndex;

    public string Name => _layout.GetString(_layout.BlockLayouts[_index].NameOffset) ?? "";
    public uint MaxCount => _layout.BlockLayouts[_index].MaxCount;
    public TagStructDefinition StructDefinition() =>
        new(_layout, (int)_layout.BlockLayouts[_index].StructIndex);
}

/// <summary>An array definition (fixed-count inline array of a struct).</summary>
public readonly struct TagArrayDefinition(TagLayout layout, int arrayLayoutIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = arrayLayoutIndex;

    public string Name => _layout.GetString(_layout.ArrayLayouts[_index].NameOffset) ?? "";
    public uint Count => _layout.ArrayLayouts[_index].Count;
    public TagStructDefinition StructDefinition() =>
        new(_layout, (int)_layout.ArrayLayouts[_index].StructIndex);
}

/// <summary>An api-interop definition (from the <c>]==[</c> chunk).</summary>
public readonly struct TagApiInteropDefinition(TagLayout layout, int interopLayoutIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = interopLayoutIndex;

    public string Name => _layout.GetString(_layout.InteropLayouts[_index].NameOffset) ?? "";
    public byte[] Guid => _layout.InteropLayouts[_index].Guid;
    public TagStructDefinition Descriptor() =>
        new(_layout, (int)_layout.InteropLayouts[_index].StructIndex);
}

/// <summary>A pageable-resource definition.</summary>
public readonly struct TagResourceDefinition(TagLayout layout, int resourceLayoutIndex)
{
    private readonly TagLayout _layout = layout;
    private readonly int _index = resourceLayoutIndex;

    public string Name => _layout.GetString(_layout.ResourceLayouts[_index].NameOffset) ?? "";
    public TagStructDefinition StructDefinition() =>
        new(_layout, (int)_layout.ResourceLayouts[_index].StructIndex);
}
