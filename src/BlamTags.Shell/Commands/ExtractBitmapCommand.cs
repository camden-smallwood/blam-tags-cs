using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>extract-bitmap</c> — write each image of a <c>.bitmap</c> tag as
/// a TIFF or DDS. <c>--format tif</c> (default) is Tool-importable RGBA8;
/// <c>--format dds</c> is a debug dump. <c>--output</c> picks a directory, or
/// an exact <c>.tif</c>/<c>.dds</c> filename (single-image tags).</summary>
public static class ExtractBitmapCommand
{
    private enum OutFormat { Tif, Dds }

    private static string Ext(OutFormat f) => f == OutFormat.Tif ? "tif" : "dds";

    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        string formatStr = args.TakeOption("--format") ?? "tif";
        string file = args.Positional(0) ?? throw new CliError("extract-bitmap: missing <file>");

        var cliFormat = ParseFormat(formatStr);
        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("extract-bitmap");
        Bitmap bitmap;
        try { bitmap = Bitmap.New(loaded.Tag); }
        catch (BitmapException e) { throw new CliError($"tag does not look like a .bitmap: {e.Message}"); }

        int count = bitmap.Count;
        if (count == 0) { Console.WriteLine("no images in tag"); return 0; }

        string stem = Path.GetFileNameWithoutExtension(loaded.Path);
        string outputPath = output ?? ".";

        var extFormat = FormatFromExtension(outputPath);
        if (extFormat is { } ef)
            return RunToFile(outputPath, bitmap, count, ef);
        return RunToDir(outputPath, stem, bitmap, count, cliFormat);
    }

    private static OutFormat ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "tif" or "tiff" => OutFormat.Tif,
        "dds" => OutFormat.Dds,
        var other => throw new CliError($"unknown --format `{other}`; expected `tif` or `dds`"),
    };

    private static OutFormat? FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => OutFormat.Tif,
        ".dds" => OutFormat.Dds,
        _ => null,
    };

    private static int RunToFile(string target, Bitmap bitmap, int count, OutFormat format)
    {
        if (count > 1)
            throw new CliError($"tag has {count} images; --output as a `.{Ext(format)}` filename only works for single-image tags. Pass a directory path instead.");
        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        string summary = WriteOne(target, bitmap.Image(0)!, format);
        Console.WriteLine($"{target}: {summary}");
        return 0;
    }

    private static int RunToDir(string dir, string stem, Bitmap bitmap, int count, OutFormat format)
    {
        Directory.CreateDirectory(dir);
        string outDir = count > 1 ? Path.Combine(dir, stem) : dir;
        if (count > 1) Directory.CreateDirectory(outDir);

        int errors = 0, i = 0;
        foreach (var image in bitmap.Images())
        {
            string filename = count > 1 ? $"{i}.{Ext(format)}" : $"{stem}.{Ext(format)}";
            string path = Path.Combine(outDir, filename);
            try
            {
                string summary = WriteOne(path, image, format);
                Console.WriteLine($"{path}: {summary}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{path}: error: {e.Message}");
                errors++;
            }
            i++;
        }
        if (errors > 0) throw new CliError($"{errors} of {count} images failed");
        return 0;
    }

    private static string WriteOne(string path, BitmapImage image, OutFormat format)
    {
        string formatName = image.FormatName ?? "?";
        string typeName = image.TypeName ?? "?";
        uint mips = image.MipmapLevels;
        string summary = $"{image.Width}×{image.Height} {formatName} ({typeName}, {mips} mip{(mips == 1 ? "" : "s")})";
        using var fs = File.Create(path);
        if (format == OutFormat.Tif) image.WriteTiff(fs);
        else image.WriteDds(fs);
        return summary;
    }
}
