using System.Globalization;
using System.Text;
using BlamTags;

namespace BlamTags.Shell;

/// <summary>
/// One-line text rendering of <see cref="TagFieldData"/> values for CLI
/// output (the library ships no Display impls). Mirrors the Rust
/// <c>format.rs</c>.
/// </summary>
public static class Formatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string FormatValue(CliContext ctx, TagFieldData value, bool hex)
    {
        var sb = new StringBuilder();
        Write(ctx, sb, value, hex);
        return sb.ToString();
    }

    private static void Write(CliContext ctx, StringBuilder o, TagFieldData v, bool hex)
    {
        switch (v)
        {
            case TagFieldData.String s: o.Append('"').Append(s.Value).Append('"'); break;
            case TagFieldData.LongString s: o.Append('"').Append(s.Value).Append('"'); break;

            case TagFieldData.StringId s: WriteStringId(o, s.Value.Value); break;
            case TagFieldData.OldStringId s: WriteStringId(o, s.Value.Value); break;
            case TagFieldData.TagReference r: WriteTagReference(ctx, o, r.Value); break;
            case TagFieldData.Data d: o.Append($"data [{d.Value.Length} bytes]"); break;
            case TagFieldData.ApiInterop i:
                var ai = i.Value;
                if (ai.Descriptor is { } dd && ai.Address is { } aa && ai.DefinitionAddress is { } da)
                    o.Append($"api_interop {{ descriptor=0x{dd:X8}, address=0x{aa:X8}, definition_address=0x{da:X8} }}");
                else
                    o.Append($"api_interop [{ai.Raw.Length} bytes]");
                break;

            case TagFieldData.CharInteger x: o.Append(hex ? $"0x{(byte)x.Value:X2}" : x.Value.ToString(Inv)); break;
            case TagFieldData.ShortInteger x: o.Append(hex ? $"0x{(ushort)x.Value:X4}" : x.Value.ToString(Inv)); break;
            case TagFieldData.LongInteger x: o.Append(hex ? $"0x{(uint)x.Value:X8}" : x.Value.ToString(Inv)); break;
            case TagFieldData.Int64Integer x: o.Append(hex ? $"0x{(ulong)x.Value:X16}" : x.Value.ToString(Inv)); break;
            case TagFieldData.ByteInteger x: o.Append(hex ? $"0x{x.Value:X2}" : x.Value.ToString(Inv)); break;
            case TagFieldData.WordInteger x: o.Append(hex ? $"0x{x.Value:X4}" : x.Value.ToString(Inv)); break;
            case TagFieldData.DwordInteger x: o.Append(hex ? $"0x{x.Value:X8}" : x.Value.ToString(Inv)); break;
            case TagFieldData.QwordInteger x: o.Append(hex ? $"0x{x.Value:X16}" : x.Value.ToString(Inv)); break;
            case TagFieldData.Tag x: o.Append(GroupTag.Format(x.Value)); break;

            case TagFieldData.CharEnum e: WriteEnum(o, e.Value, e.Name); break;
            case TagFieldData.ShortEnum e: WriteEnum(o, e.Value, e.Name); break;
            case TagFieldData.LongEnum e: WriteEnum(o, e.Value, e.Name); break;

            case TagFieldData.ByteFlags f: WriteFlags(o, f.Value, f.Names, 2); break;
            case TagFieldData.WordFlags f: WriteFlags(o, f.Value, f.Names, 4); break;
            case TagFieldData.LongFlags f: WriteFlags(o, (uint)f.Value, f.Names, 8); break;

            case TagFieldData.ByteBlockFlags x: o.Append($"0x{x.Value:X2}"); break;
            case TagFieldData.WordBlockFlags x: o.Append($"0x{x.Value:X4}"); break;
            case TagFieldData.LongBlockFlags x: o.Append($"0x{(uint)x.Value:X8}"); break;

            case TagFieldData.CharBlockIndex x: WriteBlockIndex(o, x.Value); break;
            case TagFieldData.CustomCharBlockIndex x: WriteBlockIndex(o, x.Value); break;
            case TagFieldData.ShortBlockIndex x: WriteBlockIndex(o, x.Value); break;
            case TagFieldData.CustomShortBlockIndex x: WriteBlockIndex(o, x.Value); break;
            case TagFieldData.LongBlockIndex x: WriteBlockIndex(o, x.Value); break;
            case TagFieldData.CustomLongBlockIndex x: WriteBlockIndex(o, x.Value); break;

            case TagFieldData.Angle x:
                o.Append($"{x.Value.ToString("F4", Inv)} rad ({(x.Value * 180.0 / System.Math.PI).ToString("F2", Inv)} deg)");
                break;
            case TagFieldData.Real x: o.Append(R(x.Value)); break;
            case TagFieldData.RealSlider x: o.Append(R(x.Value)); break;
            case TagFieldData.RealFraction x: o.Append(R(x.Value)); break;

            case TagFieldData.Point2dValue p: o.Append($"{p.Value.X}, {p.Value.Y}"); break;
            case TagFieldData.Rectangle2dValue r: o.Append($"{r.Value.Top}, {r.Value.Left}, {r.Value.Bottom}, {r.Value.Right}"); break;
            case TagFieldData.RealPoint2dValue p: o.Append($"x={R(p.Value.X)}, y={R(p.Value.Y)}"); break;
            case TagFieldData.RealPoint3dValue p: o.Append($"x={R(p.Value.X)}, y={R(p.Value.Y)}, z={R(p.Value.Z)}"); break;
            case TagFieldData.RealVector2dValue v2: o.Append($"i={R(v2.Value.I)}, j={R(v2.Value.J)}"); break;
            case TagFieldData.RealVector3dValue v3: o.Append($"i={R(v3.Value.I)}, j={R(v3.Value.J)}, k={R(v3.Value.K)}"); break;
            case TagFieldData.RealQuaternionValue q: o.Append($"i={R(q.Value.I)}, j={R(q.Value.J)}, k={R(q.Value.K)}, w={R(q.Value.W)}"); break;
            case TagFieldData.RealEulerAngles2dValue e2: o.Append($"yaw={R(e2.Value.Yaw)}, pitch={R(e2.Value.Pitch)}"); break;
            case TagFieldData.RealEulerAngles3dValue e3: o.Append($"yaw={R(e3.Value.Yaw)}, pitch={R(e3.Value.Pitch)}, roll={R(e3.Value.Roll)}"); break;
            case TagFieldData.RealPlane2dValue p: o.Append($"i={R(p.Value.I)}, j={R(p.Value.J)}, d={R(p.Value.D)}"); break;
            case TagFieldData.RealPlane3dValue p: o.Append($"i={R(p.Value.I)}, j={R(p.Value.J)}, k={R(p.Value.K)}, d={R(p.Value.D)}"); break;

            case TagFieldData.RgbColorValue c: o.Append($"0x{c.Value.Packed:X8}"); break;
            case TagFieldData.ArgbColorValue c: o.Append($"0x{c.Value.Packed:X8}"); break;
            case TagFieldData.RealRgbColorValue c: o.Append($"r={R(c.Value.Red)}, g={R(c.Value.Green)}, b={R(c.Value.Blue)}"); break;
            case TagFieldData.RealArgbColorValue c: o.Append($"a={R(c.Value.Alpha)}, r={R(c.Value.Red)}, g={R(c.Value.Green)}, b={R(c.Value.Blue)}"); break;
            case TagFieldData.RealHsvColorValue c: o.Append($"h={R(c.Value.Hue)}, s={R(c.Value.Saturation)}, v={R(c.Value.Value)}"); break;
            case TagFieldData.RealAhsvColorValue c: o.Append($"a={R(c.Value.Alpha)}, h={R(c.Value.Hue)}, s={R(c.Value.Saturation)}, v={R(c.Value.Value)}"); break;

            case TagFieldData.ShortIntegerBounds b: o.Append($"{b.Value.Lower}..{b.Value.Upper}"); break;
            case TagFieldData.AngleBounds b: o.Append($"{R(b.Value.Lower)}..{R(b.Value.Upper)}"); break;
            case TagFieldData.RealBounds b: o.Append($"{R(b.Value.Lower)}..{R(b.Value.Upper)}"); break;
            case TagFieldData.FractionBounds b: o.Append($"{R(b.Value.Lower)}..{R(b.Value.Upper)}"); break;

            case TagFieldData.Custom d: o.Append($"custom [{d.Value.Length} bytes]"); break;
        }
    }

    private static void WriteStringId(StringBuilder o, string s)
    {
        if (string.IsNullOrEmpty(s)) o.Append("NONE");
        else o.Append('"').Append(s).Append('"');
    }

    /// <summary>Render a tag reference as <c>&lt;path&gt;.&lt;group_name&gt;</c>
    /// (canonical filename form) when the group resolves, else
    /// <c>&lt;group_tag&gt;:&lt;path&gt;</c>; <c>NONE</c> for null refs.</summary>
    public static void WriteTagReference(CliContext ctx, StringBuilder o, TagReferenceData r)
    {
        if (r.GroupTagAndName is not var (groupTag, path) || r.GroupTagAndName is null)
        {
            o.Append("NONE");
            return;
        }
        var name = ctx.TagIndex.NameFor(groupTag);
        if (name is not null) o.Append($"{path}.{name}");
        else o.Append($"{GroupTag.Format(groupTag)}:{path}");
    }

    private static void WriteEnum(StringBuilder o, long value, string? name)
    {
        if (name is not null) o.Append($"{value} ({name})");
        else o.Append(value.ToString(Inv));
    }

    private static void WriteFlags(StringBuilder o, ulong value, IReadOnlyList<(uint Bit, string Name)> names, int hexWidth)
    {
        string hex = value.ToString("X" + hexWidth, Inv);
        if (names.Count == 0)
            o.Append($"0x{hex} (none set)");
        else
            o.Append($"0x{hex} [{string.Join(", ", names.Select(n => n.Name))}]");
    }

    private static void WriteBlockIndex(StringBuilder o, long value)
    {
        if (value == -1) o.Append("NONE");
        else o.Append(value.ToString(Inv));
    }

    private static string R(float v) => v.ToString(Inv);
}
