using System.Text;
using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// End-to-end ASS parity: the C# ASS output must be byte-identical to the
/// Rust oracle's <c>extract-geometry</c> across all three input paths —
/// <c>.scenario_structure_bsp</c> (sbsp → single ASS), <c>.scenario</c>
/// (per-BSP ASS with stli lighting), and <c>.model</c> render→ASS
/// (instance geometry). The C# side builds in-process; only the oracle is a
/// subprocess. ASS is text, so this is a straight byte compare.
/// </summary>
public sealed class AssParityTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    [SkippableFact]
    public void Ass_Sbsp_MatchesOracle()
    {
        var (corpus, cap) = Setup();
        string tmp = Path.Combine(Path.GetTempPath(), "blam_ass_sbsp");

        int compared = 0, mismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus, "*.scenario_structure_bsp", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;
            examined++;
            ResetDir(tmp);
            var r = Oracle.Run("extract-geometry", path, "--output", tmp);
            string oracleFile = Path.Combine(tmp, $"{Path.GetFileNameWithoutExtension(path)}.ASS");
            if (r.ExitCode != 0 || !File.Exists(oracleFile)) continue;

            string myText;
            try { myText = AssFile.FromScenarioStructureBsp(TagFile.Read(path)).ToText(); }
            catch (Exception ex) { mismatches++; Add(failures, $"{Rel(corpus, path)}: cs threw {ex.GetType().Name}: {ex.Message}"); continue; }

            compared++;
            Compare(File.ReadAllBytes(oracleFile), myText, Rel(corpus, path), failures, ref mismatches);
        }

        CleanupAndAssert(tmp, compared, mismatches, failures, "sbsp");
    }

    [SkippableFact]
    public void Ass_RenderModel_MatchesOracle()
    {
        var (corpus, cap) = Setup();
        string tmp = Path.Combine(Path.GetTempPath(), "blam_ass_rm");

        int compared = 0, mismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus, "*.model", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;
            examined++;
            ResetDir(tmp);
            // Force ASS render output; ignore the JMS coll/phys siblings.
            var r = Oracle.Run("extract-geometry", path, "--force", "ass", "--flat", "--output", tmp);
            string oracleFile = Path.Combine(tmp, $"{Path.GetFileNameWithoutExtension(path)}.render.ass");
            if (r.ExitCode != 0 || !File.Exists(oracleFile)) continue;

            string? myText;
            try
            {
                var root = TagFile.Read(path).Root;
                string? tagsRoot = TagPaths.DeriveTagsRoot(path);
                string? rel = root.ReadTagRefPath("render model");
                if (tagsRoot is null || string.IsNullOrEmpty(rel)) continue;
                string rmPath = TagPaths.ResolveTagPath(tagsRoot, rel, "render_model");
                if (!File.Exists(rmPath)) continue;
                myText = AssFile.FromRenderModel(TagFile.Read(rmPath)).ToText();
            }
            catch (Exception ex) { mismatches++; Add(failures, $"{Rel(corpus, path)}: cs threw {ex.GetType().Name}: {ex.Message}"); continue; }

            compared++;
            Compare(File.ReadAllBytes(oracleFile), myText, Rel(corpus, path), failures, ref mismatches);
        }

        CleanupAndAssert(tmp, compared, mismatches, failures, "render_model");
    }

    [SkippableFact]
    public void Ass_Scenario_MatchesOracle()
    {
        var (corpus, cap) = Setup();
        string tmp = Path.Combine(Path.GetTempPath(), "blam_ass_scn");

        int compared = 0, mismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus, "*.scenario", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;
            examined++;
            ResetDir(tmp);
            var r = Oracle.Run("extract-geometry", path, "--flat", "--output", tmp);
            var oracleFiles = Directory.Exists(tmp) ? Directory.GetFiles(tmp, "*.ass") : [];
            if (oracleFiles.Length == 0) continue;

            // Build per-BSP ASS in-process (geometry + paired stli lighting).
            Dictionary<string, string> mine;
            try { mine = BuildScenarioAss(path); }
            catch (Exception ex) { mismatches += oracleFiles.Length; Add(failures, $"{Rel(corpus, path)}: cs threw {ex.GetType().Name}: {ex.Message}"); continue; }

            string scnStem = Path.GetFileNameWithoutExtension(path);
            foreach (var oracleFile in oracleFiles)
            {
                string bspStem = Path.GetFileName(oracleFile);
                if (bspStem.StartsWith(scnStem + ".", StringComparison.Ordinal))
                    bspStem = bspStem[(scnStem.Length + 1)..];
                bspStem = Path.GetFileNameWithoutExtension(bspStem);
                compared++;
                if (!mine.TryGetValue(bspStem, out var myText))
                {
                    mismatches++; Add(failures, $"{Rel(corpus, path)} [{bspStem}]: oracle emitted but cs did not");
                    continue;
                }
                Compare(File.ReadAllBytes(oracleFile), myText, $"{Rel(corpus, path)} [{bspStem}]", failures, ref mismatches);
            }
        }

        CleanupAndAssert(tmp, compared, mismatches, failures, "scenario");
    }

    private static Dictionary<string, string> BuildScenarioAss(string scnPath)
    {
        var result = new Dictionary<string, string>();
        var root = TagFile.Read(scnPath).Root;
        string? tagsRoot = TagPaths.DeriveTagsRoot(scnPath);
        if (tagsRoot is null) return result;
        var bsps = root.FieldPath("structure bsps")?.AsBlock();
        if (bsps is null) return result;

        for (int bi = 0; bi < bsps.Count; bi++)
        {
            var entry = bsps.Element(bi)!;
            string? bspRel = NonEmpty(entry.ReadTagRefPath("structure bsp"));
            if (bspRel is null) continue;
            string bspPath = TagPaths.ResolveTagPath(tagsRoot, bspRel, "scenario_structure_bsp");
            if (!File.Exists(bspPath)) continue;
            var ass = AssFile.FromScenarioStructureBsp(TagFile.Read(bspPath));
            string? lightingRel = NonEmpty(entry.ReadTagRefPath("structure lighting_info"));
            if (lightingRel is not null)
            {
                string stliPath = TagPaths.ResolveTagPath(tagsRoot, lightingRel, "scenario_structure_lighting_info");
                if (File.Exists(stliPath)) ass.AddLightsFromStli(TagFile.Read(stliPath));
            }
            string bspStem = bspRel.Split('\\').LastOrDefault() ?? "bsp";
            result[bspStem] = ass.ToText();
        }
        return result;
    }

    // ---- shared helpers ----

    private static (string Corpus, int Cap) Setup()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");
        Skip.If(TestEnvironment.OraclePath is null, "Oracle not found.");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : 40;
        return (corpus!, cap);
    }

    private static void Compare(byte[] oracleBytes, string myText, string label, List<string> failures, ref int mismatches)
    {
        byte[] myBytes = Utf8NoBom.GetBytes(myText);
        if (!oracleBytes.AsSpan().SequenceEqual(myBytes))
        {
            mismatches++;
            Add(failures, $"{label}: {FirstDiff(oracleBytes, myBytes)}");
        }
    }

    private static void CleanupAndAssert(string tmp, int compared, int mismatches, List<string> failures, string what)
    {
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Assert.True(mismatches == 0, $"[{what}] compared {compared} ASS files: {mismatches} mismatch\n  " + string.Join("\n  ", failures));
        Assert.True(compared > 0, $"[{what}] no ASS files compared");
    }

    private static void ResetDir(string tmp)
    {
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static void Add(List<string> failures, string msg) { if (failures.Count < 25) failures.Add(msg); }
    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    private static string FirstDiff(byte[] a, byte[] b)
    {
        int n = System.Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        int lineStart = i;
        while (lineStart > 0 && a[lineStart - 1] != (byte)'\n') lineStart--;
        int line = 1;
        for (int k = 0; k < i; k++) if (a[k] == (byte)'\n') line++;
        return $"len oracle={a.Length} cs={b.Length}, first diff @byte {i} (line {line}): oracle=`{SliceLine(a, lineStart)}` cs=`{(lineStart < b.Length ? SliceLine(b, lineStart) : "<eof>")}`";
    }

    private static string SliceLine(byte[] data, int start)
    {
        int end = start;
        while (end < data.Length && data[end] != (byte)'\n') end++;
        return Encoding.ASCII.GetString(data, start, System.Math.Min(end - start, 80));
    }
}
