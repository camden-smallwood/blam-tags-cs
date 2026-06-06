using System.Globalization;
using System.Text;
using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>export</c> — emit a tag's state as replayable <c>set</c>
/// commands, plus a trailing comment listing fields whose type isn't
/// round-trippable via <c>set</c>.</summary>
public static class ExportCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        string file = args.Positional(0) ?? throw new CliError("export: missing <file>");
        string? subtree = args.Positional(1);

        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("export");
        string tagPath = loaded.Path;
        var root = loaded.Tag.Root;

        var start = subtree is null ? root
            : root.Descend(subtree) ?? throw new CliError($"subtree '{subtree}' does not resolve to a struct");

        var visitor = new ExportVisitor(ctx, subtree ?? "", tagPath);
        Walk.Run(start, visitor);

        var sb = new StringBuilder();
        sb.Append("# exported from ").Append(tagPath).Append('\n');
        if (subtree is not null) sb.Append("# subtree: ").Append(subtree).Append('\n');
        sb.Append('\n');
        foreach (var line in visitor.Lines) sb.Append(line).Append('\n');
        if (visitor.Skipped.Count != 0)
        {
            sb.Append('\n');
            sb.Append($"# {visitor.Skipped.Count} field(s) skipped (type not round-trippable via set):\n");
            foreach (var (p, reason) in visitor.Skipped)
                sb.Append($"#   {p}: {reason}\n");
        }

        if (output is not null) File.WriteAllText(output, sb.ToString());
        else Console.Out.Write(sb.ToString());
        return 0;
    }

    private sealed class ExportVisitor(CliContext ctx, string prefix, string tagPath) : FieldVisitor
    {
        public List<string> Lines { get; } = new();
        public List<(string Path, string Reason)> Skipped { get; } = new();

        private string Absolute(string path) =>
            prefix.Length == 0 ? path : path.Length == 0 ? prefix : $"{prefix}/{path}";

        public override void VisitLeaf(string path, int depth, TagField field)
        {
            if (field.Value is not { } value) return;
            string? v = ExportValue(ctx, value);
            if (v is not null)
            {
                string abs = Absolute(path);
                Lines.Add($"set {Quote(tagPath)} {Quote(abs)} {Quote(v)}");
            }
            else if (field.Name.Length != 0)
            {
                Skipped.Add((Absolute(path), NonSettableReason(value)));
            }
        }
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string? ExportValue(CliContext ctx, TagFieldData v) => v switch
    {
        TagFieldData.CharInteger x => x.Value.ToString(Inv),
        TagFieldData.ShortInteger x => x.Value.ToString(Inv),
        TagFieldData.LongInteger x => x.Value.ToString(Inv),
        TagFieldData.Int64Integer x => x.Value.ToString(Inv),
        TagFieldData.Tag x => GroupTag.Format(x.Value),

        TagFieldData.Angle x => x.Value.ToString(Inv),
        TagFieldData.Real x => x.Value.ToString(Inv),
        TagFieldData.RealSlider x => x.Value.ToString(Inv),
        TagFieldData.RealFraction x => x.Value.ToString(Inv),

        TagFieldData.CharEnum e => e.Value.ToString(Inv),
        TagFieldData.ShortEnum e => e.Value.ToString(Inv),
        TagFieldData.LongEnum e => e.Value.ToString(Inv),

        TagFieldData.ByteFlags f => $"0x{f.Value:X2}",
        TagFieldData.WordFlags f => $"0x{f.Value:X4}",
        TagFieldData.LongFlags f => $"0x{(uint)f.Value:X8}",
        TagFieldData.ByteBlockFlags f => $"0x{f.Value:X2}",
        TagFieldData.WordBlockFlags f => $"0x{f.Value:X4}",
        TagFieldData.LongBlockFlags f => $"0x{(uint)f.Value:X8}",

        TagFieldData.CharBlockIndex x => BlockIndex(x.Value),
        TagFieldData.CustomCharBlockIndex x => BlockIndex(x.Value),
        TagFieldData.ShortBlockIndex x => BlockIndex(x.Value),
        TagFieldData.CustomShortBlockIndex x => BlockIndex(x.Value),
        TagFieldData.LongBlockIndex x => BlockIndex(x.Value),
        TagFieldData.CustomLongBlockIndex x => BlockIndex(x.Value),

        TagFieldData.String s => s.Value,
        TagFieldData.LongString s => s.Value,
        TagFieldData.StringId s => s.Value.Value,
        TagFieldData.OldStringId s => s.Value.Value,

        TagFieldData.TagReference r => TagRef(ctx, r.Value),

        _ => null,
    };

    private static string TagRef(CliContext ctx, TagReferenceData r)
    {
        if (r.GroupTagAndName is null) return "none";
        var sb = new StringBuilder();
        Formatter.WriteTagReference(ctx, sb, r);
        return sb.ToString();
    }

    private static string BlockIndex(long v) => v == -1 ? "none" : v.ToString(Inv);

    private static string NonSettableReason(TagFieldData v) => v switch
    {
        TagFieldData.Data => "data blob",
        TagFieldData.Custom => "custom bytes",
        TagFieldData.ApiInterop => "runtime handle (use 'set <path> reset' to scrub)",
        TagFieldData.Point2dValue or TagFieldData.Rectangle2dValue or TagFieldData.RealPoint2dValue
            or TagFieldData.RealPoint3dValue or TagFieldData.RealVector2dValue or TagFieldData.RealVector3dValue
            or TagFieldData.RealQuaternionValue or TagFieldData.RealEulerAngles2dValue
            or TagFieldData.RealEulerAngles3dValue or TagFieldData.RealPlane2dValue
            or TagFieldData.RealPlane3dValue => "math composite",
        TagFieldData.RgbColorValue or TagFieldData.ArgbColorValue or TagFieldData.RealRgbColorValue
            or TagFieldData.RealArgbColorValue or TagFieldData.RealHsvColorValue
            or TagFieldData.RealAhsvColorValue => "color",
        TagFieldData.ShortIntegerBounds or TagFieldData.AngleBounds or TagFieldData.RealBounds
            or TagFieldData.FractionBounds => "bounds",
        _ => "type not supported by `set`",
    };

    /// <summary>POSIX shell quote matching shlex's <c>try_quote</c>: unquoted if
    /// non-empty and all chars are shell-safe; strings containing a backslash
    /// use the double-quote form with backslashes/quotes escaped; otherwise
    /// single-quoted.</summary>
    private static string Quote(string s)
    {
        if (s.Length == 0) return "''";
        if (s.All(IsShellSafe)) return s;
        if (s.Contains('\\'))
        {
            var esc = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
            return "\"" + esc + "\"";
        }
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    private static bool IsShellSafe(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
        || "_@%+=:,./-".IndexOf(c) >= 0;
}
