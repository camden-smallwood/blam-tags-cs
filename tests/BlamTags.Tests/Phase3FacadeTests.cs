using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// Phase 3 gates for the editing facade. Three properties, swept over the
/// configured corpus:
/// <list type="number">
///   <item><b>Navigation + parse</b> reach and decode every leaf without
///     throwing (full-tree walk).</item>
///   <item><b>Serialize is byte-exact</b>: setting every in-place field to its
///     own value and re-writing reproduces the original bytes.</item>
///   <item><b>Block edits work</b>: add-then-delete restores the original bytes.</item>
/// </list>
/// Together with the Phase 2 byte-exact roundtrip (which proves the read path),
/// these prove the write/edit path is correct and consistent.
/// </summary>
public sealed class Phase3FacadeTests
{
    /// <summary>Yield (relative path, file bytes) for up to <paramref name="cap"/>
    /// real tags. Each gate parses once from these bytes — no redundant
    /// re-reads.</summary>
    private static IEnumerable<(string Rel, byte[] Bytes)> SampleTagBytes(string corpus, int cap)
    {
        int n = 0;
        foreach (var path in TestEnvironment.EnumerateCorpusTags(corpus))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { continue; }
            if (!TestEnvironment.LooksLikeTagFile(bytes)) continue;
            yield return (Path.GetRelativePath(corpus, path), bytes);
            if (++n >= cap) yield break;
        }
    }

    private static int Cap(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : fallback;

    [SkippableFact]
    public void Facade_TreeWalk_ReadsEveryLeaf()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");

        int tags = 0; long leaves = 0;
        var failures = new List<string>();
        foreach (var (rel, bytes) in SampleTagBytes(corpus!, Cap(4000)))
        {
            TagFile tag;
            try { tag = TagFile.ReadFromBytes(bytes); }
            catch { continue; } // known-bad tags handled by the roundtrip gate
            tags++;
            try { leaves += WalkRead(tag.Root); }
            catch (Exception ex)
            {
                if (failures.Count < 25) failures.Add($"{rel}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0,
            $"walked {tags} tag(s), {leaves} leaves; {failures.Count} failed:\n  " + string.Join("\n  ", failures));
        Assert.True(tags > 0, "no tags walked");
    }

    [SkippableFact]
    public void Facade_NoopSet_InPlaceFields_IsByteExact()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");

        int tags = 0, mismatches = 0;
        var failures = new List<string>();
        foreach (var (rel, bytes) in SampleTagBytes(corpus!, Cap(2000)))
        {
            TagFile tag;
            try { tag = TagFile.ReadFromBytes(bytes); }
            catch { continue; }
            tags++;
            WalkNoopSet(tag.Root);
            var rewritten = tag.WriteToBytes();
            if (!bytes.AsSpan().SequenceEqual(rewritten))
            {
                mismatches++;
                if (failures.Count < 25) failures.Add(rel);
            }
        }
        Assert.True(mismatches == 0,
            $"noop-set over {tags} tag(s): {mismatches} differed:\n  " + string.Join("\n  ", failures));
    }

    [SkippableFact]
    public void Facade_BlockAddDelete_IsByteExact()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");

        int tested = 0, mismatches = 0;
        var failures = new List<string>();
        foreach (var (rel, bytes) in SampleTagBytes(corpus!, Cap(1500)))
        {
            TagFile tag;
            try { tag = TagFile.ReadFromBytes(bytes); }
            catch { continue; }

            // Find the first root-level block.
            TagBlock? block = null;
            foreach (var f in tag.Root.Fields())
            {
                block = f.AsBlock();
                if (block is not null) break;
            }
            if (block is null) continue;

            int before = block.Count;
            block.AddElement();
            if (block.Count != before + 1) { failures.Add($"{rel}: add didn't grow"); mismatches++; continue; }
            block.DeleteElement(block.Count - 1);
            if (block.Count != before) { failures.Add($"{rel}: delete didn't shrink"); mismatches++; continue; }

            tested++;
            var rewritten = tag.WriteToBytes();
            if (!bytes.AsSpan().SequenceEqual(rewritten))
            {
                mismatches++;
                if (failures.Count < 25) failures.Add($"{rel}: add+delete not byte-exact");
            }
        }
        Assert.True(mismatches == 0,
            $"block add/delete over {tested} tag(s): {mismatches} failed:\n  " + string.Join("\n  ", failures));
        Assert.True(tested > 0, "no blocks exercised");
    }

    // ---- recursive walkers ----

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

    private static void WalkNoopSet(TagStruct s)
    {
        foreach (var f in s.Fields())
        {
            switch (f.FieldType)
            {
                case TagFieldType.Struct:
                    if (f.AsStruct() is { } ns) WalkNoopSet(ns);
                    break;
                case TagFieldType.Block:
                    if (f.AsBlock() is { } b)
                        foreach (var el in b.Elements()) WalkNoopSet(el);
                    break;
                case TagFieldType.Array:
                    if (f.AsArray() is { } a)
                        foreach (var el in a.Elements()) WalkNoopSet(el);
                    break;
                case TagFieldType.PageableResource:
                    if (f.AsResource()?.AsStruct() is { } rs) WalkNoopSet(rs);
                    break;
                default:
                    if (f.Value is { } v && IsInPlace(v))
                        f.Set(v);
                    break;
            }
        }
    }

    /// <summary>In-place field values re-serialize byte-exactly. Excludes
    /// fixed strings (post-NUL bytes aren't preserved by re-encode) and
    /// sub-chunk leaves (whose payloads can normalize on re-encode).</summary>
    private static bool IsInPlace(TagFieldData v) => v is not (
        TagFieldData.String or TagFieldData.LongString or TagFieldData.StringId or
        TagFieldData.OldStringId or TagFieldData.TagReference or TagFieldData.Data or
        TagFieldData.ApiInterop);
}
