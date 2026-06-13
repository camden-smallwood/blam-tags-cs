namespace BlamTags;

/// <summary>The category of a <see cref="TagReadException"/>.</summary>
/// <remarks>
/// Mirrors the variants of the Rust <c>TagReadError</c>. New kinds may be
/// added over time — treat unknown values defensively.
/// </remarks>
public enum TagReadErrorKind
{
    Io,
    BadChunkSignature,
    BadChunkVersion,
    ChunkSizeMismatch,
    CountMismatch,
    UnsupportedLayoutVersion,
    UnsupportedBlockLayoutVersion,
    UnsupportedFieldType,
    MissingSubChunk,
    UnknownSubChunkSignature,
    InvalidUtf8,
    StringOffsetOutOfBounds,
    DuplicateOptionalStream,
    UnexpectedEof,
    /// <summary>A classic (Halo CE / Halo 2) tag body failed to decode —
    /// not-classic header, corrupt block header, or unconsumed trailing bytes.</summary>
    ClassicDecode,
}

/// <summary>
/// Every failure on the binary read path. Carries enough context (offsets,
/// expected/actual signatures, chunk names) to diagnose a malformed tag.
/// The read path never throws raw <see cref="IndexOutOfRangeException"/> —
/// out-of-bounds reads surface as <see cref="TagReadErrorKind.UnexpectedEof"/>.
/// </summary>
public sealed class TagReadException(TagReadErrorKind kind, string message) : Exception(message)
{
    public TagReadErrorKind Kind { get; } = kind;

    public static TagReadException BadChunkSignature(long offset, uint expected, uint got) =>
        new(TagReadErrorKind.BadChunkSignature,
            $"bad chunk signature at offset 0x{offset:X}: expected \"{Tag.Show(expected)}\", got \"{Tag.Show(got)}\"");

    public static TagReadException BadChunkVersion(string chunk, uint version) =>
        new(TagReadErrorKind.BadChunkVersion, $"\"{chunk}\" chunk has unsupported version {version}");

    public static TagReadException ChunkSizeMismatch(string chunk, long startedAt, long endedAt, long expectedEnd) =>
        new(TagReadErrorKind.ChunkSizeMismatch,
            $"\"{chunk}\" chunk size mismatch: started at 0x{startedAt:X}, ended at 0x{endedAt:X}, expected end 0x{expectedEnd:X}");

    public static TagReadException CountMismatch(string chunk, uint headerCount, uint derivedCount) =>
        new(TagReadErrorKind.CountMismatch,
            $"\"{chunk}\" count mismatch: header says {headerCount}, derived from payload size = {derivedCount}");

    public static TagReadException UnsupportedLayoutVersion(uint v) =>
        new(TagReadErrorKind.UnsupportedLayoutVersion, $"unsupported layout payload version {v} (expected 1..=4)");

    public static TagReadException UnsupportedFieldType(string typeName) =>
        new(TagReadErrorKind.UnsupportedFieldType, $"unsupported field type \"{typeName}\"");

    public static TagReadException UnknownSubChunkSignature(string context, uint signature) =>
        new(TagReadErrorKind.UnknownSubChunkSignature, $"unknown sub-chunk signature \"{Tag.Show(signature)}\" in {context}");

    public static TagReadException InvalidUtf8(string context) =>
        new(TagReadErrorKind.InvalidUtf8, $"invalid UTF-8 in {context}");

    public static TagReadException DuplicateOptionalStream(uint signature) =>
        new(TagReadErrorKind.DuplicateOptionalStream,
            $"duplicate optional stream \"{Tag.Show(signature)}\" — tags carry at most one each of want / info / assd");

    public static TagReadException UnexpectedEof(string chunk) =>
        new(TagReadErrorKind.UnexpectedEof, $"unexpected EOF while reading \"{chunk}\"");

    public static TagReadException ClassicDecode(string message) =>
        new(TagReadErrorKind.ClassicDecode, message);
}
