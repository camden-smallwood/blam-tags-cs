using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Tool-importable RGBA8 TIFF writer (SnowyMouse libtiff profile:
/// uncompressed, RGB photometric, 4 samples, unassociated alpha, top-left,
/// chunky). Cube maps emit a 4×3 horizontal cross; 2D arrays emit a vertical
/// strip. The container bytes aren't identical to the Rust <c>tiff</c> crate
/// (different encoder), but the decoded pixels are.
/// </summary>
internal static class BitmapTiff
{
    private const uint CubeFaceCount = 6;
    // (column, row, storage_face_index): 0=+X,1=-X,2=+Y,3=-Y,4=+Z,5=-Z.
    private static readonly (uint Col, uint Row, uint Face)[] CubeCrossCells =
    [
        (1, 0, 2), (0, 1, 0), (1, 1, 4), (2, 1, 1), (3, 1, 5), (1, 2, 3),
    ];
    private static readonly byte[] CrossBg = [0x00, 0x00, 0xFF, 0xFF];

    public static void WriteImageTiff(BitmapImage image, Stream outp)
    {
        if (image.TypeName == "3D texture")
            throw BitmapException.TiffLayoutDeferred("3D texture");

        uint compositeW, compositeH;
        byte[] rgba;
        if (image.IsCube)
            (compositeW, compositeH, rgba) = ComposeCubeCross(image);
        else if (image.IsArray)
            (compositeW, compositeH, rgba) = ComposeLayerStrip(image);
        else
        {
            var format = image.Format;
            uint width = image.Width, height = image.Height;
            var bytes = image.PixelBytes();
            int mip0Len = (int)format.LevelBytes(width, height);
            if (bytes.Length < mip0Len)
                throw BitmapException.PixelSliceOutOfBounds(0, (ulong)mip0Len, (ulong)bytes.Length);
            rgba = BitmapDecode.DecodeToRgba8(format, width, height, bytes[..mip0Len]);
            (compositeW, compositeH) = (width, height);
        }

        WriteRgba8Tiff(outp, compositeW, compositeH, rgba);
    }

    /// <summary>Write RGBA8 (memory order [R,G,B,A]) as a baseline TIFF.</summary>
    public static void WriteRgba8Tiff(Stream outp, uint width, uint height, ReadOnlySpan<byte> rgba)
    {
        int expected = (int)width * (int)height * 4;
        if (rgba.Length != expected)
            throw BitmapException.PixelSliceOutOfBounds(0, (ulong)expected, (ulong)rgba.Length);

        // Layout: header(8) + IFD(13 entries: 2+13*12+4 = 162) + BitsPerSample[4](8)
        // + SampleFormat[4](8) + pixels.
        const uint ifdOffset = 8;
        const uint bitsPerSampleOffset = ifdOffset + 2 + 13 * 12 + 4; // 170
        const uint sampleFormatOffset = bitsPerSampleOffset + 8;       // 178
        const uint stripOffset = sampleFormatOffset + 8;               // 186

        Span<byte> hdr = stackalloc byte[8];
        hdr[0] = (byte)'I'; hdr[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], ifdOffset);
        outp.Write(hdr);

        // IFD: 13 entries, sorted by tag.
        byte[] ifd = new byte[2 + 13 * 12 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(ifd, 13);
        int e = 2;
        const ushort SHORT = 3, LONG = 4;
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(e), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(e + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(e + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(e + 8), value);
            e += 12;
        }
        Entry(256, LONG, 1, width);                    // ImageWidth
        Entry(257, LONG, 1, height);                   // ImageLength
        Entry(258, SHORT, 4, bitsPerSampleOffset);     // BitsPerSample → [8,8,8,8]
        Entry(259, SHORT, 1, 1);                        // Compression = none
        Entry(262, SHORT, 1, 2);                        // Photometric = RGB
        Entry(273, LONG, 1, stripOffset);              // StripOffsets
        Entry(274, SHORT, 1, 1);                        // Orientation = TOPLEFT
        Entry(277, SHORT, 1, 4);                        // SamplesPerPixel
        Entry(278, LONG, 1, height);                   // RowsPerStrip
        Entry(279, LONG, 1, (uint)expected);           // StripByteCounts
        Entry(284, SHORT, 1, 1);                        // PlanarConfiguration = chunky
        Entry(338, SHORT, 1, 2);                        // ExtraSamples = unassociated alpha
        Entry(339, SHORT, 4, sampleFormatOffset);      // SampleFormat → [1,1,1,1] (UINT)
        BinaryPrimitives.WriteUInt32LittleEndian(ifd.AsSpan(e), 0); // next IFD = none
        outp.Write(ifd);

        // Out-of-line SHORT[4] arrays.
        Span<byte> arr = stackalloc byte[8];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt16LittleEndian(arr[(i * 2)..], 8);
        outp.Write(arr); // BitsPerSample
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt16LittleEndian(arr[(i * 2)..], 1);
        outp.Write(arr); // SampleFormat (UINT)

        outp.Write(rgba);
    }

    private static (uint, uint, byte[]) ComposeCubeCross(BitmapImage image)
    {
        var format = image.Format;
        uint cellW = image.Width, cellH = image.Height;
        uint outW = cellW * 4, outH = cellH * 3;
        var outBuf = MakeFilled(outW, outH, CrossBg);
        foreach (var (col, row, face) in CubeCrossCells)
        {
            if (face >= CubeFaceCount) throw new InvalidOperationException("bad cube cell");
            var layerBytes = LayerMip0Bytes(image, face);
            var faceRgba = BitmapDecode.DecodeToRgba8(format, cellW, cellH, layerBytes);
            BlitRgba(faceRgba, cellW, cellH, outBuf, outW, col * cellW, row * cellH);
        }
        return (outW, outH, outBuf);
    }

    private static (uint, uint, byte[]) ComposeLayerStrip(BitmapImage image)
    {
        var format = image.Format;
        uint width = image.Width, height = image.Height, layers = image.LayerCount;
        uint totalHeight = height * layers;
        var outBuf = new byte[(int)width * (int)totalHeight * 4];
        for (uint layer = 0; layer < layers; layer++)
        {
            var layerBytes = LayerMip0Bytes(image, layer);
            var layerRgba = BitmapDecode.DecodeToRgba8(format, width, height, layerBytes);
            BlitRgba(layerRgba, width, height, outBuf, width, 0, layer * height);
        }
        return (width, totalHeight, outBuf);
    }

    private static byte[] LayerMip0Bytes(BitmapImage image, uint layerIndex)
    {
        var format = image.Format;
        uint width = image.Width, height = image.Height;
        var bytes = image.PixelBytes();
        int chainBytes = (int)format.SurfaceBytes(width, height, image.MipmapLevels);
        int mip0Bytes = (int)format.LevelBytes(width, height);
        int start = (int)layerIndex * chainBytes;
        int end = start + mip0Bytes;
        if (end > bytes.Length)
            throw BitmapException.PixelSliceOutOfBounds((ulong)start, (ulong)mip0Bytes, (ulong)bytes.Length);
        return bytes.Slice(start, mip0Bytes).ToArray();
    }

    private static byte[] MakeFilled(uint width, uint height, byte[] pixel)
    {
        var outBuf = new byte[(int)width * (int)height * 4];
        for (int i = 0; i < outBuf.Length; i += 4)
        {
            outBuf[i] = pixel[0]; outBuf[i + 1] = pixel[1]; outBuf[i + 2] = pixel[2]; outBuf[i + 3] = pixel[3];
        }
        return outBuf;
    }

    private static void BlitRgba(ReadOnlySpan<byte> src, uint srcW, uint srcH, byte[] dst, uint dstW, uint dstX, uint dstY)
    {
        int rowBytes = (int)srcW * 4;
        for (uint y = 0; y < srcH; y++)
        {
            int srcOff = (int)y * rowBytes;
            int dstOff = (int)(dstY + y) * (int)dstW * 4 + (int)dstX * 4;
            src.Slice(srcOff, rowBytes).CopyTo(dst.AsSpan(dstOff, rowBytes));
        }
    }
}
