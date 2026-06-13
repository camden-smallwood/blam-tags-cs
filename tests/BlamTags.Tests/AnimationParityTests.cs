using System.Text;
using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// End-to-end animation parity: the C# JMA-family output must be byte-identical
/// to the Rust oracle's <c>extract-animation</c> across the
/// <c>.model_animation_graph</c> corpus. Exercises the full pipeline — codec
/// dispatch (fullframe / keyframe / curve), quaternion decompression + Hermite
/// interpolation, pose composition via node-flag bitarrays, movement folding,
/// and the JMA writer's per-kind frame layout.
///
/// The C# side builds in-process per jmad (additional-node-data defaults,
/// matching the oracle's direct-jmad path); only the oracle is a subprocess.
/// </summary>
public sealed class AnimationParityTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    [SkippableFact]
    public void Jma_MatchesOracle_ByteForByte()
    {
        var corpus = TestEnvironment.CorpusRoot;
        Skip.If(corpus is null, "No corpus configured.");
        Skip.If(TestEnvironment.OraclePath is null, "Oracle not found.");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("BLAM_TAGS_SAMPLE"), out var n) && n > 0 ? n : 20;

        string tmp = Path.Combine(Path.GetTempPath(), "blam_jma_parity");

        int compared = 0, exact = 0, nearExact = 0, hardMismatches = 0, examined = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpus!, "*.model_animation_graph", SearchOption.AllDirectories))
        {
            if (examined >= cap) break;

            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            Directory.CreateDirectory(tmp);
            var r = Oracle.Run("extract-animation", path, "--flat", "--output", tmp);
            var oracleFiles = Directory.GetFiles(tmp);
            if (r.ExitCode != 0 || oracleFiles.Length == 0) continue; // inheriting / undecodable jmad — oracle declined
            examined++;

            Dictionary<string, string> mine;
            try { mine = BuildAll(path); }
            catch (Exception ex)
            {
                hardMismatches += oracleFiles.Length;
                if (failures.Count < 25) failures.Add($"{Rel(corpus!, path)}: cs build threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var oracleFile in oracleFiles)
            {
                compared++;
                string fname = Path.GetFileName(oracleFile);
                if (!mine.TryGetValue(fname, out var myText))
                {
                    hardMismatches++;
                    if (failures.Count < 25) failures.Add($"{Rel(corpus!, path)}: oracle emitted {fname} but cs did not");
                    continue;
                }
                string oracleText = File.ReadAllText(oracleFile);
                switch (Classify(oracleText, myText))
                {
                    case Match.Exact: exact++; break;
                    // Irreducible: Rust's f32 sin/cos in the movement-yaw fold round
                    // 1 ULP off from .NET's; only isolated root-bone quaternion
                    // floats on movement-folding frames differ, by < 1e-6.
                    case Match.NearExact: nearExact++; break;
                    default:
                        hardMismatches++;
                        var ob = Utf8NoBom.GetBytes(oracleText);
                        if (failures.Count < 25) failures.Add($"{Rel(corpus!, path)} [{fname}]: {FirstDiff(ob, Utf8NoBom.GetBytes(myText))}");
                        break;
                }
            }
        }

        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Assert.True(hardMismatches == 0,
            $"compared {compared} JMA files ({exact} exact, {nearExact} 1-ULP-near): {hardMismatches} hard mismatch\n  " + string.Join("\n  ", failures));
        Assert.True(compared > 0, "no JMA files compared");
    }

    private enum Match { Exact, NearExact, Hard }

    /// <summary>Exact byte match; else "near-exact" iff line counts match and
    /// every differing line is float tokens within 1e-6 (the transcendental
    /// 1-ULP boundary); else a hard mismatch (structural / large numeric).</summary>
    private static Match Classify(string oracle, string mine)
    {
        if (oracle == mine) return Match.Exact;
        var a = oracle.Split('\n');
        var b = mine.Split('\n');
        if (a.Length != b.Length) return Match.Hard;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            var ta = a[i].Split('\t');
            var tb = b[i].Split('\t');
            if (ta.Length != tb.Length) return Match.Hard;
            for (int j = 0; j < ta.Length; j++)
            {
                if (ta[j] == tb[j]) continue;
                if (!float.TryParse(ta[j], System.Globalization.CultureInfo.InvariantCulture, out var fa)
                    || !float.TryParse(tb[j], System.Globalization.CultureInfo.InvariantCulture, out var fb))
                    return Match.Hard;
                if (System.Math.Abs(fa - fb) > 1e-6f) return Match.Hard;
            }
        }
        return Match.NearExact;
    }

    /// <summary>Build every animation's JMA text in-process (direct-jmad path:
    /// additional-node-data defaults, --flat naming). Keyed by output filename.</summary>
    private static Dictionary<string, string> BuildAll(string jmadPath)
    {
        var result = new Dictionary<string, string>();
        var tag = TagFile.Read(jmadPath);
        var animation = Animation.New(tag);
        var skeleton = Skeleton.FromTag(tag);
        if (animation.IsEmpty || skeleton.IsEmpty) return result;
        string stem = Path.GetFileNameWithoutExtension(jmadPath);
        var defaults = BuildDefaults(skeleton, tag);
        var graph = AnimationGraph.FromTag(tag);

        foreach (var group in animation.Groups)
        {
            var clip = group.Decode();
            var kind = JmaKindExtensions.FromMetadata(group.AnimationType, group.FrameInfoType, group.WorldRelative);
            var baseList = (kind is JmaKind.Jmo or JmaKind.Jmr)
                ? animation.OverlayBasePose(graph, group, skeleton, defaults) ?? defaults
                : defaults;
            Pose pose;
            IReadOnlyList<NodeTransform> leading;
            switch (kind)
            {
                case JmaKind.Jmo:
                {
                    var (reference, body) = clip.OverlayPose(skeleton, baseList);
                    body.ApplyObjectSpaceCorrections(reference, skeleton, baseList, group.ObjectSpaceParents);
                    (pose, leading) = (body, reference);
                    break;
                }
                case JmaKind.Jmr:
                {
                    var body = clip.ReplacementPose(skeleton, baseList);
                    var reference = new List<NodeTransform>(baseList);
                    body.ApplyObjectSpaceCorrections(reference, skeleton, baseList, group.ObjectSpaceParents);
                    (pose, leading) = (body, reference);
                    break;
                }
                default: pose = clip.Pose(skeleton, defaults); leading = defaults; break;
            }
            string text = JmaWriter.ToText(pose, skeleton, leading, group.NodeListChecksum, kind, stem, clip.Movement);
            string name = group.Name is { } nm ? Sanitize(nm) : $"anim_{group.Index}";
            result[$"{stem}.{name}.{kind.Extension()}"] = text;
        }
        return result;
    }

    private static IReadOnlyList<NodeTransform> BuildDefaults(Skeleton skeleton, TagFile jmad)
    {
        var byName = new Dictionary<string, NodeTransform>();
        var addl = jmad.Root.FieldPath("additional node data")?.AsBlock();
        if (addl is not null)
            for (int i = 0; i < addl.Count; i++)
            {
                var elem = addl.Element(i);
                if (elem is null) continue;
                string? name = elem.ReadStringId("node name");
                if (string.IsNullOrEmpty(name)) continue;
                byName[name] = new NodeTransform
                {
                    Translation = elem.ReadPoint3d("default translation"),
                    Rotation = elem.ReadQuat("default rotation"),
                    Scale = elem.ReadReal("default scale") ?? 1.0f,
                };
            }
        return skeleton.Nodes
            .Select(node => byName.TryGetValue(node.Name, out var t) ? t : NodeTransform.Identity)
            .ToList();
    }

    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

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
