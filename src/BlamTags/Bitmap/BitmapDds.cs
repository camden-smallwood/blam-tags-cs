using System.Buffers.Binary;

namespace BlamTags;

/// <summary>DDS writers (legacy + DXT10) for the bitmap → DDS path. Port of
/// the Rust <c>bitmap::dds</c>; output is byte-deterministic.</summary>
internal static class BitmapDds
{
    private const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8,
        DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
    private const uint DDPF_ALPHAPIXELS = 0x1, DDPF_ALPHA = 0x2, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40, DDPF_LUMINANCE = 0x20000;
    private const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;
    private const uint DDSCAPS2_CUBEMAP = 0x200, DDSCAPS2_CUBEMAP_ALL_FACES = 0xFC00;
    private const uint D3D10_RESOURCE_DIMENSION_TEXTURE2D = 3;

    private static readonly byte[] DdsMagic = "DDS "u8.ToArray();

    private static uint FourCc(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

    private readonly record struct PixelFormat(uint Flags, uint FourCc, uint RgbBitCount, uint R, uint G, uint B, uint A);

    private static PixelFormat PfFourCc(uint v) => new(DDPF_FOURCC, v, 0, 0, 0, 0, 0);

    private static PixelFormat GetPixelFormat(BitmapFormat f) => f switch
    {
        BitmapFormat.Dxt1 => PfFourCc(FourCc("DXT1")),
        BitmapFormat.Dxt3 => PfFourCc(FourCc("DXT3")),
        BitmapFormat.Dxt5 => PfFourCc(FourCc("DXT5")),
        BitmapFormat.Dxt5a => PfFourCc(FourCc("ATI1")),
        BitmapFormat.Dxn => PfFourCc(FourCc("ATI2")),
        BitmapFormat.Q8w8v8u8 => PfFourCc(63),
        BitmapFormat.Abgrfp16 => PfFourCc(113),
        BitmapFormat.Abgrfp32 => PfFourCc(116),
        BitmapFormat.A16b16g16r16 => PfFourCc(36),
        BitmapFormat.A8 => new(DDPF_ALPHA, 0, 8, 0, 0, 0, 0xFF),
        BitmapFormat.Y8 or BitmapFormat.R8 => new(DDPF_LUMINANCE, 0, 8, 0xFF, 0, 0, 0),
        BitmapFormat.Ay8 => new(DDPF_ALPHA, 0, 8, 0, 0, 0, 0xFF),
        BitmapFormat.A8y8 => new(DDPF_LUMINANCE | DDPF_ALPHAPIXELS, 0, 16, 0x00FF, 0, 0, 0xFF00),
        BitmapFormat.R5g6b5 => new(DDPF_RGB, 0, 16, 0xF800, 0x07E0, 0x001F, 0),
        BitmapFormat.A1r5g5b5 => new(DDPF_RGB | DDPF_ALPHAPIXELS, 0, 16, 0x7C00, 0x03E0, 0x001F, 0x8000),
        BitmapFormat.A4r4g4b4 or BitmapFormat.A4r4g4b4Font => new(DDPF_RGB | DDPF_ALPHAPIXELS, 0, 16, 0x0F00, 0x00F0, 0x000F, 0xF000),
        BitmapFormat.X8r8g8b8 => new(DDPF_RGB, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0),
        BitmapFormat.A8r8g8b8 => new(DDPF_RGB | DDPF_ALPHAPIXELS, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
        BitmapFormat.A2r10g10b10 => new(DDPF_RGB | DDPF_ALPHAPIXELS, 0, 32, 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000),
        BitmapFormat.V8u8 => new(DDPF_RGB, 0, 16, 0xFF00, 0x00FF, 0, 0),
        _ => throw new InvalidOperationException($"`{f}` has no legacy DDS pixelformat — caller should have decoded to A8R8G8B8 first"),
    };

    /// <summary>Whether the format must be CPU-decoded to RGBA8 before the DDS
    /// writer (Halo-specific BC variants, float-mono, etc.).</summary>
    public static bool NeedsDecodeForDds(BitmapFormat f) => f switch
    {
        BitmapFormat.A8 or BitmapFormat.Y8 or BitmapFormat.R8 or BitmapFormat.Ay8 or BitmapFormat.A8y8
            or BitmapFormat.R5g6b5 or BitmapFormat.A1r5g5b5 or BitmapFormat.A4r4g4b4 or BitmapFormat.A4r4g4b4Font
            or BitmapFormat.X8r8g8b8 or BitmapFormat.A8r8g8b8 or BitmapFormat.A2r10g10b10
            or BitmapFormat.V8u8 or BitmapFormat.Q8w8v8u8 or BitmapFormat.A16b16g16r16
            or BitmapFormat.Abgrfp16 or BitmapFormat.Abgrfp32
            or BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5 or BitmapFormat.Dxt5a or BitmapFormat.Dxn
            or BitmapFormat.Signedr16g16b16a16 => false,
        _ => true,
    };

    private static void U32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    /// <summary>Legacy DDS: 4-byte magic + 124-byte header + raw pixels.</summary>
    public static void WriteDds(Stream outp, BitmapFormat format, uint width, uint height, uint mipmapLevels, bool isCube, ReadOnlySpan<byte> pixels)
    {
        var pf = GetPixelFormat(format);
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;
        if (mipmapLevels > 1) flags |= DDSD_MIPMAPCOUNT;

        uint pitchOrLinear;
        if (format.IsCompressed()) { pitchOrLinear = (uint)format.LevelBytes(width, height); flags |= DDSD_LINEARSIZE; }
        else { pitchOrLinear = (width * pf.RgbBitCount + 7) / 8; flags |= DDSD_PITCH; }

        uint caps = DDSCAPS_TEXTURE;
        if (mipmapLevels > 1) caps |= DDSCAPS_MIPMAP | DDSCAPS_COMPLEX;
        if (isCube) caps |= DDSCAPS_COMPLEX;
        uint caps2 = isCube ? DDSCAPS2_CUBEMAP | DDSCAPS2_CUBEMAP_ALL_FACES : 0;

        outp.Write(DdsMagic);
        U32(outp, 124); U32(outp, flags); U32(outp, height); U32(outp, width);
        U32(outp, pitchOrLinear); U32(outp, 0); U32(outp, mipmapLevels);
        for (int i = 0; i < 11; i++) U32(outp, 0);
        U32(outp, 32); U32(outp, pf.Flags); U32(outp, pf.FourCc); U32(outp, pf.RgbBitCount);
        U32(outp, pf.R); U32(outp, pf.G); U32(outp, pf.B); U32(outp, pf.A);
        U32(outp, caps); U32(outp, caps2); U32(outp, 0); U32(outp, 0); U32(outp, 0);
        outp.Write(pixels);
    }

    private static uint DxgiFormat(BitmapFormat f) => f switch
    {
        BitmapFormat.A8 => 65,
        BitmapFormat.Y8 or BitmapFormat.R8 or BitmapFormat.Ay8 => 61,
        BitmapFormat.A8y8 => 49,
        BitmapFormat.A4r4g4b4 or BitmapFormat.A4r4g4b4Font => 115,
        BitmapFormat.X8r8g8b8 => 88,
        BitmapFormat.A8r8g8b8 => 87,
        BitmapFormat.Dxt1 => 71,
        BitmapFormat.Dxt3 => 74,
        BitmapFormat.Dxt5 => 77,
        BitmapFormat.Dxt5a => 80,
        BitmapFormat.Dxn => 83,
        BitmapFormat.V8u8 => 51,
        BitmapFormat.Q8w8v8u8 => 32,
        BitmapFormat.Abgrfp16 => 10,
        BitmapFormat.Abgrfp32 => 2,
        BitmapFormat.A16b16g16r16 => 11,
        BitmapFormat.Signedr16g16b16a16 => 13,
        _ => throw new InvalidOperationException($"`{f}` has no DXGI mapping — caller should have decoded to A8R8G8B8 first"),
    };

    /// <summary>DDS with the DXT10 extension (texture arrays + formats with no
    /// legacy expression).</summary>
    public static void WriteDdsDx10(Stream outp, BitmapFormat format, uint width, uint height, uint mipmapLevels, uint layerCount, ReadOnlySpan<byte> pixels)
    {
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;
        if (mipmapLevels > 1) flags |= DDSD_MIPMAPCOUNT;
        uint pitchOrLinear;
        if (format.IsCompressed()) { pitchOrLinear = (uint)format.LevelBytes(width, height); flags |= DDSD_LINEARSIZE; }
        else { uint bpp = format.BytesPerPixel() * 8; pitchOrLinear = (width * bpp + 7) / 8; flags |= DDSD_PITCH; }

        uint caps = DDSCAPS_TEXTURE;
        if (mipmapLevels > 1) caps |= DDSCAPS_MIPMAP | DDSCAPS_COMPLEX;
        if (layerCount > 1) caps |= DDSCAPS_COMPLEX;

        outp.Write(DdsMagic);
        U32(outp, 124); U32(outp, flags); U32(outp, height); U32(outp, width);
        U32(outp, pitchOrLinear); U32(outp, 0); U32(outp, mipmapLevels);
        for (int i = 0; i < 11; i++) U32(outp, 0);
        U32(outp, 32); U32(outp, DDPF_FOURCC); U32(outp, FourCc("DX10")); U32(outp, 0);
        U32(outp, 0); U32(outp, 0); U32(outp, 0); U32(outp, 0);
        U32(outp, caps); U32(outp, 0); U32(outp, 0); U32(outp, 0); U32(outp, 0);
        U32(outp, DxgiFormat(format)); U32(outp, D3D10_RESOURCE_DIMENSION_TEXTURE2D);
        U32(outp, 0); U32(outp, layerCount); U32(outp, 0);
        outp.Write(pixels);
    }
}
