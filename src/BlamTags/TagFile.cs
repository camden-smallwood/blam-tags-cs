using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// The fixed 64-byte preamble at the start of every tag file:
/// <c>pad[36] + build_version + build_number + version + group_tag +
/// group_version + checksum + signature</c>.
/// </summary>
public sealed class TagFileHeader
{
    /// <summary>36 bytes of zero padding, preserved verbatim.</summary>
    public required byte[] Pad { get; init; }
    public required int BuildVersion { get; init; }
    public required int BuildNumber { get; init; }
    /// <summary>Tag-file format version (distinct from group/layout version).</summary>
    public required uint Version { get; init; }
    /// <summary>4-byte tag group (BE-packed), e.g. <c>scnr</c>.</summary>
    public required uint GroupTag { get; init; }
    public required uint GroupVersion { get; init; }
    public required uint Checksum { get; init; }
    /// <summary>Always <c>BLAM</c>.</summary>
    public required uint Signature { get; init; }

    internal static TagFileHeader Read(TagReader reader)
    {
        byte[] pad = reader.ReadBytes(36);
        int buildVersion = (int)reader.ReadU32();
        int buildNumber = (int)reader.ReadU32();
        uint version = reader.ReadU32();
        uint groupTag = reader.ReadU32();
        uint groupVersion = reader.ReadU32();
        uint checksum = reader.ReadU32();
        long signatureOffset = reader.Position;
        uint signature = reader.ReadU32();
        uint blam = Tag.Of("BLAM");
        if (signature != blam)
            throw TagReadException.BadChunkSignature(signatureOffset, blam, signature);

        return new TagFileHeader
        {
            Pad = pad,
            BuildVersion = buildVersion,
            BuildNumber = buildNumber,
            Version = version,
            GroupTag = groupTag,
            GroupVersion = groupVersion,
            Checksum = checksum,
            Signature = signature,
        };
    }

    internal void Write(TagWriter writer)
    {
        writer.WriteBytes(Pad);
        writer.WriteU32((uint)BuildVersion);
        writer.WriteU32((uint)BuildNumber);
        writer.WriteU32(Version);
        writer.WriteU32(GroupTag);
        writer.WriteU32(GroupVersion);
        writer.WriteU32(Checksum);
        writer.WriteU32(Signature);
    }
}

/// <summary>
/// A fully parsed Halo tag file: the fixed 64-byte header, the mandatory
/// <c>tag!</c> stream, and up to one each of the optional <c>want</c> /
/// <c>info</c> / <c>assd</c> streams (in that fixed order). The read/write
/// path is byte-exact.
/// </summary>
public sealed partial class TagFile
{
    public required TagFileHeader Header { get; init; }
    /// <summary>Wire byte order detected on read; preserved so writers can
    /// round-trip to the same orientation.</summary>
    public required Endian Endian { get; init; }

    internal TagStream TagStream { get; init; } = null!;
    internal TagStream? DependencyListStream { get; set; }
    internal TagStream? ImportInfoStream { get; set; }
    internal TagStream? AssetDepotStorageStream { get; set; }

    /// <summary>Open <paramref name="path"/> and parse a complete tag file.</summary>
    public static TagFile Read(string path) => ReadFromBytes(File.ReadAllBytes(path));

    /// <summary>Serialize this tag to <paramref name="path"/>, byte-exact.</summary>
    public void Write(string path) => File.WriteAllBytes(path, WriteToBytes());

    /// <summary>Parse a complete tag file from an in-memory byte buffer. The
    /// read asserts the file ends exactly at the last consumed stream.</summary>
    public static TagFile ReadFromBytes(ReadOnlySpan<byte> bytes)
    {
        byte[] data = bytes.ToArray();
        Endian endian = DetectEndian(data);
        var reader = new TagReader(data, endian);

        var header = TagFileHeader.Read(reader);
        var tagStream = TagStream.Read(Tag.Of("tag!"), reader);

        TagStream? dependencyList = null, importInfo = null, assetDepot = null;
        while (reader.Position != data.Length)
        {
            uint signature = reader.PeekSignatureAt(reader.Position);
            switch (signature)
            {
                case var s when s == Tag.Of("want"):
                    if (dependencyList is not null) throw TagReadException.DuplicateOptionalStream(s);
                    dependencyList = TagStream.Read(s, reader);
                    break;
                case var s when s == Tag.Of("info"):
                    if (importInfo is not null) throw TagReadException.DuplicateOptionalStream(s);
                    importInfo = TagStream.Read(s, reader);
                    break;
                case var s when s == Tag.Of("assd"):
                    if (assetDepot is not null) throw TagReadException.DuplicateOptionalStream(s);
                    assetDepot = TagStream.Read(s, reader);
                    break;
                default:
                    throw TagReadException.UnknownSubChunkSignature("tag-file top-level", signature);
            }
        }

        return new TagFile
        {
            Header = header,
            Endian = endian,
            TagStream = tagStream,
            DependencyListStream = dependencyList,
            ImportInfoStream = importInfo,
            AssetDepotStorageStream = assetDepot,
        };
    }

    /// <summary>Serialize this tag to a new byte buffer, byte-exact.</summary>
    public byte[] WriteToBytes()
    {
        var writer = new TagWriter();
        Header.Write(writer);
        TagStream.Write(Tag.Of("tag!"), writer);
        DependencyListStream?.Write(Tag.Of("want"), writer);
        ImportInfoStream?.Write(Tag.Of("info"), writer);
        AssetDepotStorageStream?.Write(Tag.Of("assd"), writer);
        return writer.ToArray();
    }

    /// <summary>
    /// Detect wire byte order by inspecting the <c>BLAM</c> magic at offset 60
    /// in both orientations. Mirrors TagTool's <c>DetectEndianFormat</c>.
    /// </summary>
    private static Endian DetectEndian(byte[] data)
    {
        const int sigOffset = 60;
        const int headerSize = 64;
        uint blam = Tag.Of("BLAM");

        if (data.Length < headerSize)
            throw TagReadException.BadChunkSignature(0, blam, 0);

        var sig = data.AsSpan(sigOffset, 4);
        if (BinaryPrimitives.ReadUInt32LittleEndian(sig) == blam)
            return Endian.Le;
        if (BinaryPrimitives.ReadUInt32BigEndian(sig) == blam)
            return Endian.Be;

        // Render the raw bytes in file order for the error message.
        throw TagReadException.BadChunkSignature(sigOffset, blam, BinaryPrimitives.ReadUInt32BigEndian(sig));
    }
}
