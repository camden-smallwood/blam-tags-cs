using System.Diagnostics;
using BlamTags;
using Xunit.Abstractions;

namespace BlamTags.Tests;

/// <summary>
/// Ad-hoc performance probe (not a gate): times parse / full-tree-walk /
/// write separately over a corpus sample and reports totals plus the
/// slowest individual tags, to locate hot paths. Run explicitly with a
/// corpus + sample, e.g.
/// <c>BLAM_TAGS_CORPUS=~/Halo/halo4_mcc/tags BLAM_TAGS_SAMPLE=400 dotnet test --filter Perf_Probe</c>.
/// </summary>
public sealed class PerfDiagnostics(ITestOutputHelper output)
{
    [SkippableFact]
    public void Perf_Probe()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : 400;

        double parseMs = 0, walkMs = 0, writeMs = 0;
        long totalBytes = 0;
        int tags = 0;
        var slow = new List<(double Ms, long Bytes, long Leaves, string Rel)>();
        var sw = new Stopwatch();

        foreach (var path in TestEnvironment.EnumerateCorpusTags(corpus!))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch { continue; }
            if (!TestEnvironment.LooksLikeTagFile(bytes)) continue;

            string rel = Path.GetRelativePath(corpus!, path);

            sw.Restart();
            TagFile tag;
            try { tag = TagFile.ReadFromBytes(bytes); } catch { continue; }
            sw.Stop(); double p = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            long leaves = WalkRead(tag.Root);
            sw.Stop(); double w = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            _ = tag.WriteToBytes();
            sw.Stop(); double wr = sw.Elapsed.TotalMilliseconds;

            parseMs += p; walkMs += w; writeMs += wr;
            totalBytes += bytes.Length;
            double tot = p + w + wr;
            slow.Add((tot, bytes.Length, leaves, rel));
            if (++tags >= cap) break;
        }

        slow.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        output.WriteLine($"tags={tags}  totalMB={totalBytes / 1024.0 / 1024.0:F1}");
        output.WriteLine($"parse={parseMs:F0}ms  walk={walkMs:F0}ms  write={writeMs:F0}ms  total={parseMs + walkMs + writeMs:F0}ms");
        output.WriteLine("slowest:");
        foreach (var s in slow.Take(15))
            output.WriteLine($"  {s.Ms,9:F1}ms  {s.Bytes / 1024.0,8:F0}KB  leaves={s.Leaves,-8}  {s.Rel}");

        Assert.True(tags > 0);
    }

    private static long WalkRead(TagStruct s)
    {
        long leaves = 0;
        foreach (var f in s.Fields())
        {
            switch (f.FieldType)
            {
                case TagFieldType.Struct:
                    if (f.AsStruct() is { } ns) leaves += WalkRead(ns);
                    break;
                case TagFieldType.Block:
                    if (f.AsBlock() is { } b)
                        foreach (var el in b.Elements()) leaves += WalkRead(el);
                    break;
                case TagFieldType.Array:
                    if (f.AsArray() is { } a)
                        foreach (var el in a.Elements()) leaves += WalkRead(el);
                    break;
                case TagFieldType.PageableResource:
                    if (f.AsResource()?.AsStruct() is { } rs) leaves += WalkRead(rs);
                    break;
                default:
                    _ = f.Value;
                    leaves++;
                    break;
            }
        }
        return leaves;
    }
}
