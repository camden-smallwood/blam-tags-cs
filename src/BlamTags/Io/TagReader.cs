using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Cursor over an in-memory tag file. Tag files are small, so the whole
/// buffer is held in memory (matching the Rust <c>read_from_bytes</c>
/// path) and primitive reads dispatch on <see cref="Endian"/>. Reads past
/// the end surface as <see cref="TagReadErrorKind.UnexpectedEof"/> rather
/// than a raw index-out-of-range, keeping the read path panic-free on
/// malformed input.
/// </summary>
internal sealed class TagReader(byte[] data, Endian endian)
{
    private readonly byte[] _data = data;

    public Endian Endian { get; } = endian;
    public int Position { get; private set; }
    public int Length => _data.Length;

    public void Seek(int position) => Position = position;

    private ReadOnlySpan<byte> Take(int n, string what)
    {
        if (n < 0 || Position + n > _data.Length)
            throw TagReadException.UnexpectedEof(what);
        var span = _data.AsSpan(Position, n);
        Position += n;
        return span;
    }

    public byte ReadU8() => Take(1, "u8")[0];

    public ushort ReadU16()
    {
        var s = Take(2, "u16");
        return Endian == Endian.Le
            ? BinaryPrimitives.ReadUInt16LittleEndian(s)
            : BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    public uint ReadU32()
    {
        var s = Take(4, "u32");
        return Endian == Endian.Le
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    public ulong ReadU64()
    {
        var s = Take(8, "u64");
        return Endian == Endian.Le
            ? BinaryPrimitives.ReadUInt64LittleEndian(s)
            : BinaryPrimitives.ReadUInt64BigEndian(s);
    }

    /// <summary>Read exactly <paramref name="n"/> bytes into a fresh array.</summary>
    public byte[] ReadBytes(int n) => Take(n, "bytes").ToArray();

    /// <summary>Read a fixed 16-byte guid / inline byte array.</summary>
    public byte[] ReadGuid() => Take(16, "guid").ToArray();

    /// <summary>Peek 4 bytes at <paramref name="at"/> as a signature without
    /// moving the cursor.</summary>
    public uint PeekSignatureAt(int at)
    {
        var s = _data.AsSpan(at, 4);
        return Endian == Endian.Le
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    /// <summary>Read a 12-byte chunk header (signature, version, size), no
    /// validation.</summary>
    public TagChunkHeader ReadChunkHeader() => new(ReadU32(), ReadU32(), ReadU32());

    /// <summary>Read a chunk header and require its signature to match
    /// <paramref name="expected"/> and its version to be 0.</summary>
    public TagChunkHeader ReadValidatedChunkHeader(string expected)
    {
        long offset = Position;
        var header = ReadChunkHeader();
        uint want = Tag.Of(expected);
        if (header.Signature != want)
            throw TagReadException.BadChunkSignature(offset, want, header.Signature);
        if (header.Version != 0)
            throw TagReadException.BadChunkVersion(expected, header.Version);
        return header;
    }

    /// <summary>Read a chunk header, validate its signature (version is
    /// preserved, not checked), then read the payload bytes.</summary>
    public (uint Version, byte[] Content) ReadChunkContent(string expectedSig)
    {
        long offset = Position;
        var header = ReadChunkHeader();
        uint want = Tag.Of(expectedSig);
        if (header.Signature != want)
            throw TagReadException.BadChunkSignature(offset, want, header.Signature);
        byte[] content = ReadBytes((int)header.Size);
        return (header.Version, content);
    }

    /// <summary>Validate that a header's count matches
    /// <c>payloadSize / entrySize</c>.</summary>
    public static void CheckCountMatchesSize(string chunk, uint headerCount, uint payloadSize, uint entrySize)
    {
        uint derived = payloadSize / entrySize;
        if (headerCount != derived)
            throw TagReadException.CountMismatch(chunk, headerCount, derived);
    }

    /// <summary>Validate that a chunk read finished where its header's size
    /// implied.</summary>
    public void CheckChunkEnd(string chunk, long startedAt, uint expectedSize)
    {
        long expectedEnd = startedAt + expectedSize;
        if (Position != expectedEnd)
            throw TagReadException.ChunkSizeMismatch(chunk, startedAt, Position, expectedEnd);
    }
}
