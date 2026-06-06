namespace BlamTags;

/// <summary>The category of a <see cref="BitmapException"/>.</summary>
public enum BitmapErrorKind
{
    NotABitmapTag,
    FormatNotSupported,
    PixelSliceOutOfBounds,
    UnsupportedTextureType,
    TiffLayoutDeferred,
    Tiff,
    Io,
}

/// <summary>Errors from the bitmap walkers and TIFF/DDS writers.</summary>
public sealed class BitmapException(BitmapErrorKind kind, string message) : Exception(message)
{
    public BitmapErrorKind Kind { get; } = kind;

    public static BitmapException NotABitmapTag() =>
        new(BitmapErrorKind.NotABitmapTag, "tag is not a recognizable bitmap (missing `bitmaps` or `processed pixel data`)");

    public static BitmapException FormatNotSupported(string name) =>
        new(BitmapErrorKind.FormatNotSupported, $"bitmap format `{name}` is not directly DDS-mappable (needs decoder)");

    public static BitmapException PixelSliceOutOfBounds(ulong offset, ulong size, ulong available) =>
        new(BitmapErrorKind.PixelSliceOutOfBounds, $"pixels slice [{offset}..{offset + size}] exceeds processed pixel data ({available} bytes)");

    public static BitmapException UnsupportedTextureType(string name) =>
        new(BitmapErrorKind.UnsupportedTextureType, $"bitmap type `{name}` is not supported (only `2D texture`, `cube map`, and `array`)");

    public static BitmapException TiffLayoutDeferred(string kind) =>
        new(BitmapErrorKind.TiffLayoutDeferred, $"TIFF emission for bitmap type `{kind}` is deferred (cube cross / array strip layouts)");
}
