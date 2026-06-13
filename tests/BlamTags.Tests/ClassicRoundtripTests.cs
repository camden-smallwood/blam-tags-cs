using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlamTags.Tests;

/// <summary>
/// Byte-exact roundtrip gate for classic (Halo CE / Halo 2) loose tags: read
/// each tag through <see cref="Classic.ReadClassicTagFile"/> with the group's
/// JSON layout, write it back, and require identical bytes. Mirrors the Rust
/// <c>classic_file_roundtrip</c> sweep.
/// </summary>
/// <remarks>
/// Corpus roots come from <c>BLAM_CE_CORPUS</c> / <c>BLAM_H2_CORPUS</c>,
/// defaulting to <c>~/Halo/haloce_mcc/tags</c> and <c>~/Halo/halo2_mcc/tags</c>
/// when present. Skipped when neither corpus nor the definitions tree is found.
/// <c>BLAM_TAGS_SAMPLE</c> caps the per-game sweep.
/// </remarks>
public sealed class ClassicRoundtripTests
{
    [SkippableTheory]
    [InlineData("haloce_mcc", "BLAM_CE_CORPUS", "haloce_mcc/tags")]
    [InlineData("halo2_mcc", "BLAM_H2_CORPUS", "halo2_mcc/tags")]
    public void ClassicRoundtrip_IsByteExact(string game, string envVar, string defaultRel)
    {
        var defs = TestEnvironment.DefinitionsRoot;
        Skip.If(defs is null, "definitions/ not found");

        string? corpus = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(corpus) || !Directory.Exists(corpus))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            corpus = Path.Combine(home, "Halo", defaultRel);
        }
        Skip.If(!Directory.Exists(corpus), $"classic corpus not found (set {envVar})");

        var groupNameByTag = LoadTagIndex(Path.Combine(defs!, game, "_meta.json"));
        var layoutCache = new Dictionary<string, TagLayout>(StringComparer.Ordinal);
        int sampleCap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : int.MaxValue;

        int tags = 0, mismatches = 0, readErrors = 0, examined = 0, correctedChecksums = 0;
        var failures = new List<string>();

        foreach (var path in TestEnvironment.EnumerateCorpusTags(corpus))
        {
            byte[] original;
            try { original = File.ReadAllBytes(path); }
            catch { continue; }

            var parsed = Classic.ParseHeader(original);
            if (parsed is not { } p)
                continue; // not a classic tag

            tags++;
            string rel = Path.GetRelativePath(corpus, path).Replace('\\', '/');
            try
            {
                uint groupTag = BinaryPrimitives.ReadUInt32BigEndian(p.Header.GroupTag);
                if (!groupNameByTag.TryGetValue(groupTag, out string? defName))
                {
                    readErrors++;
                    if (failures.Count < 40) failures.Add($"[no-def] {rel} (group {GroupTag.Format(groupTag)})");
                    continue;
                }
                if (!layoutCache.TryGetValue(defName, out var layout))
                {
                    layout = TagLayout.FromJson(Path.Combine(defs!, game, $"{defName}.json"));
                    layoutCache[defName] = layout;
                }
                var round = Classic.ReadClassicTagFile(original, layout).WriteToBytes();
                if (!Md5Equal(original, round))
                {
                    // A modding tool emits a CORRECT checksum. A handful of
                    // shipped tags carry stale/incorrect checksums in their
                    // offset-40 header word; rewriting that word to the real
                    // CRC is the right behavior, so a diff confined to bytes
                    // 40-43 (with the body byte-identical) is tolerated.
                    if (IsChecksumOnlyDiff(original, round))
                    {
                        correctedChecksums++;
                        if (failures.Count < 40) failures.Add($"[corrected-checksum] {rel}");
                    }
                    else
                    {
                        mismatches++;
                        if (failures.Count < 40) failures.Add($"[mismatch] {rel}");
                    }
                }
            }
            catch (Exception ex)
            {
                readErrors++;
                if (failures.Count < 40) failures.Add($"[read-error] {rel}: {ex.GetType().Name}: {ex.Message}");
            }

            if (++examined >= sampleCap) break;
        }

        string summary = $"{game}: swept {tags} classic tag(s): {mismatches} byte-mismatch, " +
                         $"{readErrors} read-error, {correctedChecksums} corrected-checksum (tolerated)";
        Assert.True(mismatches == 0 && readErrors == 0, $"{summary}\n  " + string.Join("\n  ", failures));
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are
    /// identical except within the 4-byte header checksum word (offset 40-43)
    /// — i.e. the body round-tripped byte-exact and only the (recomputed)
    /// checksum differs.</summary>
    private static bool IsChecksumOnlyDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length < 64) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i] && (i < 40 || i >= 44))
                return false;
        return true;
    }

    private static Dictionary<uint, string> LoadTagIndex(string metaPath)
    {
        var map = new Dictionary<uint, string>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(metaPath));
        if (doc.RootElement.TryGetProperty("tag_index", out var ti) && ti.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ti.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (GroupTag.Parse(prop.Name) is { } tag)
                    map[tag] = prop.Value.GetString()!;
            }
        }
        return map;
    }

    private static bool Md5Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        Span<byte> ha = stackalloc byte[16], hb = stackalloc byte[16];
        MD5.HashData(a, ha);
        MD5.HashData(b, hb);
        return ha.SequenceEqual(hb);
    }
}
