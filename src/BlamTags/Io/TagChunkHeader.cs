namespace BlamTags;

/// <summary>
/// The 12-byte on-disk header that prefixes every tag chunk: three u32s —
/// signature, version, size.
/// </summary>
/// <param name="Signature">Four ASCII bytes packed BE (see <see cref="FourCc"/>).</param>
/// <param name="Version">Per-chunk-type version. Most leaf chunks are 0;
/// <c>tgly</c> carries the layout version (2/3/4), <c>tgst</c> mirrors its
/// own size.</param>
/// <param name="Size">Payload byte count, excluding the 12-byte header.</param>
public readonly record struct TagChunkHeader(uint Signature, uint Version, uint Size);
