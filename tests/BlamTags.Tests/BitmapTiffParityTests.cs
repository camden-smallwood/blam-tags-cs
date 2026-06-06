using System.Buffers.Binary;
using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// End-to-end bitmap decode parity: the C# TIFF output's pixels must match
/// the Rust oracle's TIFF output's pixels, across the corpus. This exercises
/// the full decode path (BC1/2/3 + every Halo format) and confirms the
/// bcdec rounding matches. The TIFF container bytes differ (different
/// encoder) — only the decoded RGBA8 is compared.
/// </summary>
public sealed class BitmapTiffParityTests
{
    [SkippableFact]
    public void Tiff_DecodedPixels_MatchOracle()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");
        Skip.If(TestEnvironment.OraclePath is null, "Oracle not found.");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : 80;

        string tmp = Path.Combine(Path.GetTempPath(), "blam_tiff_parity");
        Directory.CreateDirectory(tmp);

        int compared = 0, mismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus!, "*.bitmap", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;

            Bitmap bitmap;
            try { bitmap = Bitmap.New(TagFile.Read(path)); }
            catch { continue; }
            if (bitmap.Count != 1) continue; // single-image only (exact-filename oracle path)
            var img = bitmap.Image(0)!;
            if (img.TypeName is "3D texture") continue;

            examined++;

            // My TIFF → decoded pixels.
            byte[] myRgba;
            uint mw, mh;
            try
            {
                using var ms = new MemoryStream();
                img.WriteTiff(ms);
                (mw, mh, myRgba) = ReadTiffRgba(ms.ToArray());
            }
            catch (Exception ex) { failures.Add($"{Rel(corpus!, path)}: cs decode {ex.GetType().Name}"); mismatches++; continue; }

            // Oracle TIFF → decoded pixels.
            string outFile = Path.Combine(tmp, "o.tif");
            File.Delete(outFile);
            var r = Oracle.Run("extract-bitmap", path, "--output", outFile);
            if (r.ExitCode != 0 || !File.Exists(outFile)) continue; // oracle declined (deferred layout, etc.)

            var (ow, oh, oRgba) = ReadTiffRgba(File.ReadAllBytes(outFile));
            compared++;
            if (mw != ow || mh != oh || !myRgba.AsSpan().SequenceEqual(oRgba))
            {
                mismatches++;
                if (failures.Count < 25)
                {
                    string detail = "";
                    if (mw == ow && mh == oh)
                    {
                        int d = -1;
                        for (int k = 0; k < myRgba.Length; k++) if (myRgba[k] != oRgba[k]) { d = k; break; }
                        if (d >= 0)
                        {
                            int px = d / 4 * 4;
                            detail = $" first-diff@px{d / 4}(ch{d % 4}) mine=[{myRgba[px]},{myRgba[px + 1]},{myRgba[px + 2]},{myRgba[px + 3]}] oracle=[{oRgba[px]},{oRgba[px + 1]},{oRgba[px + 2]},{oRgba[px + 3]}]";
                        }
                    }
                    failures.Add($"{Rel(corpus!, path)}: {img.FormatName} {mw}x{mh} vs {ow}x{oh}{detail}");
                }
            }
        }

        Assert.True(mismatches == 0, $"compared {compared} TIFFs: {mismatches} pixel-mismatch\n  " + string.Join("\n  ", failures));
        Assert.True(compared > 0, "no TIFFs compared");
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    /// <summary>Minimal baseline-TIFF reader: little-endian, uncompressed,
    /// gathers all strips into the full pixel buffer. Returns (width, height,
    /// rgba bytes).</summary>
    private static (uint W, uint H, byte[] Rgba) ReadTiffRgba(byte[] t)
    {
        if (t[0] != (byte)'I' || t[1] != (byte)'I')
            throw new InvalidOperationException("only little-endian TIFF supported in test reader");
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan((int)ifd));

        uint width = 0, height = 0;
        uint[] stripOffsets = [], stripByteCounts = [];
        for (int i = 0; i < count; i++)
        {
            int e = (int)ifd + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e + 2));
            uint cnt = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(e + 4));
            int valOff = e + 8;
            switch (tag)
            {
                case 256: width = ReadScalar(t, type, valOff); break;
                case 257: height = ReadScalar(t, type, valOff); break;
                case 273: stripOffsets = ReadArray(t, type, cnt, valOff); break;
                case 279: stripByteCounts = ReadArray(t, type, cnt, valOff); break;
            }
        }

        var rgba = new byte[(int)width * (int)height * 4];
        int pos = 0;
        for (int s = 0; s < stripOffsets.Length; s++)
        {
            int off = (int)stripOffsets[s], len = (int)stripByteCounts[s];
            Array.Copy(t, off, rgba, pos, len);
            pos += len;
        }
        return (width, height, rgba);
    }

    private static uint ReadScalar(byte[] t, ushort type, int valOff) => type switch
    {
        3 => BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(valOff)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(valOff)),
        _ => throw new InvalidOperationException($"unexpected scalar type {type}"),
    };

    private static uint[] ReadArray(byte[] t, ushort type, uint cnt, int valOff)
    {
        int elemSize = type == 3 ? 2 : 4;
        int total = (int)cnt * elemSize;
        int baseOff = total <= 4 ? valOff : (int)BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(valOff));
        var arr = new uint[cnt];
        for (int i = 0; i < cnt; i++)
            arr[i] = type == 3
                ? BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(baseOff + i * 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(baseOff + i * 4));
        return arr;
    }
}
