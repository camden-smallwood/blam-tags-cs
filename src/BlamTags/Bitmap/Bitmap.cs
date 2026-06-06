namespace BlamTags;

/// <summary>
/// High-level view of a <c>.bitmap</c> tag — the <c>bitmaps[]</c> block plus
/// each image's resolved pixel bytes (sliced from the shared
/// <c>processed pixel data</c> blob). X360 per-image cache resources are
/// monolithic-cache backlog; this covers the PC/MCC inline case.
/// </summary>
public sealed class Bitmap
{
    private readonly TagBlock _bitmaps;
    private readonly List<byte[]> _perImagePixels;

    private Bitmap(TagBlock bitmaps, List<byte[]> perImagePixels)
    {
        _bitmaps = bitmaps;
        _perImagePixels = perImagePixels;
    }

    /// <summary>Wrap a parsed <c>.bitmap</c> tag.</summary>
    public static Bitmap New(TagFile tag)
    {
        var root = tag.Root;
        var pcBitmaps = root.FieldPath("bitmaps")?.AsBlock();
        var xenonBitmaps = root.FieldPath("xenon bitmaps")?.AsBlock();
        byte[] sharedPc = root.FieldPath("processed pixel data")?.AsData() ?? [];
        byte[] sharedXenon = root.FieldPath("xenon processed pixel data")?.AsData() ?? [];

        bool useX360 = sharedPc.Length == 0 && xenonBitmaps is not null;
        TagBlock bitmaps;
        byte[] shared;
        if (useX360)
            throw BitmapException.FormatNotSupported("xenon/X360 bitmap (monolithic-cache hydration is backlog)");
        else if (pcBitmaps is not null) { bitmaps = pcBitmaps; shared = sharedPc; }
        else if (xenonBitmaps is not null) { bitmaps = xenonBitmaps; shared = sharedXenon; }
        else throw BitmapException.NotABitmapTag();

        var perImage = new List<byte[]>(bitmaps.Count);
        foreach (var elem in bitmaps.Elements())
        {
            long offset = System.Math.Max(elem.ReadIntAny("pixels offset") ?? 0, 0);
            perImage.Add(shared.Length != 0 && offset <= shared.Length ? shared[(int)offset..] : []);
        }
        if (perImage.All(p => p.Length == 0))
            throw BitmapException.NotABitmapTag();

        return new Bitmap(bitmaps, perImage);
    }

    public int Count => _bitmaps.Count;
    public bool IsEmpty => Count == 0;

    public BitmapImage? Image(int index)
    {
        var elem = _bitmaps.Element(index);
        if (elem is null || index >= _perImagePixels.Count) return null;
        return new BitmapImage(elem, _perImagePixels[index]);
    }

    public IEnumerable<BitmapImage> Images()
    {
        for (int i = 0; i < Count; i++)
            yield return Image(i)!;
    }
}

/// <summary>One element of <c>bitmaps[]</c> — metadata plus this image's pixel
/// bytes (already resolved to start at the image's first pixel byte).</summary>
public sealed class BitmapImage
{
    private readonly TagStruct _elem;
    private readonly byte[] _pixels;

    internal BitmapImage(TagStruct elem, byte[] pixels)
    {
        _elem = elem;
        _pixels = pixels;
    }

    public uint Width => (uint)System.Math.Max(_elem.ReadIntAny("width") ?? 0, 0);
    public uint Height => (uint)System.Math.Max(_elem.ReadIntAny("height") ?? 0, 0);
    public uint Depth => (uint)System.Math.Max(_elem.ReadIntAny("depth") ?? 1, 1);
    public uint MipmapLevels => (uint)System.Math.Max(_elem.ReadIntAny("mipmap count") ?? 0, 0) + 1;
    public string? FormatName => _elem.ReadEnumName("format");
    public string? TypeName => _elem.ReadEnumName("type");
    public BitmapCurve Curve => BitmapFormatExtensions.CurveFromIndex((byte)System.Math.Max(_elem.ReadIntAny("curve") ?? 0, 0));
    public bool IsCube => TypeName == "cube map";
    public bool IsArray => TypeName == "array";

    public BitmapFormat Format
    {
        get
        {
            string name = FormatName ?? throw BitmapException.NotABitmapTag();
            return BitmapFormatExtensions.FromSchemaName(name) ?? throw BitmapException.FormatNotSupported(name);
        }
    }

    public uint LayerCount => IsCube ? 6 : IsArray ? Depth : 1;

    /// <summary>This image's full mipmap chain across all faces/layers.</summary>
    public ReadOnlySpan<byte> PixelBytes()
    {
        var format = Format;
        ulong expected = format.SurfaceBytes(Width, Height, MipmapLevels) * LayerCount;
        if (expected > (ulong)_pixels.Length)
            throw BitmapException.PixelSliceOutOfBounds(0, expected, (ulong)_pixels.Length);
        return _pixels.AsSpan(0, (int)expected);
    }

    /// <summary>Write a DDS file for this image.</summary>
    public void WriteDds(Stream outp)
    {
        var format = Format;
        if (!IsCube && !IsArray && TypeName is not ("2D texture" or null))
            throw BitmapException.UnsupportedTextureType(TypeName ?? "");
        var bytes = PixelBytes();

        if (BitmapDds.NeedsDecodeForDds(format))
        {
            var decoded = DecodeAllToRgba8(format, bytes);
            WriteDdsWithFormat(outp, BitmapFormat.A8r8g8b8, decoded, IsCube, IsArray);
        }
        else
        {
            WriteDdsWithFormat(outp, format, bytes, IsCube, IsArray);
        }
    }

    /// <summary>Write a Tool-importable RGBA8 TIFF for this image.</summary>
    public void WriteTiff(Stream outp) => BitmapTiff.WriteImageTiff(this, outp);

    private byte[] DecodeAllToRgba8(BitmapFormat format, ReadOnlySpan<byte> bytes)
    {
        uint width = Width, height = Height, levels = MipmapLevels, layers = LayerCount;
        int faceSrcBytes = (int)format.SurfaceBytes(width, height, levels);
        using var ms = new MemoryStream();
        for (int face = 0; face < layers; face++)
        {
            int cursor = face * faceSrcBytes;
            for (int level = 0; level < levels; level++)
            {
                uint w = System.Math.Max(width >> level, 1), h = System.Math.Max(height >> level, 1);
                int levelSize = (int)format.LevelBytes(w, h);
                if (cursor + levelSize > bytes.Length)
                    throw BitmapException.PixelSliceOutOfBounds((ulong)cursor, (ulong)levelSize, (ulong)bytes.Length);
                var decoded = BitmapDecode.DecodeToRgba8(format, w, h, bytes.Slice(cursor, levelSize));
                ms.Write(decoded);
                cursor += levelSize;
            }
        }
        return ms.ToArray();
    }

    private void WriteDdsWithFormat(Stream outp, BitmapFormat format, ReadOnlySpan<byte> bytes, bool isCube, bool isArray)
    {
        bool needsDx10 = isArray || format.RequiresDxt10();
        if (needsDx10)
        {
            if (isCube)
                throw BitmapException.UnsupportedTextureType($"cube map of `{format}` requires DXT10 cube + array support");
            uint layers = isArray ? LayerCount : 1;
            BitmapDds.WriteDdsDx10(outp, format, Width, Height, MipmapLevels, layers, bytes);
        }
        else
        {
            BitmapDds.WriteDds(outp, format, Width, Height, MipmapLevels, isCube, bytes);
        }
    }
}
