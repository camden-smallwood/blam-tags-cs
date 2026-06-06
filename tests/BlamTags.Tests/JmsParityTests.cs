using System.Text;
using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// End-to-end JMS parity: the C# <c>extract-geometry</c> JMS output must be
/// byte-identical to the Rust oracle's <c>extract-geometry --force jms</c>
/// across the model (hlmt) corpus. JMS is a text format, so this is a
/// straight byte compare — it exercises the full geometry pipeline (skeleton
/// chaining, strip decode, compression-bounds dequant, BSP edge-ring walk,
/// Havok shape/constraint reconstruction, and the fixed 10-place float
/// formatting).
///
/// The C# side runs in-process (load hlmt → follow render/collision/physics
/// references → build <see cref="JmsFile"/>), mirroring the CLI's hlmt
/// dispatch; only the oracle is a subprocess.
/// </summary>
public sealed class JmsParityTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly (string Kind, string Field, string Ext)[] Kinds =
    [
        ("render", "render model", "render_model"),
        ("collision", "collision model", "collision_model"),
        ("physics", "physics_model", "physics_model"),
    ];

    [SkippableFact]
    public void Jms_MatchesOracle_ByteForByte()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");
        Skip.If(TestEnvironment.OraclePath is null, "Oracle not found.");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : 120;

        string tmp = Path.Combine(Path.GetTempPath(), "blam_jms_parity");

        int compared = 0, mismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus!, "*.model", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;
            examined++;

            // Oracle: emit JMS for whatever kinds it can.
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            Directory.CreateDirectory(tmp);
            var r = Oracle.Run("extract-geometry", path, "--force", "jms", "--flat", "--output", tmp);
            string stem = Path.GetFileNameWithoutExtension(path);

            var oracleFiles = Kinds
                .Select(k => (k.Kind, File: Path.Combine(tmp, $"{stem}.{k.Kind}.jms")))
                .Where(t => File.Exists(t.File))
                .ToList();
            if (oracleFiles.Count == 0) continue; // oracle declined (e.g. H4 cache-only refs)

            // C# in-process build of each kind the oracle produced.
            Dictionary<string, string?> mine;
            try { mine = BuildAll(path); }
            catch (Exception ex)
            {
                mismatches += oracleFiles.Count;
                if (failures.Count < 25) failures.Add($"{Rel(corpus!, path)}: cs build threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var (kind, file) in oracleFiles)
            {
                compared++;
                byte[] oracleBytes = File.ReadAllBytes(file);
                string? myText = mine.GetValueOrDefault(kind);
                if (myText is null)
                {
                    mismatches++;
                    if (failures.Count < 25) failures.Add($"{Rel(corpus!, path)} [{kind}]: oracle emitted but cs did not");
                    continue;
                }
                byte[] myBytes = Utf8NoBom.GetBytes(myText);
                if (!oracleBytes.AsSpan().SequenceEqual(myBytes))
                {
                    mismatches++;
                    if (failures.Count < 25)
                        failures.Add($"{Rel(corpus!, path)} [{kind}]: {FirstDiff(oracleBytes, myBytes)}");
                }
            }
        }

        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Assert.True(mismatches == 0, $"compared {compared} JMS files: {mismatches} mismatch\n  " + string.Join("\n  ", failures));
        Assert.True(compared > 0, "no JMS files compared");
    }

    /// <summary>Replicate the CLI's hlmt → render/collision/physics JMS
    /// dispatch in-process. Returns the JMS text per kind (null when that
    /// kind isn't present / can't be built).</summary>
    private static Dictionary<string, string?> BuildAll(string hlmtPath)
    {
        var result = new Dictionary<string, string?>();
        var tag = TagFile.Read(hlmtPath);
        var root = tag.Root;
        string? tagsRoot = TagPaths.DeriveTagsRoot(hlmtPath);
        if (tagsRoot is null) return result;

        TagFile? Load(string field, string ext)
        {
            string? rel = root.ReadTagRefPath(field);
            if (string.IsNullOrEmpty(rel)) return null;
            string p = TagPaths.ResolveTagPath(tagsRoot, rel, ext);
            return File.Exists(p) ? TagFile.Read(p) : null;
        }

        var renderTag = Load("render model", "render_model");
        JmsFile? renderJms = renderTag is null ? null : JmsFile.FromRenderModel(renderTag);
        if (renderJms is not null) result["render"] = renderJms.ToText();
        var skeleton = renderJms?.Nodes;
        if (skeleton is null) return result; // collision/physics need the skeleton

        var collisionTag = Load("collision model", "collision_model");
        if (collisionTag is not null)
            result["collision"] = JmsFile.FromCollisionModelWithSkeleton(collisionTag, skeleton).ToText();

        var physicsTag = Load("physics_model", "physics_model");
        if (physicsTag is not null)
            result["physics"] = JmsFile.FromPhysicsModelWithSkeleton(physicsTag, skeleton).ToText();

        return result;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    private static string FirstDiff(byte[] a, byte[] b)
    {
        int n = System.Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        // Report the line containing the first diff.
        int lineStart = i;
        while (lineStart > 0 && a[lineStart - 1] != (byte)'\n') lineStart--;
        int line = 1;
        for (int k = 0; k < i; k++) if (a[k] == (byte)'\n') line++;
        string oracleLine = SliceLine(a, lineStart);
        string myLine = lineStart < b.Length ? SliceLine(b, lineStart) : "<eof>";
        return $"len oracle={a.Length} cs={b.Length}, first diff @byte {i} (line {line}): oracle=`{oracleLine}` cs=`{myLine}`";
    }

    private static string SliceLine(byte[] data, int start)
    {
        int end = start;
        while (end < data.Length && data[end] != (byte)'\n') end++;
        return Encoding.ASCII.GetString(data, start, System.Math.Min(end - start, 80));
    }
}
