namespace BlamTags;

/// <summary>The category of a <see cref="TagSchemaException"/>.</summary>
public enum TagSchemaErrorKind
{
    Io,
    Json,
    UnknownReference,
    BadFieldDefinition,
    UnknownFieldType,
    BadGuid,
    BadGroupTag,
    StructSizeMismatch,
}

/// <summary>
/// Schema-import failures from <see cref="TagLayout.FromJson"/>. Distinct
/// from <see cref="TagReadException"/>, which covers binary-read failures.
/// </summary>
public sealed class TagSchemaException(TagSchemaErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TagSchemaErrorKind Kind { get; } = kind;

    public static TagSchemaException UnknownReference(string kind, string name) =>
        new(TagSchemaErrorKind.UnknownReference, $"schema references unknown {kind} \"{name}\"");

    public static TagSchemaException BadFieldDefinition(string field, string ty) =>
        new(TagSchemaErrorKind.BadFieldDefinition, $"field \"{field}\" of type \"{ty}\" has invalid definition value");

    public static TagSchemaException UnknownFieldType(string s) =>
        new(TagSchemaErrorKind.UnknownFieldType, $"unknown field type \"{s}\"");

    public static TagSchemaException BadGuid(string s) =>
        new(TagSchemaErrorKind.BadGuid, $"invalid guid \"{s}\" (expected 32 hex chars)");

    public static TagSchemaException BadGroupTag(string s) =>
        new(TagSchemaErrorKind.BadGroupTag, $"invalid group tag \"{s}\" (expected 4 chars)");

    public static TagSchemaException StructSizeMismatch(string name, uint schema, int computed) =>
        new(TagSchemaErrorKind.StructSizeMismatch,
            $"computed size mismatch for struct \"{name}\": schema says {schema}, computed {computed}");
}
