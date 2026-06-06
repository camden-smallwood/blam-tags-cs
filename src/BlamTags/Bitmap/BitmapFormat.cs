namespace BlamTags;

/// <summary>
/// A bitmap pixel format. Variants correspond 1:1 to the schema's
/// <c>bitmap_formats</c> enum members (keyed by name via
/// <see cref="FromSchemaName"/>, which is stable across games even though
/// on-disk indices differ).
/// </summary>
public enum BitmapFormat
{
    A8, Y8, Ay8, A8y8, R8,
    Unused2,
    R5g6b5,
    Unused3,
    A1r5g5b5, A4r4g4b4,
    X8r8g8b8, A8r8g8b8,
    Unused4,
    Dxt5nm, Dxt1, Dxt3, Dxt5,
    A4r4g4b4Font,
    Unused7, Unused8,
    SoftwareRgbfp32,
    Unused9,
    V8u8, G8b8, Abgrfp32, Abgrfp16, F16Mono, F16Red, Q8w8v8u8, A2r10g10b10,
    A16b16g16r16, V16u16, L16, R16g16, Signedr16g16b16a16,
    Dxt3a, Dxt5a, Dxt3a1111, Dxn, Ctx1,
    Dxt3aAlpha, Dxt3aMono, Dxt5aAlpha, Dxt5aMono,
    DxnMonoAlpha,
    Dxt5Red, Dxt5Green, Dxt5Blue,
    Depth24,
}

/// <summary>Per-image gamma curve (<c>e_bitmap_curve</c>); read by index.</summary>
public enum BitmapCurve
{
    Unknown = 0,
    XrgbGamma2 = 1,
    Gamma2 = 2,
    Linear = 3,
    OffsetLog = 4,
    Srgb = 5,
}

public static class BitmapFormatExtensions
{
    /// <summary>Resolve the schema's <c>bitmap_formats</c> option name, or null.</summary>
    public static BitmapFormat? FromSchemaName(string name) => name switch
    {
        "a8" => BitmapFormat.A8,
        "y8" => BitmapFormat.Y8,
        "ay8" => BitmapFormat.Ay8,
        "a8y8" => BitmapFormat.A8y8,
        "r8" => BitmapFormat.R8,
        "unused2" => BitmapFormat.Unused2,
        "r5g6b5" => BitmapFormat.R5g6b5,
        "unused3" => BitmapFormat.Unused3,
        "a1r5g5b5" => BitmapFormat.A1r5g5b5,
        "a4r4g4b4" => BitmapFormat.A4r4g4b4,
        "x8r8g8b8" => BitmapFormat.X8r8g8b8,
        "a8r8g8b8" => BitmapFormat.A8r8g8b8,
        "unused4" => BitmapFormat.Unused4,
        "dxt5_bias_alpha" or "dxt5nm" => BitmapFormat.Dxt5nm,
        "dxt1" => BitmapFormat.Dxt1,
        "dxt3" => BitmapFormat.Dxt3,
        "dxt5" => BitmapFormat.Dxt5,
        "a4r4g4b4 font" => BitmapFormat.A4r4g4b4Font,
        "unused7" => BitmapFormat.Unused7,
        "unused8" => BitmapFormat.Unused8,
        "software rgbfp32" => BitmapFormat.SoftwareRgbfp32,
        "unused9" => BitmapFormat.Unused9,
        "v8u8" => BitmapFormat.V8u8,
        "g8b8" => BitmapFormat.G8b8,
        "abgrfp32" => BitmapFormat.Abgrfp32,
        "abgrfp16" => BitmapFormat.Abgrfp16,
        "16f_mono" => BitmapFormat.F16Mono,
        "16f_red" => BitmapFormat.F16Red,
        "q8w8v8u8" => BitmapFormat.Q8w8v8u8,
        "a2r10g10b10" => BitmapFormat.A2r10g10b10,
        "a16b16g16r16" => BitmapFormat.A16b16g16r16,
        "v16u16" => BitmapFormat.V16u16,
        "l16" => BitmapFormat.L16,
        "r16g16" => BitmapFormat.R16g16,
        "signedr16g16b16a16" => BitmapFormat.Signedr16g16b16a16,
        "dxt3a" => BitmapFormat.Dxt3a,
        "dxt5a" => BitmapFormat.Dxt5a,
        "dxt3a_1111" => BitmapFormat.Dxt3a1111,
        "dxn" => BitmapFormat.Dxn,
        "ctx1" => BitmapFormat.Ctx1,
        "dxt3a_alpha" => BitmapFormat.Dxt3aAlpha,
        "dxt3a_mono" => BitmapFormat.Dxt3aMono,
        "dxt5a_alpha" => BitmapFormat.Dxt5aAlpha,
        "dxt5a_mono" => BitmapFormat.Dxt5aMono,
        "dxn_mono_alpha" => BitmapFormat.DxnMonoAlpha,
        "dxt5_red" => BitmapFormat.Dxt5Red,
        "dxt5_green" => BitmapFormat.Dxt5Green,
        "dxt5_blue" => BitmapFormat.Dxt5Blue,
        "depth 24" or "depth24" => BitmapFormat.Depth24,
        _ => null,
    };

    public static bool IsCompressed(this BitmapFormat f) => f switch
    {
        BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5 or BitmapFormat.Dxt5nm
            or BitmapFormat.Dxt5a or BitmapFormat.Dxn or BitmapFormat.DxnMonoAlpha
            or BitmapFormat.Dxt3a or BitmapFormat.Dxt3a1111 or BitmapFormat.Dxt3aAlpha
            or BitmapFormat.Dxt3aMono or BitmapFormat.Dxt5aAlpha or BitmapFormat.Dxt5aMono
            or BitmapFormat.Ctx1 or BitmapFormat.Dxt5Red or BitmapFormat.Dxt5Green
            or BitmapFormat.Dxt5Blue => true,
        _ => false,
    };

    public static bool RequiresDxt10(this BitmapFormat f) => f == BitmapFormat.Signedr16g16b16a16;

    public static bool IsSigned(this BitmapFormat f) => f switch
    {
        BitmapFormat.V8u8 or BitmapFormat.Q8w8v8u8 or BitmapFormat.V16u16
            or BitmapFormat.Signedr16g16b16a16 => true,
        _ => false,
    };

    public static bool IsHdr(this BitmapFormat f) => f switch
    {
        BitmapFormat.Abgrfp16 or BitmapFormat.Abgrfp32 or BitmapFormat.F16Mono
            or BitmapFormat.F16Red or BitmapFormat.SoftwareRgbfp32 => true,
        _ => false,
    };

    /// <summary>Bytes per stored block (8 for BC1/BC4-shaped, 16 for
    /// BC2/BC3/BC5-shaped, 0 for uncompressed).</summary>
    public static uint BlockBytes(this BitmapFormat f) => f switch
    {
        BitmapFormat.Dxt1 or BitmapFormat.Dxt5a or BitmapFormat.Dxt3a or BitmapFormat.Dxt3a1111
            or BitmapFormat.Dxt3aAlpha or BitmapFormat.Dxt3aMono or BitmapFormat.Dxt5aAlpha
            or BitmapFormat.Dxt5aMono or BitmapFormat.Ctx1 => 8,
        BitmapFormat.Dxt3 or BitmapFormat.Dxt5 or BitmapFormat.Dxt5nm or BitmapFormat.Dxn
            or BitmapFormat.DxnMonoAlpha or BitmapFormat.Dxt5Red or BitmapFormat.Dxt5Green
            or BitmapFormat.Dxt5Blue => 16,
        _ => 0,
    };

    /// <summary>(block_width, block_height, bytes_per_block), or null for
    /// unsupported formats.</summary>
    public static (uint W, uint H, uint Bytes)? BlockDimsAndSize(this BitmapFormat f)
    {
        if (f.IsCompressed()) return (4, 4, f.BlockBytes());
        uint bpp = f.BytesPerPixel();
        return bpp > 0 ? (1, 1, bpp) : null;
    }

    public static uint BytesPerPixel(this BitmapFormat f) => f switch
    {
        BitmapFormat.A8 or BitmapFormat.Y8 or BitmapFormat.Ay8 or BitmapFormat.R8 => 1,
        BitmapFormat.A8y8 or BitmapFormat.R5g6b5 or BitmapFormat.A1r5g5b5 or BitmapFormat.A4r4g4b4
            or BitmapFormat.A4r4g4b4Font or BitmapFormat.V8u8 or BitmapFormat.G8b8
            or BitmapFormat.F16Mono or BitmapFormat.F16Red or BitmapFormat.L16 => 2,
        BitmapFormat.X8r8g8b8 or BitmapFormat.A8r8g8b8 or BitmapFormat.Q8w8v8u8
            or BitmapFormat.A2r10g10b10 or BitmapFormat.V16u16 or BitmapFormat.R16g16
            or BitmapFormat.Depth24 => 4,
        BitmapFormat.A16b16g16r16 or BitmapFormat.Signedr16g16b16a16 or BitmapFormat.Abgrfp16 => 8,
        BitmapFormat.SoftwareRgbfp32 => 12,
        BitmapFormat.Abgrfp32 => 16,
        _ => 0,
    };

    /// <summary>Bytes consumed by one mip level at (width, height).</summary>
    public static ulong LevelBytes(this BitmapFormat f, uint width, uint height)
    {
        if (f.IsCompressed())
        {
            ulong blocksW = System.Math.Max((width + 3) / 4, 1);
            ulong blocksH = System.Math.Max((height + 3) / 4, 1);
            return blocksW * blocksH * f.BlockBytes();
        }
        return (ulong)width * height * f.BytesPerPixel();
    }

    /// <summary>Total bytes for one full mipmap chain.</summary>
    public static ulong SurfaceBytes(this BitmapFormat f, uint width, uint height, uint mipmapLevels)
    {
        ulong sum = 0;
        for (int i = 0; i < mipmapLevels; i++)
            sum += f.LevelBytes(System.Math.Max(width >> i, 1), System.Math.Max(height >> i, 1));
        return sum;
    }

    public static bool IsUnsupportedStub(this BitmapFormat f) => f switch
    {
        BitmapFormat.Unused2 or BitmapFormat.Unused3 or BitmapFormat.Unused4 or BitmapFormat.Unused7
            or BitmapFormat.Unused8 or BitmapFormat.Unused9 or BitmapFormat.SoftwareRgbfp32
            or BitmapFormat.Depth24 => true,
        _ => false,
    };

    public static BitmapCurve CurveFromIndex(byte index) => index switch
    {
        1 => BitmapCurve.XrgbGamma2,
        2 => BitmapCurve.Gamma2,
        3 => BitmapCurve.Linear,
        4 => BitmapCurve.OffsetLog,
        5 => BitmapCurve.Srgb,
        _ => BitmapCurve.Unknown,
    };
}
