using System.Globalization;
using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>check</c> — integrity validator: enum-out-of-range, unknown
/// flag bits, non-finite reals, and (with <c>--tags-root</c>) missing tag
/// references. (JSON output: TODO.)</summary>
public static class CheckCommand
{
    private enum Kind { Enum, Flag, Real, Reference }

    private static string Label(Kind k) => k switch
    {
        Kind.Enum => "enum", Kind.Flag => "flag", Kind.Real => "real", Kind.Reference => "reference", _ => "?",
    };

    public static int Run(CliContext ctx, Args args)
    {
        string? tagsRoot = args.TakeOption("--tags-root");
        string? only = args.TakeOption("--only");
        bool json = args.TakeFlag("--json");
        bool strict = args.TakeFlag("--strict");
        if (json) throw new CliError("check --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("check: missing <file>");

        var kinds = ParseOnly(only);
        ctx.EnsureLoaded(file);
        var tag = ctx.LoadedOrThrow("check").Tag;

        var findings = new List<(string Path, Kind Kind, string Detail)>();
        Walk.Run(tag.Root, new CheckVisitor(kinds, findings));

        if (kinds.Contains(Kind.Reference) && tagsRoot is not null)
            CheckReferencesOnDisk(tag, tagsRoot, findings);

        if (findings.Count == 0)
        {
            Console.WriteLine("clean — no issues found");
        }
        else
        {
            foreach (var (path, kind, detail) in findings)
                Console.WriteLine($"[{Label(kind)}] {path}: {detail}");
            Console.WriteLine();
            Console.WriteLine($"{findings.Count} finding(s)");
        }

        if (strict && findings.Count != 0)
            throw new CliError($"{findings.Count} finding(s)");
        return 0;
    }

    private static HashSet<Kind> ParseOnly(string? raw)
    {
        if (raw is null) return new HashSet<Kind> { Kind.Enum, Kind.Flag, Kind.Real, Kind.Reference };
        var set = new HashSet<Kind>();
        foreach (var part in raw.Split(','))
        {
            set.Add(part.Trim() switch
            {
                "enum" or "enums" => Kind.Enum,
                "flag" or "flags" => Kind.Flag,
                "real" or "reals" => Kind.Real,
                "reference" or "references" or "ref" or "refs" => Kind.Reference,
                var other => throw new CliError($"unknown check kind '{other}' (expected: enum, flag, real, reference)"),
            });
        }
        return set;
    }

    private sealed class CheckVisitor(HashSet<Kind> enabled, List<(string, Kind, string)> findings) : FieldVisitor
    {
        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (field.Value is not { } value) return;
            switch (value)
            {
                case TagFieldData.CharEnum { Name: null } e when enabled.Contains(Kind.Enum):
                    findings.Add((path, Kind.Enum, $"{e.Value} is not a declared variant")); break;
                case TagFieldData.ShortEnum { Name: null } e when enabled.Contains(Kind.Enum):
                    findings.Add((path, Kind.Enum, $"{e.Value} is not a declared variant")); break;
                case TagFieldData.LongEnum { Name: null } e when enabled.Contains(Kind.Enum):
                    findings.Add((path, Kind.Enum, $"{e.Value} is not a declared variant")); break;

                case TagFieldData.ByteFlags f when enabled.Contains(Kind.Flag) && ExtraBits(f.Value, f.Names) is { } x:
                    findings.Add((path, Kind.Flag, $"bits 0x{x:X2} set without a declared name")); break;
                case TagFieldData.WordFlags f when enabled.Contains(Kind.Flag) && ExtraBits(f.Value, f.Names) is { } x:
                    findings.Add((path, Kind.Flag, $"bits 0x{x:X4} set without a declared name")); break;
                case TagFieldData.LongFlags f when enabled.Contains(Kind.Flag) && ExtraBits((uint)f.Value, f.Names) is { } x:
                    findings.Add((path, Kind.Flag, $"bits 0x{x:X8} set without a declared name")); break;

                case TagFieldData.Angle r when enabled.Contains(Kind.Real) && !float.IsFinite(r.Value):
                    findings.Add((path, Kind.Real, $"value is {RustFloat(r.Value)}")); break;
                case TagFieldData.Real r when enabled.Contains(Kind.Real) && !float.IsFinite(r.Value):
                    findings.Add((path, Kind.Real, $"value is {RustFloat(r.Value)}")); break;
                case TagFieldData.RealSlider r when enabled.Contains(Kind.Real) && !float.IsFinite(r.Value):
                    findings.Add((path, Kind.Real, $"value is {RustFloat(r.Value)}")); break;
                case TagFieldData.RealFraction r when enabled.Contains(Kind.Real) && !float.IsFinite(r.Value):
                    findings.Add((path, Kind.Real, $"value is {RustFloat(r.Value)}")); break;
            }
        }
    }

    private static ulong? ExtraBits(ulong value, IReadOnlyList<(uint Bit, string Name)> names)
    {
        ulong declared = 0;
        foreach (var (bit, _) in names) declared |= 1UL << (int)bit;
        ulong extra = value & ~declared;
        return extra != 0 ? extra : null;
    }

    private static string RustFloat(float f) =>
        float.IsNaN(f) ? "NaN" : float.IsPositiveInfinity(f) ? "inf" : float.IsNegativeInfinity(f) ? "-inf"
            : f.ToString(CultureInfo.InvariantCulture);

    private static void CheckReferencesOnDisk(TagFile tag, string tagsRoot, List<(string, Kind, string)> findings)
    {
        var refs = new List<(string FieldPath, string Stem)>();
        Walk.Run(tag.Root, new RefCollector(refs));

        var seenMissing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fieldPath, stemRaw) in refs)
        {
            string rel = string.Join(Path.DirectorySeparatorChar, stemRaw.Split('\\'));
            string abs = Path.Combine(tagsRoot, rel);
            string parent = Path.GetDirectoryName(abs) ?? tagsRoot;
            string stem = Path.GetFileName(abs);

            if (!StemExists(parent, stem) && seenMissing.Add(stemRaw))
                findings.Add((fieldPath, Kind.Reference, $"no file with stem '{stemRaw}' under {tagsRoot}"));
        }
    }

    private sealed class RefCollector(List<(string, string)> refs) : FieldVisitor
    {
        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (field.Value is TagFieldData.TagReference r && r.Value.GroupTagAndName is var (_, p) && r.Value.GroupTagAndName is not null)
                refs.Add((path, p));
        }
    }

    private static bool StemExists(string parent, string stem)
    {
        if (!Directory.Exists(parent)) return false;
        string needle = stem + ".";
        foreach (var e in Directory.EnumerateFiles(parent))
            if (Path.GetFileName(e).StartsWith(needle, StringComparison.Ordinal))
                return true;
        return false;
    }
}
