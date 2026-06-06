using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Block-compression decoders (BC1/BC2/BC3/BC5) — a port of the canonical
/// <c>bcdec</c> algorithm (matching the Rust <c>bcdec_rs</c> the oracle uses).
/// Each writes a 4×4 block into a tightly-packed staging buffer: RGBA8
/// (pitch 16) for BC1/2/3, RG8 (pitch 8) for BC5.
/// </summary>
internal static class BcDec
{
    /// <summary>BC1 (DXT1): 8-byte block, 4-color or 3-color+transparent.</summary>
    public static void Bc1(ReadOnlySpan<byte> block, Span<byte> dst) =>
        ColorBlock(block, dst, pitch: 16, onlyOpaque: false);

    /// <summary>BC2 (DXT3): explicit 4-bit alpha + BC1 color (always 4-color).</summary>
    public static void Bc2(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        ColorBlock(block[8..], dst, pitch: 16, onlyOpaque: true);
        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(block);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int shift = 4 * (j * 4 + i);
                byte n = (byte)((alpha >> shift) & 0xF);
                dst[j * 16 + i * 4 + 3] = (byte)(n * 17);
            }
    }

    /// <summary>BC3 (DXT5): smooth-alpha (+1 division) + BC1 color (always 4-color).</summary>
    public static void Bc3(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        ColorBlock(block[8..], dst, pitch: 16, onlyOpaque: true);
        AlphaBlock(block[..8], dst, pitch: 16, pixelStride: 4, channelOffset: 3, precise: false);
    }

    /// <summary>BC5 (ATI2 / DXN): two precise-weighted channels → RG8 (pitch 8).</summary>
    public static void Bc5(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        AlphaBlock(block[..8], dst, pitch: 8, pixelStride: 2, channelOffset: 0, precise: true);
        AlphaBlock(block[8..16], dst, pitch: 8, pixelStride: 2, channelOffset: 1, precise: true);
    }

    private static void ColorBlock(ReadOnlySpan<byte> block, Span<byte> dst, int pitch, bool onlyOpaque)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);

        Span<byte> r = stackalloc byte[16]; // 4 colors × RGBA

        // Raw 5/6/5-bit components; bcdec interpolates on these, not on the
        // expanded 8-bit values.
        int r0 = (c0 >> 11) & 0x1F, g0 = (c0 >> 5) & 0x3F, b0 = c0 & 0x1F;
        int r1 = (c1 >> 11) & 0x1F, g1 = (c1 >> 5) & 0x3F, b1 = c1 & 0x1F;

        // 565 → 888 endpoint expansion (bcdec magic constants).
        r[0] = (byte)((r0 * 527 + 23) >> 6); r[1] = (byte)((g0 * 259 + 33) >> 6); r[2] = (byte)((b0 * 527 + 23) >> 6); r[3] = 255;
        r[4] = (byte)((r1 * 527 + 23) >> 6); r[5] = (byte)((g1 * 259 + 33) >> 6); r[6] = (byte)((b1 * 527 + 23) >> 6); r[7] = 255;

        if (c0 > c1 || onlyOpaque)
        {
            r[8] = (byte)(((2 * r0 + r1) * 351 + 61) >> 7);
            r[9] = (byte)(((2 * g0 + g1) * 2763 + 1039) >> 11);
            r[10] = (byte)(((2 * b0 + b1) * 351 + 61) >> 7);
            r[11] = 255;
            r[12] = (byte)(((r0 + 2 * r1) * 351 + 61) >> 7);
            r[13] = (byte)(((g0 + 2 * g1) * 2763 + 1039) >> 11);
            r[14] = (byte)(((b0 + 2 * b1) * 351 + 61) >> 7);
            r[15] = 255;
        }
        else
        {
            r[8] = (byte)(((r0 + r1) * 1053 + 125) >> 8);
            r[9] = (byte)(((g0 + g1) * 4145 + 1019) >> 11);
            r[10] = (byte)(((b0 + b1) * 1053 + 125) >> 8);
            r[11] = 255;
            r[12] = r[13] = r[14] = r[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int idx = (int)(indices & 0x3);
                indices >>= 2;
                int o = j * pitch + i * 4;
                int s = idx * 4;
                dst[o] = r[s];
                dst[o + 1] = r[s + 1];
                dst[o + 2] = r[s + 2];
                dst[o + 3] = r[s + 3];
            }
    }

    private static readonly int[] AWeights4 = [13107, 26215, 39321, 52429];
    private static readonly int[] AWeights6 = [9363, 18724, 28086, 37450, 46812, 56173];

    private static void AlphaBlock(ReadOnlySpan<byte> block, Span<byte> dst, int pitch, int pixelStride, int channelOffset, bool precise)
    {
        Span<byte> a = stackalloc byte[8];
        int a0 = block[0], a1 = block[1];
        a[0] = (byte)a0;
        a[1] = (byte)a1;
        if (a0 > a1)
        {
            for (int i = 0; i < 6; i++)
                a[2 + i] = precise
                    ? (byte)((AWeights6[5 - i] * a0 + AWeights6[i] * a1 + 32768) >> 16)
                    : (byte)(((6 - i) * a0 + (1 + i) * a1 + 1) / 7);
        }
        else
        {
            for (int i = 0; i < 4; i++)
                a[2 + i] = precise
                    ? (byte)((AWeights4[3 - i] * a0 + AWeights4[i] * a1 + 32768) >> 16)
                    : (byte)(((4 - i) * a0 + (1 + i) * a1 + 1) / 5);
            a[6] = 0;
            a[7] = 255;
        }

        ulong indices = (ulong)block[2]
            | ((ulong)block[3] << 8)
            | ((ulong)block[4] << 16)
            | ((ulong)block[5] << 24)
            | ((ulong)block[6] << 32)
            | ((ulong)block[7] << 40);

        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int bit = 3 * (j * 4 + i);
                int idx = (int)((indices >> bit) & 0x7);
                dst[j * pitch + i * pixelStride + channelOffset] = a[idx];
            }
    }
}
