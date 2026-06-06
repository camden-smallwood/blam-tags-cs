using System.Globalization;
using BlamTags;

namespace BlamTags.Shell;

/// <summary>
/// Parse CLI-flavored strings into <see cref="TagFieldData"/> values for
/// <c>set</c>. Mirrors the Rust <c>parse.rs</c> conventions (enum by name or
/// int, <c>none</c> sentinels, hex/decimal masks, <c>&lt;path&gt;.&lt;group&gt;</c>
/// tag refs, <c>reset</c>/triple api-interop).
/// </summary>
public static class ValueParser
{
    public static TagFieldData Parse(CliContext ctx, TagField field, string input) => field.FieldType switch
    {
        TagFieldType.CharInteger => new TagFieldData.CharInteger(ParseInt<sbyte>(input, "i8")),
        TagFieldType.ShortInteger => new TagFieldData.ShortInteger(ParseInt<short>(input, "i16")),
        TagFieldType.LongInteger => new TagFieldData.LongInteger(ParseInt<int>(input, "i32")),
        TagFieldType.Int64Integer => new TagFieldData.Int64Integer(ParseInt<long>(input, "i64")),
        TagFieldType.ByteInteger => new TagFieldData.ByteInteger(ParseInt<byte>(input, "u8")),
        TagFieldType.WordInteger => new TagFieldData.WordInteger(ParseInt<ushort>(input, "u16")),
        TagFieldType.DwordInteger => new TagFieldData.DwordInteger(ParseInt<uint>(input, "u32")),
        TagFieldType.QwordInteger => new TagFieldData.QwordInteger(ParseInt<ulong>(input, "u64")),
        TagFieldType.Tag => new TagFieldData.Tag(GroupTag.Parse(input) ?? throw Bad("group tag must be 1..=4 ASCII chars")),

        TagFieldType.Angle => new TagFieldData.Angle(ParseFloat(input)),
        TagFieldType.Real => new TagFieldData.Real(ParseFloat(input)),
        TagFieldType.RealSlider => new TagFieldData.RealSlider(ParseFloat(input)),
        TagFieldType.RealFraction => new TagFieldData.RealFraction(ParseFloat(input)),

        TagFieldType.CharEnum => new TagFieldData.CharEnum((sbyte)ParseEnum(field, input), null),
        TagFieldType.ShortEnum => new TagFieldData.ShortEnum((short)ParseEnum(field, input), null),
        TagFieldType.LongEnum => new TagFieldData.LongEnum(ParseEnum(field, input), null),

        TagFieldType.ByteFlags => new TagFieldData.ByteFlags((byte)ParseMask(input), Empty),
        TagFieldType.WordFlags => new TagFieldData.WordFlags((ushort)ParseMask(input), Empty),
        TagFieldType.LongFlags => new TagFieldData.LongFlags((int)ParseMask(input), Empty),

        TagFieldType.ByteBlockFlags => new TagFieldData.ByteBlockFlags((byte)ParseMask(input)),
        TagFieldType.WordBlockFlags => new TagFieldData.WordBlockFlags((ushort)ParseMask(input)),
        TagFieldType.LongBlockFlags => new TagFieldData.LongBlockFlags((int)ParseMask(input)),

        TagFieldType.CharBlockIndex => new TagFieldData.CharBlockIndex((sbyte)ParseBlockIndex(input)),
        TagFieldType.CustomCharBlockIndex => new TagFieldData.CustomCharBlockIndex((sbyte)ParseBlockIndex(input)),
        TagFieldType.ShortBlockIndex => new TagFieldData.ShortBlockIndex((short)ParseBlockIndex(input)),
        TagFieldType.CustomShortBlockIndex => new TagFieldData.CustomShortBlockIndex((short)ParseBlockIndex(input)),
        TagFieldType.LongBlockIndex => new TagFieldData.LongBlockIndex(ParseBlockIndex(input)),
        TagFieldType.CustomLongBlockIndex => new TagFieldData.CustomLongBlockIndex(ParseBlockIndex(input)),

        TagFieldType.String => new TagFieldData.String(input),
        TagFieldType.LongString => new TagFieldData.LongString(input),
        TagFieldType.StringId => new TagFieldData.StringId(new StringIdData { Value = input }),
        TagFieldType.OldStringId => new TagFieldData.OldStringId(new StringIdData { Value = input }),

        TagFieldType.TagReference => new TagFieldData.TagReference(ParseTagReference(ctx, input)),
        TagFieldType.ApiInterop => new TagFieldData.ApiInterop(ParseApiInterop(input)),

        TagFieldType.Data => throw Bad("parsing 'data' fields from a string is not supported"),
        TagFieldType.VertexBuffer => throw Bad("parsing vertex_buffer fields is not supported"),
        TagFieldType.Struct or TagFieldType.Block or TagFieldType.Array or TagFieldType.PageableResource
            => throw Bad("cannot set container field types directly"),

        var ty => throw Bad($"parsing field type {ty} from a string is not supported"),
    };

    private static readonly IReadOnlyList<(uint, string)> Empty = Array.Empty<(uint, string)>();

    private static CliError Bad(string msg) => new(msg);

    private static T ParseInt<T>(string s, string what) where T : System.Numerics.INumberBase<T> =>
        T.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Bad($"expected {what}");

    private static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw Bad("expected f32");

    private static int ParseEnum(TagField field, string input)
    {
        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        if (field.Options() is TagOptions.Enum e)
        {
            for (int i = 0; i < e.Names.Count; i++)
                if (string.Equals(e.Names[i], input, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        throw Bad($"enum option '{input}' not found");
    }

    private static long ParseMask(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
                ? h : throw Bad("expected hex integer");
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? d : throw Bad("expected integer");
    }

    private static int ParseBlockIndex(string s)
    {
        if (string.Equals(s, "none", StringComparison.OrdinalIgnoreCase))
            return -1;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : throw Bad("expected integer or 'none'");
    }

    private static TagReferenceData ParseTagReference(CliContext ctx, string s)
    {
        if (s.Length == 0 || string.Equals(s, "none", StringComparison.OrdinalIgnoreCase))
            return new TagReferenceData { GroupTagAndName = null };

        // Preferred: `<path>.<group_name>`, split on the last '.'.
        int dot = s.LastIndexOf('.');
        if (dot >= 0)
        {
            string name = s[(dot + 1)..];
            if (ctx.TagIndex.GroupTagFor(name) is { } gt)
                return new TagReferenceData { GroupTagAndName = (gt, s[..dot]) };
        }
        // Legacy: `<group_tag>:<path>`.
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            uint gt = GroupTag.Parse(s[..colon]) ?? throw Bad("group tag must be 1..=4 ASCII chars");
            return new TagReferenceData { GroupTagAndName = (gt, s[(colon + 1)..]) };
        }
        throw Bad("tag reference format: <path>.<group_name> (e.g. objects/characters/elite/elite.biped), or legacy <group_tag>:<path>, or 'none'");
    }

    private static ApiInteropData ParseApiInterop(string s)
    {
        string t = s.Trim();
        if (string.Equals(t, "reset", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
            return ApiInteropData.Reset();

        var parts = t.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length != 3)
            throw Bad("api_interop format: 'reset', or 'descriptor,address,definition_address' (each u32, decimal or 0x…)");
        uint One(string p)
        {
            if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(p.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : throw Bad("expected u32 (decimal or 0x hex)");
            return uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : throw Bad("expected u32 (decimal or 0x hex)");
        }
        var raw = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0), One(parts[0]));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), One(parts[1]));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), One(parts[2]));
        return new ApiInteropData { Raw = raw, Endian = Endian.Le };
    }
}
