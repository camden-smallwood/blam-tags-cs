using System.Buffers.Binary;
using System.IO.Compression;

namespace BlamTags;

/// <summary>
/// A bitmap tag's <b>color plate</b> — the artist's original source sheet,
/// RGBA8. Distinct from the per-image <c>processed pixel data</c> (the compiled
/// game texture): the color plate is lossless and re-imports directly. Present
/// on classic CE / Halo 2 tags; gen3+ MCC tags ship with the source stripped.
/// See <see cref="From"/>.
/// </summary>
public sealed class ColorPlate
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    /// <summary>Row-major RGBA8 (<c>[R, G, B, A]</c> per pixel), <c>Width*Height*4</c> bytes.</summary>
    public required byte[] Rgba { get; init; }

    /// <summary>Recover a bitmap tag's color plate, or null if it carries no
    /// source (<c>compressed color plate data</c> empty/absent — gen3+ MCC and
    /// tags re-saved without source).
    ///
    /// Format (per HABT / halo2-color-plate-extractor): <c>compressed color
    /// plate data</c> is a big-endian <c>u32</c> uncompressed-size prefix
    /// followed by a zlib stream that inflates to <c>Width*Height</c> ARGB8888
    /// pixels (little-endian, memory <c>[B,G,R,A]</c> → straight RGBA8). CE
    /// nests the fields under a <c>color plate</c> struct; H2 has them at root.</summary>
    public static ColorPlate? From(TagFile tag)
    {
        var root = tag.Root;
        var src = root.FieldPath("color plate")?.AsStruct() ?? root;

        byte[]? blob = src.FieldPath("compressed color plate data")?.AsData();
        if (blob is null)
            return null;
        uint width = (uint)System.Math.Max(src.ReadIntAny("color plate width") ?? 0, 0);
        uint height = (uint)System.Math.Max(src.ReadIntAny("color plate height") ?? 0, 0);
        if (blob.Length < 4 || width == 0 || height == 0)
            return null;

        long expected = (long)width * height * 4;
        if (expected > int.MaxValue)
            throw BitmapException.PixelSliceOutOfBounds(0, (ulong)expected, 0);

        // blob = [big-endian u32 uncompressed size][zlib stream].
        var outBuf = new MemoryStream((int)expected);
        using (var zlib = new ZLibStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress))
            zlib.CopyTo(outBuf);
        byte[] rgba = outBuf.ToArray();
        if (rgba.Length != expected)
            throw BitmapException.PixelSliceOutOfBounds(0, (ulong)expected, (ulong)rgba.Length);

        // Stored ARGB8888 (memory [B, G, R, A]) → straight RGBA8: swap B<->R.
        for (int i = 0; i + 4 <= rgba.Length; i += 4)
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

        return new ColorPlate { Width = width, Height = height, Rgba = rgba };
    }

    /// <summary>Write the color plate as a Tool-importable RGBA8 TIFF.</summary>
    public void WriteTiff(Stream outp) => BitmapTiff.WriteRgba8Tiff(outp, Width, Height, Rgba);

    /// <summary>Write the color plate as a single-mip A8R8G8B8 DDS.</summary>
    public void WriteDds(Stream outp)
    {
        // DDS A8R8G8B8 is BGRA in memory; convert from our RGBA8.
        var bgra = new byte[Rgba.Length];
        for (int i = 0; i + 4 <= Rgba.Length; i += 4)
        {
            bgra[i] = Rgba[i + 2];     // B
            bgra[i + 1] = Rgba[i + 1]; // G
            bgra[i + 2] = Rgba[i];     // R
            bgra[i + 3] = Rgba[i + 3]; // A
        }
        BitmapDds.WriteDds(outp, BitmapFormat.A8r8g8b8, Width, Height, 1, false, bgra);
    }
}
