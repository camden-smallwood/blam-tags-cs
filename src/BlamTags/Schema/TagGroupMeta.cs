namespace BlamTags;

/// <summary>
/// Group-level metadata extracted from a schema JSON file. Not part of
/// <see cref="TagLayout"/> (the <c>blay</c> chunk doesn't carry it) but
/// needed by the tag-file header when creating a new tag from scratch.
/// </summary>
/// <param name="Tag">BE-packed 4-byte group tag.</param>
/// <param name="Version">Group version.</param>
/// <param name="Flags">Group flags.</param>
/// <param name="ParentTag">BE-packed parent group tag, if any.</param>
public readonly record struct TagGroupMeta(uint Tag, uint Version, uint Flags, uint? ParentTag);
