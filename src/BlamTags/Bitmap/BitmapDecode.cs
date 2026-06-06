using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Decode raw <c>.bitmap</c> pixel bytes into RGBA8 (memory order
/// <c>[R, G, B, A]</c>). Port of the Rust <c>bitmap::decode</c>; channel
/// conventions match the engine pipeline.
/// </summary>
internal static class BitmapDecode
{
    private readonly record struct ChannelMask(byte R, byte G, byte B, byte A)
    {
        public static readonly ChannelMask All = new(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly ChannelMask RgbOnly = new(0xFF, 0xFF, 0xFF, 0);
        public static readonly ChannelMask AlphaOnly = new(0, 0, 0, 0xFF);
    }

    public static byte[] DecodeToRgba8(BitmapFormat format, uint width, uint height, ReadOnlySpan<byte> input)
    {
        int need = (int)format.LevelBytes(width, height);
        if (input.Length < need)
            throw BitmapException.PixelSliceOutOfBounds(0, (ulong)need, (ulong)input.Length);
        var src = input[..need];

        int pixels = (int)width * (int)height;
        var outBuf = new byte[pixels * 4];

        switch (format)
        {
            case BitmapFormat.A8: DecodeA8(src, outBuf); break;
            case BitmapFormat.Y8: DecodeY8(src, outBuf); break;
            case BitmapFormat.R8: DecodeR8(src, outBuf); break;
            case BitmapFormat.Ay8: DecodeAy8(src, outBuf); break;

            case BitmapFormat.A8y8: DecodeA8y8(src, outBuf); break;
            case BitmapFormat.R5g6b5: DecodeR5g6b5(src, outBuf); break;
            case BitmapFormat.A1r5g5b5: DecodeA1r5g5b5(src, outBuf); break;
            case BitmapFormat.A4r4g4b4: DecodeA4r4g4b4(src, outBuf); break;
            case BitmapFormat.A4r4g4b4Font: DecodeA4r4g4b4Font(src, outBuf); break;
            case BitmapFormat.G8b8: DecodeG8b8(src, outBuf); break;
            case BitmapFormat.V8u8: DecodeV8u8(src, outBuf); break;
            case BitmapFormat.L16: DecodeL16(src, outBuf); break;
            case BitmapFormat.F16Mono: DecodeF16Mono(src, outBuf); break;
            case BitmapFormat.F16Red: DecodeF16Red(src, outBuf); break;

            case BitmapFormat.X8r8g8b8: DecodeX8r8g8b8(src, outBuf); break;
            case BitmapFormat.A8r8g8b8: DecodeA8r8g8b8(src, outBuf); break;
            case BitmapFormat.Q8w8v8u8: DecodeQ8w8v8u8(src, outBuf); break;
            case BitmapFormat.A2r10g10b10: DecodeA2r10g10b10(src, outBuf); break;
            case BitmapFormat.V16u16: DecodeV16u16(src, outBuf); break;
            case BitmapFormat.R16g16: DecodeR16g16(src, outBuf); break;

            case BitmapFormat.A16b16g16r16: DecodeA16b16g16r16(src, outBuf); break;
            case BitmapFormat.Signedr16g16b16a16: DecodeSignedr16(src, outBuf); break;
            case BitmapFormat.Abgrfp16: DecodeAbgrfp16(src, outBuf); break;
            case BitmapFormat.Abgrfp32: DecodeAbgrfp32(src, outBuf); break;

            case BitmapFormat.Dxt1: DecodeBc1(src, width, height, outBuf); break;
            case BitmapFormat.Dxt3: DecodeBc2(src, width, height, outBuf); break;
            case BitmapFormat.Dxt5: DecodeBc3(src, width, height, outBuf); break;
            case BitmapFormat.Dxt5a: DecodeBc4Rgba(src, width, height, outBuf, ChannelMask.All); break;
            case BitmapFormat.Dxt5aMono: DecodeBc4Rgba(src, width, height, outBuf, ChannelMask.RgbOnly); break;
            case BitmapFormat.Dxt5aAlpha: DecodeBc4Rgba(src, width, height, outBuf, ChannelMask.AlphaOnly); break;
            case BitmapFormat.Dxn: DecodeBc5(src, width, height, outBuf); break;
            case BitmapFormat.Dxt3a: DecodeDxt3a(src, width, height, outBuf, ChannelMask.All); break;
            case BitmapFormat.Dxt3aMono: DecodeDxt3a(src, width, height, outBuf, ChannelMask.RgbOnly); break;
            case BitmapFormat.Dxt3aAlpha: DecodeDxt3a(src, width, height, outBuf, ChannelMask.AlphaOnly); break;
            case BitmapFormat.Dxt3a1111: DecodeDxt3a1111(src, width, height, outBuf); break;
            case BitmapFormat.Dxt5nm: DecodeDxt5nm(src, width, height, outBuf); break;
            case BitmapFormat.Ctx1: DecodeCtx1(src, width, height, outBuf); break;
            case BitmapFormat.Dxt5Red: DecodeBc3SingleChannel(src, width, height, outBuf, 0); break;
            case BitmapFormat.Dxt5Green: DecodeBc3SingleChannel(src, width, height, outBuf, 1); break;
            case BitmapFormat.Dxt5Blue: DecodeBc3SingleChannel(src, width, height, outBuf, 2); break;
            case BitmapFormat.DxnMonoAlpha: DecodeDxnMonoAlpha(src, width, height, outBuf); break;

            default:
                throw BitmapException.FormatNotSupported(format.ToString());
        }
        return outBuf;
    }

    // ---- single-channel ----
    private static void DecodeA8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length; k++) { int p = k * 4; o[p] = 255; o[p + 1] = 255; o[p + 2] = 255; o[p + 3] = i[k]; } }
    private static void DecodeY8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length; k++) { int p = k * 4; o[p] = i[k]; o[p + 1] = i[k]; o[p + 2] = i[k]; o[p + 3] = 255; } }
    private static void DecodeR8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length; k++) { int p = k * 4; o[p] = i[k]; o[p + 3] = 255; } }
    private static void DecodeAy8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length; k++) { int p = k * 4; o[p] = i[k]; o[p + 1] = i[k]; o[p + 2] = i[k]; o[p + 3] = i[k]; } }

    // ---- 16-bit packed ----
    private static void DecodeA8y8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 2; k++) { byte y = i[k * 2], a = i[k * 2 + 1]; int p = k * 4; o[p] = y; o[p + 1] = y; o[p + 2] = y; o[p + 3] = a; } }
    private static void DecodeA4r4g4b4(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 2; k++)
        {
            ushort v = (ushort)(i[k * 2] | (i[k * 2 + 1] << 8));
            byte a = (byte)((v >> 12) & 0xF), r = (byte)((v >> 8) & 0xF), g = (byte)((v >> 4) & 0xF), b = (byte)(v & 0xF);
            int p = k * 4; o[p] = (byte)(r * 0x11); o[p + 1] = (byte)(g * 0x11); o[p + 2] = (byte)(b * 0x11); o[p + 3] = (byte)(a * 0x11);
        }
    }
    private static void DecodeA4r4g4b4Font(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length; k++) { int p = k * 4; o[p] = i[k]; o[p + 1] = i[k]; o[p + 2] = i[k]; o[p + 3] = 255; } }
    private static void DecodeG8b8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 2; k++) { int p = k * 4; o[p] = 0; o[p + 1] = i[k * 2]; o[p + 2] = i[k * 2 + 1]; o[p + 3] = 255; } }
    private static void DecodeV8u8(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 2; k++)
        {
            sbyte u = (sbyte)i[k * 2], v = (sbyte)i[k * 2 + 1];
            int p = k * 4; o[p] = (byte)(v + 128); o[p + 1] = (byte)(u + 128); o[p + 2] = 128; o[p + 3] = 255;
        }
    }
    private static void DecodeL16(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 2; k++) { ushort v = (ushort)(i[k * 2] | (i[k * 2 + 1] << 8)); byte y = (byte)(v >> 8); int p = k * 4; o[p] = y; o[p + 1] = y; o[p + 2] = y; o[p + 3] = 255; } }
    private static void DecodeF16Mono(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 2; k++) { byte v = ClampToU8(HalfToF32((ushort)(i[k * 2] | (i[k * 2 + 1] << 8)))); int p = k * 4; o[p] = v; o[p + 1] = v; o[p + 2] = v; o[p + 3] = 255; } }
    private static void DecodeF16Red(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 2; k++) { int p = k * 4; o[p] = ClampToU8(HalfToF32((ushort)(i[k * 2] | (i[k * 2 + 1] << 8)))); o[p + 3] = 255; } }
    private static void DecodeR5g6b5(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 2; k++)
        {
            ushort v = (ushort)(i[k * 2] | (i[k * 2 + 1] << 8));
            int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
            int p = k * 4; o[p] = (byte)((r << 3) | (r >> 2)); o[p + 1] = (byte)((g << 2) | (g >> 4)); o[p + 2] = (byte)((b << 3) | (b >> 2)); o[p + 3] = 255;
        }
    }
    private static void DecodeA1r5g5b5(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 2; k++)
        {
            ushort v = (ushort)(i[k * 2] | (i[k * 2 + 1] << 8));
            int a = (v >> 15) & 0x1, r = (v >> 10) & 0x1F, g = (v >> 5) & 0x1F, b = v & 0x1F;
            int p = k * 4; o[p] = (byte)((r << 3) | (r >> 2)); o[p + 1] = (byte)((g << 3) | (g >> 2)); o[p + 2] = (byte)((b << 3) | (b >> 2)); o[p + 3] = (byte)(a * 0xFF);
        }
    }

    // ---- 32-bit packed ----
    private static void DecodeX8r8g8b8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 4; k++) { int p = k * 4, c = k * 4; o[p] = i[c + 2]; o[p + 1] = i[c + 1]; o[p + 2] = i[c]; o[p + 3] = 255; } }
    private static void DecodeA8r8g8b8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 4; k++) { int p = k * 4, c = k * 4; o[p] = i[c + 2]; o[p + 1] = i[c + 1]; o[p + 2] = i[c]; o[p + 3] = i[c + 3]; } }
    private static void DecodeQ8w8v8u8(ReadOnlySpan<byte> i, byte[] o) { for (int k = 0; k < i.Length / 4; k++) { int p = k * 4, c = k * 4; for (int ch = 0; ch < 4; ch++) o[p + ch] = (byte)((sbyte)i[c + ch] + 128); } }
    private static void DecodeA2r10g10b10(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 4; k++)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(i.Slice(k * 4, 4));
            int a = (int)((v >> 30) & 0x3); uint r = (v >> 20) & 0x3FF, g = (v >> 10) & 0x3FF, b = v & 0x3FF;
            int p = k * 4; o[p] = (byte)(r >> 2); o[p + 1] = (byte)(g >> 2); o[p + 2] = (byte)(b >> 2); o[p + 3] = (byte)System.Math.Min(a * 85, 255);
        }
    }
    private static void DecodeV16u16(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 4; k++)
        {
            int u = BinaryPrimitives.ReadInt16LittleEndian(i.Slice(k * 4, 2)) + 32768;
            int v = BinaryPrimitives.ReadInt16LittleEndian(i.Slice(k * 4 + 2, 2)) + 32768;
            int p = k * 4; o[p] = (byte)(v >> 8); o[p + 1] = (byte)(u >> 8); o[p + 2] = 128; o[p + 3] = 255;
        }
    }
    private static void DecodeR16g16(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 4; k++)
        {
            ushort r = BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(k * 4, 2));
            ushort g = BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(k * 4 + 2, 2));
            int p = k * 4; o[p] = (byte)(r >> 8); o[p + 1] = (byte)(g >> 8); o[p + 2] = 0; o[p + 3] = 255;
        }
    }

    // ---- multi-cell ----
    private static void DecodeA16b16g16r16(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 8; k++)
        {
            int c = k * 8, p = k * 4;
            o[p] = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c, 2)) >> 8);
            o[p + 1] = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 2, 2)) >> 8);
            o[p + 2] = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 4, 2)) >> 8);
            o[p + 3] = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 6, 2)) >> 8);
        }
    }
    private static void DecodeSignedr16(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 8; k++)
        {
            int c = k * 8, p = k * 4;
            o[p] = (byte)((BinaryPrimitives.ReadInt16LittleEndian(i.Slice(c, 2)) + 32768) >> 8);
            o[p + 1] = (byte)((BinaryPrimitives.ReadInt16LittleEndian(i.Slice(c + 2, 2)) + 32768) >> 8);
            o[p + 2] = (byte)((BinaryPrimitives.ReadInt16LittleEndian(i.Slice(c + 4, 2)) + 32768) >> 8);
            o[p + 3] = (byte)((BinaryPrimitives.ReadInt16LittleEndian(i.Slice(c + 6, 2)) + 32768) >> 8);
        }
    }
    private static void DecodeAbgrfp16(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 8; k++)
        {
            int c = k * 8, p = k * 4;
            o[p] = ClampToU8(HalfToF32(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c, 2))));
            o[p + 1] = ClampToU8(HalfToF32(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 2, 2))));
            o[p + 2] = ClampToU8(HalfToF32(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 4, 2))));
            o[p + 3] = ClampToU8(HalfToF32(BinaryPrimitives.ReadUInt16LittleEndian(i.Slice(c + 6, 2))));
        }
    }
    private static void DecodeAbgrfp32(ReadOnlySpan<byte> i, byte[] o)
    {
        for (int k = 0; k < i.Length / 16; k++)
        {
            int c = k * 16, p = k * 4;
            o[p] = ClampToU8(BinaryPrimitives.ReadSingleLittleEndian(i.Slice(c, 4)));
            o[p + 1] = ClampToU8(BinaryPrimitives.ReadSingleLittleEndian(i.Slice(c + 4, 4)));
            o[p + 2] = ClampToU8(BinaryPrimitives.ReadSingleLittleEndian(i.Slice(c + 8, 4)));
            o[p + 3] = ClampToU8(BinaryPrimitives.ReadSingleLittleEndian(i.Slice(c + 12, 4)));
        }
    }

    private static byte ClampToU8(float v)
    {
        float c = float.IsNaN(v) ? 0f : System.Math.Clamp(v, 0f, 1f);
        return (byte)(c * 255f + 0.5f);
    }

    private static float HalfToF32(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);

    // ---- block-compressed ----
    private static void BlitRgbaBlock(ReadOnlySpan<byte> staging, byte[] o, uint width, uint height, uint bx, uint by)
    {
        int w = (int)width;
        for (uint j = 0; j < 4; j++)
        {
            uint py = by * 4 + j;
            if (py >= height) break;
            for (uint i = 0; i < 4; i++)
            {
                uint px = bx * 4 + i;
                if (px >= width) break;
                int dst = ((int)py * w + (int)px) * 4;
                int src = (int)(j * 4 + i) * 4;
                o[dst] = staging[src]; o[dst + 1] = staging[src + 1]; o[dst + 2] = staging[src + 2]; o[dst + 3] = staging[src + 3];
            }
        }
    }

    private static void ForEachBlock(uint width, uint height, int blockBytes, ReadOnlySpan<byte> input, BlockAction action)
    {
        uint bw = System.Math.Max((width + 3) / 4, 1), bh = System.Math.Max((height + 3) / 4, 1);
        for (uint by = 0; by < bh; by++)
            for (uint bx = 0; bx < bw; bx++)
            {
                int idx = (int)(by * bw + bx);
                action(input.Slice(idx * blockBytes, blockBytes), bx, by);
            }
    }

    private delegate void BlockAction(ReadOnlySpan<byte> block, uint bx, uint by);

    private static void DecodeBc1(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    { var inp = input.ToArray(); ForEachBlock(w, h, 8, inp, (block, bx, by) => { Span<byte> s = stackalloc byte[64]; BcDec.Bc1(block, s); BlitRgbaBlock(s, o, w, h, bx, by); }); }
    private static void DecodeBc2(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    { var inp = input.ToArray(); ForEachBlock(w, h, 16, inp, (block, bx, by) => { Span<byte> s = stackalloc byte[64]; BcDec.Bc2(block, s); BlitRgbaBlock(s, o, w, h, bx, by); }); }
    private static void DecodeBc3(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    { var inp = input.ToArray(); ForEachBlock(w, h, 16, inp, (block, bx, by) => { Span<byte> s = stackalloc byte[64]; BcDec.Bc3(block, s); BlitRgbaBlock(s, o, w, h, bx, by); }); }

    private static void DecodeBc5(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 16, inp, (block, bx, by) =>
        {
            Span<byte> s = stackalloc byte[32]; BcDec.Bc5(block, s);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) break; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) break; int src = (int)(j * 4 + i) * 2; int dst = ((int)py * wi + (int)px) * 4; o[dst] = s[src]; o[dst + 1] = s[src + 1]; o[dst + 2] = 128; o[dst + 3] = 255; } }
        });
    }

    private static ulong UnpackBc4AlphaBlock(ReadOnlySpan<byte> block, Span<byte> values)
    {
        int v0 = block[0], v1 = block[1];
        values[0] = (byte)v0; values[1] = (byte)v1;
        if (v0 > v1)
            for (int i = 0; i < 6; i++) values[2 + i] = (byte)(((6 - i) * v0 + (1 + i) * v1) / 7);
        else
        {
            for (int i = 0; i < 4; i++) values[2 + i] = (byte)(((4 - i) * v0 + (1 + i) * v1) / 5);
            values[6] = 0; values[7] = 255;
        }
        return (ulong)block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16)
            | ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);
    }

    private static void DecodeBc4Rgba(ReadOnlySpan<byte> input, uint w, uint h, byte[] o, ChannelMask mask)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 8, inp, (block, bx, by) =>
        {
            Span<byte> values = stackalloc byte[8]; ulong indices = UnpackBc4AlphaBlock(block, values);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int bit = (int)(3 * (j * 4 + i)); byte v = values[(int)((indices >> bit) & 7)]; int dst = ((int)py * wi + (int)px) * 4; o[dst] = (byte)(v & mask.R); o[dst + 1] = (byte)(v & mask.G); o[dst + 2] = (byte)(v & mask.B); o[dst + 3] = (byte)(v & mask.A); } }
        });
    }

    private static void DecodeDxt3a(ReadOnlySpan<byte> input, uint w, uint h, byte[] o, ChannelMask mask)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 8, inp, (block, bx, by) =>
        {
            ulong data = BinaryPrimitives.ReadUInt64LittleEndian(block);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int shift = (int)(4 * (4 * j + i)); byte value = (byte)(((data >> shift) & 0xF) * 17); int dst = ((int)py * wi + (int)px) * 4; o[dst] = (byte)(value & mask.R); o[dst + 1] = (byte)(value & mask.G); o[dst + 2] = (byte)(value & mask.B); o[dst + 3] = (byte)(value & mask.A); } }
        });
    }

    private static void DecodeDxt3a1111(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 8, inp, (block, bx, by) =>
        {
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(block);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int shift = (int)(4 * (4 * j + i)); byte n = (byte)((bits >> shift) & 0xF); int dst = ((int)py * wi + (int)px) * 4; o[dst] = (byte)((n & 1) * 255); o[dst + 1] = (byte)(((n >> 1) & 1) * 255); o[dst + 2] = (byte)(((n >> 2) & 1) * 255); o[dst + 3] = (byte)(((n >> 3) & 1) * 255); } }
        });
    }

    private static void DecodeDxt5nm(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 16, inp, (block, bx, by) =>
        {
            Span<byte> av = stackalloc byte[8]; ulong ai = UnpackBc4AlphaBlock(block[..8], av);
            Span<byte> staging = stackalloc byte[64]; BcDec.Bc1(block[8..16], staging);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int bit = (int)(3 * (j * 4 + i)); byte r = av[(int)((ai >> bit) & 7)]; byte g = staging[(int)(j * 4 + i) * 4 + 1]; byte z = CalculateNormalZ(r, g); int dst = ((int)py * wi + (int)px) * 4; o[dst] = r; o[dst + 1] = g; o[dst + 2] = z; o[dst + 3] = 255; } }
        });
    }

    private static void DecodeCtx1(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 8, inp, (block, bx, by) =>
        {
            Span<byte> e0 = stackalloc byte[2] { block[1], block[0] };
            Span<byte> e1 = stackalloc byte[2] { block[3], block[2] };
            Span<byte> e2 = stackalloc byte[2] { (byte)((2 * e0[0] + e1[0]) / 3), (byte)((2 * e0[1] + e1[1]) / 3) };
            Span<byte> e3 = stackalloc byte[2] { (byte)((e0[0] + 2 * e1[0]) / 3), (byte)((e0[1] + 2 * e1[1]) / 3) };
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int shift = (int)(2 * (4 * j + i)); int idx = (int)((indices >> shift) & 0x3); var ep = idx switch { 0 => e0, 1 => e1, 2 => e2, _ => e3 }; byte r = ep[0], g = ep[1]; int dst = ((int)py * wi + (int)px) * 4; o[dst] = r; o[dst + 1] = g; o[dst + 2] = CalculateNormalZ(r, g); o[dst + 3] = 255; } }
        });
    }

    private static void DecodeBc3SingleChannel(ReadOnlySpan<byte> input, uint w, uint h, byte[] o, int target)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 16, inp, (block, bx, by) =>
        {
            Span<byte> values = stackalloc byte[8]; ulong indices = UnpackBc4AlphaBlock(block[..8], values);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int bit = (int)(3 * (j * 4 + i)); byte v = values[(int)((indices >> bit) & 7)]; int dst = ((int)py * wi + (int)px) * 4; o[dst] = 0; o[dst + 1] = 0; o[dst + 2] = 0; o[dst + target] = v; o[dst + 3] = 255; } }
        });
    }

    private static void DecodeDxnMonoAlpha(ReadOnlySpan<byte> input, uint w, uint h, byte[] o)
    {
        var inp = input.ToArray(); int wi = (int)w;
        ForEachBlock(w, h, 16, inp, (block, bx, by) =>
        {
            Span<byte> rv = stackalloc byte[8]; ulong ri = UnpackBc4AlphaBlock(block[..8], rv);
            Span<byte> gv = stackalloc byte[8]; ulong gi = UnpackBc4AlphaBlock(block[8..16], gv);
            for (uint j = 0; j < 4; j++) { uint py = by * 4 + j; if (py >= h) continue; for (uint i = 0; i < 4; i++) { uint px = bx * 4 + i; if (px >= w) continue; int bit = (int)(3 * (j * 4 + i)); byte r = rv[(int)((ri >> bit) & 7)]; byte g = gv[(int)((gi >> bit) & 7)]; int dst = ((int)py * wi + (int)px) * 4; o[dst] = r; o[dst + 1] = r; o[dst + 2] = r; o[dst + 3] = g; } }
        });
    }

    private static byte CalculateNormalZ(byte r, byte g)
    {
        float x = r / 127.5f - 1.0f, y = g / 127.5f - 1.0f;
        float z = (float)System.Math.Sqrt(System.Math.Clamp(1.0f - x * x - y * y, 0.0f, 1.0f));
        return (byte)((z + 1.0f) * 127.5f + 0.5f);
    }
}
