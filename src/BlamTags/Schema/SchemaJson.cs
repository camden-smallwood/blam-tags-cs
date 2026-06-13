using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlamTags;

// System.Text.Json shapes for the per-group schema files the dumper
// produces. Unknown JSON properties are ignored by default (matching
// serde). Registries are plain Dictionaries during load + parent merge;
// the importer re-sorts them ordinally (to mirror Rust's BTreeMap) before
// assigning indices.

internal sealed class TagSchemaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("parent_tag")] public string? ParentTag { get; set; }
    [JsonPropertyName("version")] public uint Version { get; set; }
    [JsonPropertyName("flags")] public uint Flags { get; set; }
    [JsonPropertyName("block")] public string Block { get; set; } = "";

    [JsonPropertyName("blocks")] public Dictionary<string, BlockJson> Blocks { get; set; } = new();
    [JsonPropertyName("structs")] public Dictionary<string, StructJson> Structs { get; set; } = new();
    [JsonPropertyName("arrays")] public Dictionary<string, ArrayJson> Arrays { get; set; } = new();
    [JsonPropertyName("enums_flags")] public Dictionary<string, EnumJson> EnumsFlags { get; set; } = new();
    [JsonPropertyName("datas")] public Dictionary<string, DataJson> Datas { get; set; } = new();
    [JsonPropertyName("resources")] public Dictionary<string, ResourceJson> Resources { get; set; } = new();
    [JsonPropertyName("interops")] public Dictionary<string, InteropJson> Interops { get; set; } = new();

    /// <summary>Classic Halo 2 versioned layouts: <c>base struct name →
    /// (on-disk version → variant struct name)</c>. A block/struct element's
    /// 16-byte header carries a version that selects which FieldSet variant
    /// to decode. Empty for MCC (gen3) and Halo CE schemas.</summary>
    [JsonPropertyName("struct_versions")]
    public Dictionary<string, Dictionary<string, string>> StructVersions { get; set; } = new();
}

internal sealed class BlockJson
{
    [JsonPropertyName("max_count")] public uint MaxCount { get; set; }
    [JsonPropertyName("struct")] public string StructName { get; set; } = "";
}

internal sealed class StructJson
{
    [JsonPropertyName("guid")] public string Guid { get; set; } = "";
    [JsonPropertyName("size")] public uint Size { get; set; }
    [JsonPropertyName("fields")] public List<FieldJson> Fields { get; set; } = new();
    /// <summary>Classic Halo 2 inline-struct 4cc tag (e.g. <c>masd</c>). When
    /// present, this struct carries a 16-byte block-style header on disk whose
    /// version selects its FieldSet variant. Null = untagged (always v0).</summary>
    [JsonPropertyName("tag")] public string? Tag { get; set; }
}

internal sealed class FieldJson
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("definition")] public JsonElement Definition { get; set; }
    [JsonPropertyName("group_tag")] public string? GroupTag { get; set; }
}

internal sealed class ArrayJson
{
    [JsonPropertyName("count")] public uint Count { get; set; }
    [JsonPropertyName("struct")] public string StructName { get; set; } = "";
}

internal sealed class EnumJson
{
    [JsonPropertyName("options")] public List<string?> Options { get; set; } = new();
}

internal sealed class DataJson;

internal sealed class ResourceJson
{
    [JsonPropertyName("flags")] public ulong Flags { get; set; }
    [JsonPropertyName("struct")] public string StructName { get; set; } = "";
}

internal sealed class InteropJson
{
    [JsonPropertyName("guid")] public string Guid { get; set; } = "";
    [JsonPropertyName("struct")] public string StructName { get; set; } = "";
}
