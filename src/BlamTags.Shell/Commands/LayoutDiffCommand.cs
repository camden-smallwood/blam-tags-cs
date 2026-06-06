using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary><c>layout-diff</c> — schema-level comparison: field adds /
/// removes / type / offset changes between two tags' layouts.</summary>
public static class LayoutDiffCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        string fileA = args.Positional(0) ?? throw new CliError("layout-diff: missing <file_a>");
        string fileB = args.Positional(1) ?? throw new CliError("layout-diff: missing <file_b>");

        TagFile tagA, tagB;
        try { tagA = TagFile.Read(fileA); } catch (Exception e) { throw new CliError($"failed to parse first file: {e.Message}"); }
        try { tagB = TagFile.Read(fileB); } catch (Exception e) { throw new CliError($"failed to parse second file: {e.Message}"); }

        string nameA = Path.GetFileName(fileA);
        string nameB = Path.GetFileName(fileB);
        Console.WriteLine($"Layout diff: {nameA} vs {nameB}");
        Console.WriteLine();

        var ga = tagA.Group;
        var gb = tagB.Group;
        if (ga.Tag != gb.Tag)
            Console.WriteLine($"  group_tag: {GroupTag.Format(ga.Tag)} -> {GroupTag.Format(gb.Tag)}");
        if (ga.Version != gb.Version)
            Console.WriteLine($"  group_version: {ga.Version} -> {gb.Version}");

        DiffStruct(tagA.Definitions.RootStruct(), tagB.Definitions.RootStruct(), 1);
        return 0;
    }

    private static void DiffStruct(TagStructDefinition a, TagStructDefinition b, int indent)
    {
        string pad = new(' ', indent * 2);
        bool nameChanged = a.Name != b.Name;
        bool sizeChanged = a.Size != b.Size;
        bool guidChanged = !a.Guid.AsSpan().SequenceEqual(b.Guid);

        var fieldsA = a.Fields().ToList();
        var fieldsB = b.Fields().ToList();

        if (!nameChanged && !sizeChanged && !guidChanged && FieldsEqual(fieldsA, fieldsB))
            return;

        Console.WriteLine(nameChanged ? $"{pad}struct {a.Name} -> {b.Name}:" : $"{pad}struct {a.Name}:");

        if (guidChanged)
            Console.WriteLine($"{pad}  guid: {Guid4(a.Guid)} -> {Guid4(b.Guid)}");
        if (sizeChanged)
        {
            int delta = b.Size - a.Size;
            Console.WriteLine($"{pad}  size: {a.Size} -> {b.Size} ({(delta >= 0 ? "+" : "")}{delta})");
        }

        foreach (var f in fieldsA)
        {
            string name = f.Name;
            if (name.Length == 0 || !fieldsB.Any(x => x.Name == name))
                Console.WriteLine($"{pad}  - {name} : {f.TypeName} @ {f.Offset}");
        }
        foreach (var f in fieldsB)
        {
            string name = f.Name;
            if (name.Length == 0 || !fieldsA.Any(x => x.Name == name))
                Console.WriteLine($"{pad}  + {name} : {f.TypeName} @ {f.Offset}");
        }

        foreach (var fa in fieldsA)
        {
            string name = fa.Name;
            if (name.Length == 0) continue;
            var match = fieldsB.Where(x => x.Name == name).Cast<TagFieldDefinition?>().FirstOrDefault();
            if (match is not { } fb) continue;

            var changes = new List<string>();
            if (fa.FieldType != fb.FieldType)
                changes.Add($"type: {fa.TypeName} -> {fb.TypeName}");
            if (fa.Offset != fb.Offset)
                changes.Add($"offset: {fa.Offset} -> {fb.Offset}");
            if (changes.Count != 0)
                Console.WriteLine($"{pad}  ~ {name} : {string.Join(", ", changes)}");

            if (fa.AsStruct() is { } sa && fb.AsStruct() is { } sb)
                DiffStruct(sa, sb, indent + 2);
            else if (fa.AsBlock() is { } ba && fb.AsBlock() is { } bb)
            {
                if (ba.MaxCount != bb.MaxCount)
                    Console.WriteLine($"{pad}    block max_count: {ba.MaxCount} -> {bb.MaxCount}");
                DiffStruct(ba.StructDefinition(), bb.StructDefinition(), indent + 2);
            }
            else if (fa.AsArray() is { } aa && fb.AsArray() is { } ab)
            {
                if (aa.Count != ab.Count)
                    Console.WriteLine($"{pad}    array count: {aa.Count} -> {ab.Count}");
                DiffStruct(aa.StructDefinition(), ab.StructDefinition(), indent + 2);
            }
        }
    }

    private static bool FieldsEqual(List<TagFieldDefinition> a, List<TagFieldDefinition> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Name != b[i].Name || a[i].FieldType != b[i].FieldType || a[i].Offset != b[i].Offset)
                return false;
        return true;
    }

    private static string Guid4(byte[] guid) =>
        "[" + string.Join(", ", guid.Take(4).Select(x => x.ToString("x2"))) + "]";
}
