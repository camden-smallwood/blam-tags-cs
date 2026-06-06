namespace BlamTags;

/// <summary>
/// Wire byte order of a tag file. Detected once per file (by peeking the
/// fixed <c>BLAM</c> signature in both orientations) and threaded through
/// the entire read tree, then preserved on the parsed tag so writers can
/// round-trip to the same orientation.
/// </summary>
public enum Endian
{
    /// <summary>Little-endian (PC / MCC). The common case.</summary>
    Le,
    /// <summary>Big-endian (Xbox 360 / legacy debug builds).</summary>
    Be,
}
