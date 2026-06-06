using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Append-only writer for tag binary output. Always emits little-endian:
/// the library never serializes a big-endian tag back to disk, so writes
/// are unconditionally LE (matching the Rust write path). Nested chunk
/// bodies are built into their own <see cref="TagWriter"/> and folded into
/// the parent via <see cref="WriteChunkContent"/>.
/// </summary>
internal sealed class TagWriter
{
    private readonly MemoryStream _stream = new();

    public long Length => _stream.Length;

    public void WriteU8(byte v) => _stream.WriteByte(v);

    public void WriteU16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        _stream.Write(b);
    }

    public void WriteU32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _stream.Write(b);
    }

    public void WriteU64(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        _stream.Write(b);
    }

    public void WriteBytes(ReadOnlySpan<byte> b) => _stream.Write(b);

    /// <summary>Write a 12-byte chunk header (signature, version, size).</summary>
    public void WriteChunkHeader(uint signature, uint version, uint size)
    {
        WriteU32(signature);
        WriteU32(version);
        WriteU32(size);
    }

    /// <summary>Write a chunk header followed by its payload; size is the
    /// content length.</summary>
    public void WriteChunkContent(uint signature, uint version, ReadOnlySpan<byte> content)
    {
        WriteChunkHeader(signature, version, (uint)content.Length);
        WriteBytes(content);
    }

    public byte[] ToArray() => _stream.ToArray();
}
