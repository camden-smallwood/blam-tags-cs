using System.Security.Cryptography;

namespace BlamTags.Tests;

/// <summary>
/// The headline gate: read every tag in the corpus, write it back, and
/// require the bytes to be identical. Mirrors the Rust
/// <c>examples/roundtrip.rs</c> sweep.
/// </summary>
/// <remarks>
/// Policy: a <b>byte-mismatch</b> (a tag that parsed but re-serialized to
/// different bytes) is always a hard failure. A <b>read-error</b> (a tag
/// the parser rejects) is tolerated only if the tag is on the
/// <see cref="KnownBadTags"/> allowlist — these are genuinely-malformed
/// tags in the shipped corpora that the Rust engine also rejects. A new,
/// un-allowlisted read-error fails the build.
///
/// All observed read-errors / mismatches are also dumped to a file
/// (<c>BLAM_TAGS_FAILLOG</c>, default a temp file) for inspection.
/// </remarks>
public sealed class RoundtripTests
{
    /// <summary>Corpus-relative paths (forward-slash) of tags that are
    /// genuinely malformed in the shipped data and which both the Rust
    /// engine and this port correctly reject. Tolerated as read-errors.</summary>
    private static readonly HashSet<string> KnownBadTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // halo3 — truncated render_model (oracle: "failed to fill whole buffer").
        "levels/shared/decorators/foliage/ivy_clump/ivy_clump.render_model",
        // halo3 — corrupt trailing chunk (oracle: unknown top-level signature "????").
        "objects/gear/human/residential/y_set_fan_small_a/y_set_fan_small_a.model_animation_graph",
        // halo4 — misaligned chunk in script tag (oracle: expected "tgst", got "gst?").
        "environments/solo/m90_sacrifice/device_machines/dm_trench_prober_c/scripts/dm_trench_prober_c.hsc",
        "environments/solo/m90_sacrifice/device_machines/dm_trench_prober_d/scripts/dm_trench_prober_d.hsc",
    };

    [SkippableFact]
    public void Roundtrip_Corpus_IsByteExact()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null,
            "No corpus configured. Set BLAM_TAGS_CORPUS to a directory of tag files.");

        var sampleCap = ParseSampleCap();
        int tags = 0, mismatches = 0, unexpectedReadErrors = 0, allowedReadErrors = 0, examined = 0;
        var realFailures = new List<string>();
        var faillog = new List<string>();
        string game = GameName(corpus!);

        foreach (var path in TestEnvironment.EnumerateCorpusTags(corpus!))
        {
            byte[] original;
            try { original = File.ReadAllBytes(path); }
            catch { continue; }

            if (!LooksLikeTagFile(original))
                continue; // extension-less corpus: skip non-tags by magic

            tags++;
            string rel = Rel(corpus!, path);
            try
            {
                var round = TagFile.ReadFromBytes(original).WriteToBytes();
                if (!Md5Equal(original, round))
                {
                    mismatches++;
                    faillog.Add($"{game}\tbyte-mismatch\t{rel}");
                    if (realFailures.Count < 40) realFailures.Add($"[mismatch] {rel}");
                }
            }
            catch (NotImplementedException)
            {
                throw new SkipException("Roundtrip engine not yet implemented (Phase 2).");
            }
            catch (Exception ex)
            {
                bool allowed = KnownBadTags.Contains(rel);
                faillog.Add($"{game}\tread-error{(allowed ? "(known-bad)" : "")}\t{rel}\t{ex.GetType().Name}: {ex.Message}");
                if (allowed)
                {
                    allowedReadErrors++;
                }
                else
                {
                    unexpectedReadErrors++;
                    if (realFailures.Count < 40) realFailures.Add($"[read-error] {rel}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (++examined >= sampleCap) break;
        }

        WriteFailLog(faillog);

        var summary = $"{game}: swept {tags} tag(s): {mismatches} byte-mismatch, " +
                      $"{unexpectedReadErrors} unexpected read-error, {allowedReadErrors} known-bad (tolerated)";
        Assert.True(mismatches == 0 && unexpectedReadErrors == 0,
            $"{summary}\n  " + string.Join("\n  ", realFailures));
    }

    private static void WriteFailLog(List<string> lines)
    {
        if (lines.Count == 0) return;
        string path = Environment.GetEnvironmentVariable("BLAM_TAGS_FAILLOG")
            ?? Path.Combine(Path.GetTempPath(), "blam_roundtrip_failures.txt");
        File.AppendAllLines(path, lines);
    }

    private static string GameName(string corpusRoot)
    {
        // ~/Halo/<game>/tags → "<game>"; fall back to the leaf name.
        var parent = Path.GetDirectoryName(corpusRoot.TrimEnd(Path.DirectorySeparatorChar));
        return parent is null ? Path.GetFileName(corpusRoot) : Path.GetFileName(parent);
    }

    private static int ParseSampleCap() =>
        int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0
            ? n : int.MaxValue;

    private static bool LooksLikeTagFile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64) return false;
        var sig = bytes.Slice(60, 4);
        return sig.SequenceEqual("BLAM"u8) || sig.SequenceEqual("MALB"u8);
    }

    private static bool Md5Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        Span<byte> ha = stackalloc byte[16], hb = stackalloc byte[16];
        MD5.HashData(a, ha);
        MD5.HashData(b, hb);
        return ha.SequenceEqual(hb);
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
