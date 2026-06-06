using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// Unit tests for the RGBA8 decoders, ported 1:1 from the Rust
/// <c>bitmap::decode</c> tests. Locks in the uncompressed + bias paths
/// (channel order, nibble replication, signed bias). BC-format parity is
/// validated end-to-end against the oracle's corpus output.
/// </summary>
public sealed class BitmapDecodeTests
{
    private static byte[] Decode(BitmapFormat f, uint w, uint h, byte[] input) =>
        BitmapDecode.DecodeToRgba8(f, w, h, input);

    private static void AssertRgba(ReadOnlySpan<byte> got, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(r, got[0]); Assert.Equal(g, got[1]); Assert.Equal(b, got[2]); Assert.Equal(a, got[3]);
    }

    [Fact] public void A8_WhiteWithAlpha()
    {
        var o = Decode(BitmapFormat.A8, 4, 1, [0x00, 0x80, 0xFF, 0x40]);
        AssertRgba(o.AsSpan(0), 255, 255, 255, 0x00);
        AssertRgba(o.AsSpan(4), 255, 255, 255, 0x80);
        AssertRgba(o.AsSpan(8), 255, 255, 255, 0xFF);
        AssertRgba(o.AsSpan(12), 255, 255, 255, 0x40);
    }

    [Fact] public void Y8_ReplicatesToRgb()
    {
        var o = Decode(BitmapFormat.Y8, 2, 1, [0x00, 0x80]);
        AssertRgba(o.AsSpan(0), 0, 0, 0, 255);
        AssertRgba(o.AsSpan(4), 0x80, 0x80, 0x80, 255);
    }

    [Fact] public void R8_RedOnly() => AssertRgba(Decode(BitmapFormat.R8, 1, 1, [0xCC]), 0xCC, 0, 0, 255);

    [Fact] public void Ay8_ReplicatesToAllFour() => AssertRgba(Decode(BitmapFormat.Ay8, 1, 1, [0x40]), 0x40, 0x40, 0x40, 0x40);

    [Fact] public void A8y8_YInRgbAInAlpha() => AssertRgba(Decode(BitmapFormat.A8y8, 1, 1, [0x80, 0x40]), 0x80, 0x80, 0x80, 0x40);

    [Fact] public void A4r4g4b4_NibbleReplication() => AssertRgba(Decode(BitmapFormat.A4r4g4b4, 1, 1, [0xDC, 0xFE]), 0xEE, 0xDD, 0xCC, 0xFF);

    [Fact] public void X8r8g8b8_AlphaForced255() => AssertRgba(Decode(BitmapFormat.X8r8g8b8, 1, 1, [0x10, 0x20, 0x30, 0xAA]), 0x30, 0x20, 0x10, 0xFF);

    [Fact] public void A8r8g8b8_BgraToRgba() => AssertRgba(Decode(BitmapFormat.A8r8g8b8, 1, 1, [0x10, 0x20, 0x30, 0x40]), 0x30, 0x20, 0x10, 0x40);

    [Fact] public void V8u8_SignedBias()
    {
        AssertRgba(Decode(BitmapFormat.V8u8, 1, 1, [0xFF, 0x00]), 128, 127, 128, 255);
        AssertRgba(Decode(BitmapFormat.V8u8, 1, 1, [0x7F, 0x80]), 0, 255, 128, 255);
    }

    [Fact] public void R5g6b5_BitReplication()
    {
        // u16 LE 0xF800 = R=0x1F G=0 B=0 → R=255
        AssertRgba(Decode(BitmapFormat.R5g6b5, 1, 1, [0x00, 0xF8]), 255, 0, 0, 255);
    }
}
