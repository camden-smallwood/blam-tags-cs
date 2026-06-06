namespace BlamTags;

// Per-tag instance data tree. Byte ownership is per-block: each
// TagBlockData owns one contiguous raw-data buffer holding all its
// elements' bytes. Nested structs / inline arrays are offset regions
// inside the enclosing block's buffer; nested blocks start fresh buffers.
// This mirrors the on-disk tgbl layout 1:1.

/// <summary>Per-shape payload for a <c>tgst</c> sub-chunk entry. The case
/// reflects the on-disk chunk signature; primitive-leaf bytes are preserved
/// verbatim so writes are byte-exact.</summary>
internal abstract record TagSubChunkContent
{
    private TagSubChunkContent() { }

    /// <summary>Nested struct field — raw bytes live in the enclosing block.</summary>
    public sealed record StructContent(TagStructData Struct) : TagSubChunkContent;
    /// <summary>Nested block field — starts a new byte region.</summary>
    public sealed record BlockContent(TagBlockData Block) : TagSubChunkContent;
    /// <summary>Inline fixed-count array; element raw bytes live inline in the
    /// enclosing block.</summary>
    public sealed record ArrayContent(List<TagStructData> Elements) : TagSubChunkContent;
    /// <summary><c>tgrf</c> payload (4-byte group tag + path).</summary>
    public sealed record TagReferenceContent(byte[] Payload) : TagSubChunkContent;
    /// <summary><c>tgsi</c> payload.</summary>
    public sealed record StringIdContent(byte[] Payload) : TagSubChunkContent;
    /// <summary><c>tgsi</c> payload (old-style string id).</summary>
    public sealed record OldStringIdContent(byte[] Payload) : TagSubChunkContent;
    /// <summary><c>tgda</c> payload.</summary>
    public sealed record DataContent(byte[] Payload) : TagSubChunkContent;
    /// <summary><c>ti][</c> payload for an api-interop field.</summary>
    public sealed record ApiInteropContent(byte[] Payload) : TagSubChunkContent;
    /// <summary>Pageable resource.</summary>
    public sealed record ResourceContent(TagResourceChunk Resource) : TagSubChunkContent;
    /// <summary>An empty <c>tgst</c> (size=0) with no corresponding layout
    /// field — preserved at its position so writes re-emit it byte-exactly.</summary>
    public sealed record EmptyPlaceholder : TagSubChunkContent;
}

/// <summary>Pageable-resource on-disk shape. Only the variants observed in
/// Halo 3 / Reach tags are modeled; <see cref="XsyncResource"/> covers
/// opaque payloads (Halo 4 monolithic — hydration is backlog).</summary>
internal abstract record TagResourceChunk
{
    private TagResourceChunk() { }

    /// <summary><c>tg\0c</c> — empty null resource.</summary>
    public sealed record NullResource : TagResourceChunk;
    /// <summary><c>tgrc</c> — exploded resource: a <c>tgdt</c> payload blob
    /// plus the resource's own struct tree.</summary>
    public sealed record ExplodedResource(byte[] Exploded, TagStructData StructData) : TagResourceChunk;
    /// <summary><c>tgxc</c> — xsync resource. Opaque payload + version.</summary>
    public sealed record XsyncResource(uint Version, byte[] Payload) : TagResourceChunk;
}

/// <summary>One entry in a <c>tgst</c> chunk's sub-chunk list: the owning
/// layout field index (or null for empty placeholders) paired with the
/// entry's typed payload.</summary>
internal sealed class TagSubChunkEntry
{
    public required uint? FieldIndex { get; init; }
    /// <summary>Mutable — setting a field's value swaps this content.</summary>
    public required TagSubChunkContent Content { get; set; }
}
