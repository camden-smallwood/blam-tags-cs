using System.Text;
using System.Text.Json;

namespace BlamTags;

public sealed partial class TagLayout
{
    /// <summary>
    /// Build a <see cref="TagLayout"/> from a per-group JSON schema file.
    /// The result matches what <see cref="Read"/> would produce from an
    /// equivalent <c>blay</c> chunk, with every struct's size + field
    /// offsets computed and cross-checked against the schema's declared
    /// sizes.
    /// </summary>
    public static TagLayout FromJson(string path) => FromJsonWithMeta(path).Layout;

    /// <summary>Like <see cref="FromJson"/> but also returns the group-level
    /// metadata (group tag, version, flags, parent) the JSON carries but
    /// <c>blay</c> doesn't.</summary>
    public static (TagLayout Layout, TagGroupMeta Meta) FromJsonWithMeta(string path)
    {
        TagSchemaJson schema;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            schema = JsonSerializer.Deserialize<TagSchemaJson>(bytes, TagSchemaJson.Options)
                ?? throw new TagSchemaException(TagSchemaErrorKind.Json, "schema JSON deserialized to null");
        }
        catch (IOException e)
        {
            throw new TagSchemaException(TagSchemaErrorKind.Io, $"I/O error reading schema: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new TagSchemaException(TagSchemaErrorKind.Json, $"JSON parse error: {e.Message}", e);
        }

        var meta = new TagGroupMeta(
            SchemaImport.ParseGroupTag(schema.Tag),
            schema.Version,
            schema.Flags,
            schema.ParentTag is null ? null : SchemaImport.ParseGroupTag(schema.ParentTag));

        string defsDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        SchemaImport.MergeParentSchemas(schema, defsDir);
        var layout = SchemaImport.BuildLayoutFromSchema(schema, defsDir);
        return (layout, meta);
    }
}

/// <summary>JSON schema → <see cref="TagLayout"/> import logic.</summary>
internal static class SchemaImport
{
    private readonly record struct FieldTypeInfo(TagFieldType Ty, string Canonical, uint Size, uint NeedsSubChunk);

    /// <summary>JSON <c>"type"</c> string → metadata (on-wire canonical name,
    /// byte size, sub-chunk flag). Returns null for an unknown type.</summary>
    private static FieldTypeInfo? FieldType(string ty) => ty switch
    {
        "string" => new(TagFieldType.String, "string", 32, 0),
        "long_string" => new(TagFieldType.LongString, "long string", 256, 0),
        "string_id" => new(TagFieldType.StringId, "string id", 4, 1),
        "old_string_id" => new(TagFieldType.OldStringId, "old string id", 4, 1),
        "char_integer" => new(TagFieldType.CharInteger, "char integer", 1, 0),
        "short_integer" => new(TagFieldType.ShortInteger, "short integer", 2, 0),
        "long_integer" => new(TagFieldType.LongInteger, "long integer", 4, 0),
        "int64_integer" => new(TagFieldType.Int64Integer, "int64 integer", 8, 0),
        "byte_integer" => new(TagFieldType.ByteInteger, "byte integer", 1, 0),
        "word_integer" => new(TagFieldType.WordInteger, "word integer", 2, 0),
        "dword_integer" => new(TagFieldType.DwordInteger, "dword integer", 4, 0),
        "qword_integer" => new(TagFieldType.QwordInteger, "qword integer", 8, 0),
        "angle" => new(TagFieldType.Angle, "angle", 4, 0),
        "tag" => new(TagFieldType.Tag, "tag", 4, 0),
        "char_enum" => new(TagFieldType.CharEnum, "char enum", 1, 0),
        "short_enum" => new(TagFieldType.ShortEnum, "short enum", 2, 0),
        "long_enum" => new(TagFieldType.LongEnum, "long enum", 4, 0),
        "long_flags" => new(TagFieldType.LongFlags, "long flags", 4, 0),
        "word_flags" => new(TagFieldType.WordFlags, "word flags", 2, 0),
        "byte_flags" => new(TagFieldType.ByteFlags, "byte flags", 1, 0),
        "point_2d" => new(TagFieldType.Point2d, "point 2d", 4, 0),
        "rectangle_2d" => new(TagFieldType.Rectangle2d, "rectangle 2d", 8, 0),
        "rgb_color" => new(TagFieldType.RgbColor, "rgb color", 4, 0),
        "argb_color" => new(TagFieldType.ArgbColor, "argb color", 4, 0),
        "real" => new(TagFieldType.Real, "real", 4, 0),
        "real_slider" => new(TagFieldType.RealSlider, "real slider", 4, 0),
        "real_fraction" => new(TagFieldType.RealFraction, "real fraction", 4, 0),
        "real_point_2d" => new(TagFieldType.RealPoint2d, "real point 2d", 8, 0),
        "real_point_3d" => new(TagFieldType.RealPoint3d, "real point 3d", 12, 0),
        "real_vector_2d" => new(TagFieldType.RealVector2d, "real vector 2d", 8, 0),
        "real_vector_3d" => new(TagFieldType.RealVector3d, "real vector 3d", 12, 0),
        "real_quaternion" => new(TagFieldType.RealQuaternion, "real quaternion", 16, 0),
        "real_euler_angles_2d" => new(TagFieldType.RealEulerAngles2d, "real euler angles 2d", 8, 0),
        "real_euler_angles_3d" => new(TagFieldType.RealEulerAngles3d, "real euler angles 3d", 12, 0),
        "real_plane_2d" => new(TagFieldType.RealPlane2d, "real plane 2d", 12, 0),
        "real_plane_3d" => new(TagFieldType.RealPlane3d, "real plane 3d", 16, 0),
        "real_rgb_color" => new(TagFieldType.RealRgbColor, "real rgb color", 12, 0),
        "real_argb_color" => new(TagFieldType.RealArgbColor, "real argb color", 16, 0),
        "real_hsv_color" => new(TagFieldType.RealHsvColor, "real hsv color", 12, 0),
        "real_ahsv_color" => new(TagFieldType.RealAhsvColor, "real ahsv color", 16, 0),
        "short_bounds" => new(TagFieldType.ShortIntegerBounds, "short integer bounds", 4, 0),
        "angle_bounds" => new(TagFieldType.AngleBounds, "angle bounds", 8, 0),
        "real_bounds" => new(TagFieldType.RealBounds, "real bounds", 8, 0),
        "fraction_bounds" => new(TagFieldType.FractionBounds, "fraction bounds", 8, 0),
        "tag_reference" => new(TagFieldType.TagReference, "tag reference", 16, 1),
        "block" => new(TagFieldType.Block, "block", 12, 1),
        "long_block_flags" => new(TagFieldType.LongBlockFlags, "long block flags", 4, 0),
        "word_block_flags" => new(TagFieldType.WordBlockFlags, "word block flags", 2, 0),
        "byte_block_flags" => new(TagFieldType.ByteBlockFlags, "byte block flags", 1, 0),
        "char_block_index" => new(TagFieldType.CharBlockIndex, "char block index", 1, 0),
        "custom_char_block_index" => new(TagFieldType.CustomCharBlockIndex, "custom char block index", 1, 0),
        "short_block_index" => new(TagFieldType.ShortBlockIndex, "short block index", 2, 0),
        "custom_short_block_index" => new(TagFieldType.CustomShortBlockIndex, "custom short block index", 2, 0),
        "long_block_index" => new(TagFieldType.LongBlockIndex, "long block index", 4, 0),
        "custom_long_block_index" => new(TagFieldType.CustomLongBlockIndex, "custom long block index", 4, 0),
        "data" => new(TagFieldType.Data, "data", 20, 1),
        "vertex_buffer" => new(TagFieldType.VertexBuffer, "vertex buffer", 32, 0),
        "pad" => new(TagFieldType.Pad, "pad", 0, 0),
        "useless_pad" => new(TagFieldType.UselessPad, "useless pad", 0, 0),
        "skip" => new(TagFieldType.Skip, "skip", 0, 0),
        "explanation" => new(TagFieldType.Explanation, "explanation", 0, 0),
        "custom" => new(TagFieldType.Custom, "custom", 0, 0),
        "struct" => new(TagFieldType.Struct, "struct", 0, 1),
        "array" => new(TagFieldType.Array, "array", 0, 0),
        "tag_resource" => new(TagFieldType.PageableResource, "pageable resource", 8, 1),
        "tag_interop" => new(TagFieldType.ApiInterop, "api interop", 12, 1),
        "terminator" => new(TagFieldType.Terminator, "terminator X", 0, 0),
        "non_cache_runtime_value" => new(TagFieldType.NonCacheRuntimeValue, "non-cache runtime value", 4, 0),
        _ => null,
    };

    /// <summary>String table builder — dedups identical strings so name
    /// offsets point at shared bytes. Offset 0 is the empty string.</summary>
    private sealed class StringTable
    {
        private readonly List<byte> _bytes = [0];
        private readonly Dictionary<string, uint> _offsets = new() { [""] = 0 };

        public uint Intern(string s)
        {
            if (_offsets.TryGetValue(s, out uint off))
                return off;
            off = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.UTF8.GetBytes(s));
            _bytes.Add(0);
            _offsets[s] = off;
            return off;
        }

        public byte[] ToArray() => _bytes.ToArray();
        public int Length => _bytes.Count;
    }

    public static uint ParseGroupTag(string s)
    {
        if (s.Length != 4)
            throw TagSchemaException.BadGroupTag(s);
        foreach (char c in s)
            if (c > 0xFF)
                throw TagSchemaException.BadGroupTag(s);
        return ((uint)(byte)s[0] << 24) | ((uint)(byte)s[1] << 16) | ((uint)(byte)s[2] << 8) | (byte)s[3];
    }

    private static byte[] ParseGuid(string s)
    {
        if (s.Length != 32)
            throw TagSchemaException.BadGuid(s);
        var outBytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out outBytes[i]))
                throw TagSchemaException.BadGuid(s);
        }
        return outBytes;
    }

    /// <summary>Ordinal-sorted keys of a registry — mirrors Rust's BTreeMap
    /// iteration order, which determines index assignment.</summary>
    private static List<string> SortedKeys<T>(Dictionary<string, T> map)
    {
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static Dictionary<string, uint> IndexMap(List<string> sortedKeys)
    {
        var map = new Dictionary<string, uint>(sortedKeys.Count, StringComparer.Ordinal);
        for (int i = 0; i < sortedKeys.Count; i++)
            map[sortedKeys[i]] = (uint)i;
        return map;
    }

    public static TagLayout BuildLayoutFromSchema(TagSchemaJson schema, string defsDir)
    {
        _ = ParseGroupTag(schema.Tag); // validate early

        var strings = new StringTable();

        // field_types registry — grown on demand as fields are emitted.
        var fieldTypes = new List<TagFieldTypeLayout>();
        var fieldTypeIndexByName = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint InternFieldType(string canonical, uint size, uint needsSub)
        {
            if (fieldTypeIndexByName.TryGetValue(canonical, out uint existing))
                return existing;
            uint nameOffset = strings.Intern(canonical);
            uint i = (uint)fieldTypes.Count;
            fieldTypes.Add(new TagFieldTypeLayout { NameOffset = nameOffset, Size = size, NeedsSubChunk = needsSub });
            fieldTypeIndexByName[canonical] = i;
            return i;
        }

        // Ordinal-sorted registry views + name→index maps.
        var structKeys = SortedKeys(schema.Structs);
        var blockKeys = SortedKeys(schema.Blocks);
        var arrayKeys = SortedKeys(schema.Arrays);
        var enumKeys = SortedKeys(schema.EnumsFlags);
        var dataKeys = SortedKeys(schema.Datas);
        var resourceKeys = SortedKeys(schema.Resources);
        var interopKeys = SortedKeys(schema.Interops);

        var structIndex = IndexMap(structKeys);
        var blockIndex = IndexMap(blockKeys);
        var arrayIndex = IndexMap(arrayKeys);
        var enumIndex = IndexMap(enumKeys);
        var dataIndex = IndexMap(dataKeys);
        var resourceIndex = IndexMap(resourceKeys);
        var interopIndex = IndexMap(interopKeys);

        uint ResolveStruct(string name) =>
            structIndex.TryGetValue(name, out uint i) ? i : throw TagSchemaException.UnknownReference("struct", name);

        var customSearchNames = new List<uint>();

        // data_definition_name_offsets from `datas` keys.
        var dataDefNames = new List<uint>(dataKeys.Count);
        foreach (var key in dataKeys)
            dataDefNames.Add(strings.Intern(key));

        // string_lists (enums/flags): options go into string_offsets
        // contiguously; each list points at its slice.
        var stringOffsets = new List<uint>();
        var stringLists = new List<TagStringList>();
        foreach (var key in enumKeys)
        {
            uint listNameOffset = strings.Intern(key);
            uint first = (uint)stringOffsets.Count;
            var options = schema.EnumsFlags[key].Options;
            foreach (var opt in options)
                stringOffsets.Add(opt is null ? 0 : strings.Intern(opt));
            stringLists.Add(new TagStringList { Offset = listNameOffset, Count = (uint)options.Count, First = first });
        }

        // array_layouts.
        var arrayLayouts = new List<TagArrayLayout>(arrayKeys.Count);
        foreach (var key in arrayKeys)
        {
            var a = schema.Arrays[key];
            arrayLayouts.Add(new TagArrayLayout
            {
                NameOffset = strings.Intern(key),
                Count = a.Count,
                StructIndex = ResolveStruct(a.StructName),
            });
        }

        // resource_layouts.
        var resourceLayouts = new List<TagResourceLayout>(resourceKeys.Count);
        foreach (var key in resourceKeys)
        {
            var r = schema.Resources[key];
            resourceLayouts.Add(new TagResourceLayout
            {
                NameOffset = strings.Intern(key),
                Unknown = (uint)r.Flags,
                StructIndex = ResolveStruct(r.StructName),
            });
        }

        // interop_layouts.
        var interopLayouts = new List<TagInteropLayout>(interopKeys.Count);
        foreach (var key in interopKeys)
        {
            var ix = schema.Interops[key];
            interopLayouts.Add(new TagInteropLayout
            {
                NameOffset = strings.Intern(key),
                StructIndex = ResolveStruct(ix.StructName),
                Guid = ParseGuid(ix.Guid),
            });
        }

        // block_layouts.
        var blockLayouts = new List<TagBlockLayout>(blockKeys.Count);
        for (int i = 0; i < blockKeys.Count; i++)
        {
            var b = schema.Blocks[blockKeys[i]];
            blockLayouts.Add(new TagBlockLayout
            {
                Index = (uint)i,
                NameOffset = strings.Intern(blockKeys[i]),
                MaxCount = b.MaxCount,
                StructIndex = ResolveStruct(b.StructName),
            });
        }

        // struct_layouts + the flat `fields` array.
        var structLayouts = new List<TagStructLayout>(structKeys.Count);
        var fields = new List<TagFieldLayout>();
        for (int i = 0; i < structKeys.Count; i++)
        {
            var structSchema = schema.Structs[structKeys[i]];
            uint first = (uint)fields.Count;

            foreach (var field in structSchema.Fields)
            {
                var info = FieldType(field.Type) ?? throw TagSchemaException.UnknownFieldType(field.Type);
                uint typeIndex = InternFieldType(info.Canonical, info.Size, info.NeedsSubChunk);
                uint fieldNameOffset = field.Name is null ? 0 : strings.Intern(field.Name);
                uint definition = ResolveFieldDefinition(field, info.Ty,
                    structIndex, blockIndex, arrayIndex, enumIndex, dataIndex, resourceIndex, interopIndex);

                fields.Add(new TagFieldLayout
                {
                    NameOffset = fieldNameOffset,
                    TypeIndex = typeIndex,
                    Definition = definition,
                    FieldType = info.Ty,
                    Offset = 0,
                });
            }

            structLayouts.Add(new TagStructLayout
            {
                Index = (uint)i,
                Guid = ParseGuid(structSchema.Guid),
                NameOffset = strings.Intern(structKeys[i]),
                FirstFieldIndex = first,
                Size = 0,
                Version = 0,
            });
        }

        // Root block → its struct supplies the layout-level guid/root size.
        if (!blockIndex.TryGetValue(schema.Block, out uint rootBlockIndex))
            throw TagSchemaException.UnknownReference("block", schema.Block);
        int rootStructIndex = (int)blockLayouts[(int)rootBlockIndex].StructIndex;
        byte[] layoutGuid = structLayouts[rootStructIndex].Guid;
        uint schemaRootSize = schema.Structs[structKeys[rootStructIndex]].Size;

        var header = new TagLayoutHeader
        {
            TagGroupBlockIndex = rootBlockIndex,
            StringDataSize = (uint)strings.Length,
            StringOffsetCount = (uint)stringOffsets.Count,
            StringListCount = (uint)stringLists.Count,
            CustomBlockIndexSearchNamesCount = (uint)customSearchNames.Count,
            DataDefinitionNameCount = (uint)dataDefNames.Count,
            ArrayLayoutCount = (uint)arrayLayouts.Count,
            FieldTypeCount = (uint)fieldTypes.Count,
            FieldCount = (uint)fields.Count,
            AggregateLayoutCount = 0,
            StructLayoutCount = (uint)structLayouts.Count,
            BlockLayoutCount = (uint)blockLayouts.Count,
            ResourceLayoutCount = (uint)resourceLayouts.Count,
            InteropLayoutCount = (uint)interopLayouts.Count,
        };

        var result = new TagLayout
        {
            RootDataSize = schemaRootSize,
            Guid = layoutGuid,
            Version = 3, // JSON-built layouts use payload version 3
            Header = header,
            StringData = strings.ToArray(),
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

        // Precompute tmpl-custom expansion sizes, keyed by global field index
        // (the flat `fields` order = structs in sorted order, fields in
        // declaration order).
        var tmplExpansions = new Dictionary<int, uint>();
        {
            int globalFieldIdx = 0;
            foreach (var key in structKeys)
            {
                foreach (var field in schema.Structs[key].Fields)
                {
                    if (field.Type == "custom"
                        && field.GroupTag == "tmpl"
                        && field.Definition.ValueKind == JsonValueKind.String)
                    {
                        string? target = field.Definition.GetString();
                        if (target is not null)
                        {
                            uint exp = TmplExpansionSize(defsDir, target);
                            if (exp > 0)
                                tmplExpansions[globalFieldIdx] = exp;
                        }
                    }
                    globalFieldIdx++;
                }
            }
        }

        for (int i = 0; i < result.StructLayouts.Count; i++)
            result.ComputeStructLayout(i);

        // Cross-check computed vs declared sizes; apply tmpl expansion if the
        // declared size is larger and the struct has tmpl customs.
        for (int i = 0; i < structKeys.Count; i++)
        {
            int computed = result.StructLayouts[i].Size;
            int declared = (int)schema.Structs[structKeys[i]].Size;
            if (computed == declared)
                continue;
            if (computed < declared)
            {
                int fieldIdx = (int)result.StructLayouts[i].FirstFieldIndex;
                int applied = 0;
                while (result.Fields[fieldIdx].FieldType != TagFieldType.Terminator)
                {
                    if (tmplExpansions.TryGetValue(fieldIdx, out uint exp))
                    {
                        result.Fields[fieldIdx].Definition = exp;
                        applied += (int)exp;
                    }
                    fieldIdx++;
                }
                if (applied > 0)
                {
                    result.StructLayouts[i].Size = 0;
                    result.ComputeStructLayout(i);
                }
            }
            computed = result.StructLayouts[i].Size;
            if (computed != declared)
                throw TagSchemaException.StructSizeMismatch(structKeys[i], schema.Structs[structKeys[i]].Size, computed);
        }

        result.Header.StringDataSize = (uint)result.StringData.Length;
        return result;
    }

    /// <summary>Translate a field's JSON <c>definition</c> into the u32 the
    /// layout stores. Interpretation depends on the field type.</summary>
    private static uint ResolveFieldDefinition(
        FieldJson field, TagFieldType ty,
        Dictionary<string, uint> structIndex, Dictionary<string, uint> blockIndex,
        Dictionary<string, uint> arrayIndex, Dictionary<string, uint> enumIndex,
        Dictionary<string, uint> dataIndex, Dictionary<string, uint> resourceIndex,
        Dictionary<string, uint> interopIndex)
    {
        var def = field.Definition;

        // custom: 0 by default (tmpl expansion patched later).
        if (ty == TagFieldType.Custom)
            return 0;

        // Primitives & no-definition types: 0.
        if (ty is TagFieldType.Unknown or TagFieldType.String or TagFieldType.LongString
            or TagFieldType.StringId or TagFieldType.OldStringId or TagFieldType.CharInteger
            or TagFieldType.ShortInteger or TagFieldType.LongInteger or TagFieldType.Int64Integer
            or TagFieldType.ByteInteger or TagFieldType.WordInteger or TagFieldType.DwordInteger
            or TagFieldType.QwordInteger or TagFieldType.Angle or TagFieldType.Tag
            or TagFieldType.Point2d or TagFieldType.Rectangle2d or TagFieldType.RgbColor
            or TagFieldType.ArgbColor or TagFieldType.Real or TagFieldType.RealSlider
            or TagFieldType.RealFraction or TagFieldType.RealPoint2d or TagFieldType.RealPoint3d
            or TagFieldType.RealVector2d or TagFieldType.RealVector3d or TagFieldType.RealQuaternion
            or TagFieldType.RealEulerAngles2d or TagFieldType.RealEulerAngles3d or TagFieldType.RealPlane2d
            or TagFieldType.RealPlane3d or TagFieldType.RealRgbColor or TagFieldType.RealArgbColor
            or TagFieldType.RealHsvColor or TagFieldType.RealAhsvColor or TagFieldType.ShortIntegerBounds
            or TagFieldType.AngleBounds or TagFieldType.RealBounds or TagFieldType.FractionBounds
            or TagFieldType.VertexBuffer or TagFieldType.CustomCharBlockIndex
            or TagFieldType.CustomShortBlockIndex or TagFieldType.CustomLongBlockIndex
            or TagFieldType.Terminator or TagFieldType.NonCacheRuntimeValue)
        {
            return 0;
        }

        // pad/skip/useless_pad: definition is a byte count.
        if (ty is TagFieldType.Pad or TagFieldType.UselessPad or TagFieldType.Skip)
        {
            if (def.ValueKind == JsonValueKind.Number && def.TryGetUInt32(out uint count))
                return count;
            throw TagSchemaException.BadFieldDefinition(field.Name ?? "", field.Type);
        }

        // explanation: text not stored in the layout's definition slot.
        if (ty == TagFieldType.Explanation)
            return 0;

        // tag_reference: definition object holds flags.
        if (ty == TagFieldType.TagReference)
        {
            if (def.ValueKind == JsonValueKind.Object
                && def.TryGetProperty("flags", out var flags)
                && flags.ValueKind == JsonValueKind.Number
                && flags.TryGetUInt32(out uint f))
                return f;
            return 0;
        }

        // Named-registry types: resolve by name.
        if (def.ValueKind != JsonValueKind.String)
            throw TagSchemaException.BadFieldDefinition(field.Name ?? "", field.Type);
        string name = def.GetString()!;

        (uint? found, string kind) = ty switch
        {
            TagFieldType.Struct => (Lookup(structIndex, name), "struct"),
            TagFieldType.Block or TagFieldType.LongBlockFlags or TagFieldType.WordBlockFlags
                or TagFieldType.ByteBlockFlags or TagFieldType.CharBlockIndex
                or TagFieldType.ShortBlockIndex or TagFieldType.LongBlockIndex
                => (Lookup(blockIndex, name), "block"),
            TagFieldType.Array => (Lookup(arrayIndex, name), "array"),
            TagFieldType.CharEnum or TagFieldType.ShortEnum or TagFieldType.LongEnum
                or TagFieldType.LongFlags or TagFieldType.WordFlags or TagFieldType.ByteFlags
                => (Lookup(enumIndex, name), "enum_or_flags"),
            TagFieldType.Data => (Lookup(dataIndex, name), "data"),
            TagFieldType.PageableResource => (Lookup(resourceIndex, name), "resource"),
            TagFieldType.ApiInterop => (Lookup(interopIndex, name), "interop"),
            _ => (null, "?"),
        };

        return found ?? throw TagSchemaException.UnknownReference(kind, name);

        static uint? Lookup(Dictionary<string, uint> map, string name) =>
            map.TryGetValue(name, out uint i) ? i : null;
    }

    /// <summary>Walk a tag's parent chain via <c>_meta.json</c> and merge each
    /// ancestor's registries into <paramref name="schema"/> (child wins on
    /// key collision). Tolerates missing files / bad data as "no parent".</summary>
    public static void MergeParentSchemas(TagSchemaJson schema, string defsDir)
    {
        var tagIndex = ReadTagIndex(defsDir);
        if (tagIndex is null)
            return;

        string? currentParent = schema.ParentTag;
        for (int depth = 0; depth < 32; depth++)
        {
            if (currentParent is null)
                break;
            if (!tagIndex.TryGetValue(currentParent, out string? name))
                break;
            TagSchemaJson? parent = TryLoadSchema(defsDir, name);
            if (parent is null)
                break;

            MergeInto(schema.Blocks, parent.Blocks);
            MergeInto(schema.Structs, parent.Structs);
            MergeInto(schema.Arrays, parent.Arrays);
            MergeInto(schema.EnumsFlags, parent.EnumsFlags);
            MergeInto(schema.Datas, parent.Datas);
            MergeInto(schema.Resources, parent.Resources);
            MergeInto(schema.Interops, parent.Interops);

            currentParent = parent.ParentTag;
        }

        static void MergeInto<T>(Dictionary<string, T> child, Dictionary<string, T> parent)
        {
            foreach (var (k, v) in parent)
                child.TryAdd(k, v);
        }
    }

    /// <summary>Cumulative root-struct size of a <c>tmpl</c> target's parent
    /// chain (the target itself excluded). 0 if unresolvable.</summary>
    private static uint TmplExpansionSize(string defsDir, string targetTag)
    {
        var tagIndex = ReadTagIndex(defsDir);
        if (tagIndex is null)
            return 0;

        uint sum = 0;
        string cur = targetTag;
        for (int depth = 0; depth < 32; depth++)
        {
            if (!tagIndex.TryGetValue(cur, out string? name))
                break;
            TagSchemaJson? schema = TryLoadSchema(defsDir, name);
            if (schema is null)
                break;
            if (cur != targetTag)
            {
                if (!schema.Blocks.TryGetValue(schema.Block, out var block))
                    break;
                if (!schema.Structs.TryGetValue(block.StructName, out var rs))
                    break;
                sum = unchecked(sum + rs.Size);
            }
            if (schema.ParentTag is null)
                break;
            cur = schema.ParentTag;
        }
        return sum;
    }

    private static Dictionary<string, string>? ReadTagIndex(string defsDir)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(defsDir, "_meta.json"));
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("tag_index", out var ti) || ti.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in ti.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString()!;
            return map;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private static TagSchemaJson? TryLoadSchema(string defsDir, string name)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(defsDir, $"{name}.json"));
            return JsonSerializer.Deserialize<TagSchemaJson>(bytes, TagSchemaJson.Options);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
