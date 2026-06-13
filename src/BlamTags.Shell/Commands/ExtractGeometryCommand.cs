using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>extract-geometry</c> — geometry source files for a geometry-bearing
/// tag, dispatched on the input group:
/// <list type="bullet">
/// <item><c>.model</c> (hlmt): render / collision / physics source files.
///   The render side auto-picks JMS or ASS (ASS when the render_model carries
///   instance geometry — decorators, the brute, level objects); coll/phys are
///   always JMS. <c>--force {jms,ass}</c> overrides the render decision.</item>
/// <item><c>.scenario</c> (scnr): one ASS per <c>structure_bsps[]</c> entry,
///   pairing the BSP with its lighting_info (.stli). Always ASS.</item>
/// <item><c>.scenario_structure_bsp</c> (sbsp): a single ASS (no paired stli,
///   so no light objects).</item>
/// </list>
/// The <c>[KINDS...]</c> positional and <c>--force</c> are hlmt-only.
/// </summary>
public static class ExtractGeometryCommand
{
    private enum Kind { Render, Collision, Physics }

    private static string KindStr(Kind k) => k switch
    {
        Kind.Render => "render", Kind.Collision => "collision", _ => "physics",
    };

    private static string KindExt(Kind k) => k switch
    {
        Kind.Render => "render_model", Kind.Collision => "collision_model", _ => "physics_model",
    };

    private static string KindModelField(Kind k) => k switch
    {
        Kind.Render => "render model", Kind.Collision => "collision model", _ => "physics_model",
    };

    public static int Run(CliContext ctx, Args args)
    {
        string? output = args.TakeOption("--output");
        bool flat = args.TakeFlag("--flat");
        string? force = args.TakeOption("--force");
        string file = args.Positional(0) ?? throw new CliError("extract-geometry: missing <file>");

        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("extract-geometry");
        var kinds = (ctx.ReplMode ? args.Positionals : args.Positionals.Skip(1)).ToList();

        string group = GroupTag.Format(loaded.Tag.Group.Tag);
        return group switch
        {
            "hlmt" => RunHlmt(ctx, kinds, output, flat, ParseForce(force)),
            "scnr" => RunScenario(ctx, RejectHlmtOnly(kinds, force, "scenario", output), flat),
            "sbsp" => RunSbsp(ctx, RejectHlmtOnly(kinds, force, "scenario_structure_bsp", output)),
            // Halo CE references a gbxmodel (mod2) directly — no .model wrapper.
            "mod2" => RunGbxmodel(ctx, RejectHlmtOnly(kinds, force, "gbxmodel", output), flat),
            // Direct collision input: CE model_collision_geometry or H2/H3 collision_model.
            "coll" => RunCollision(ctx, RejectHlmtOnly(kinds, force, "collision_model", output), flat),
            _ => throw new CliError(
                $"extract-geometry expects `.model` (hlmt), `.gbxmodel` (mod2, Halo CE), `.collision_model`/`.model_collision_geometry` (coll), `.scenario` (scnr), or `.scenario_structure_bsp` (sbsp) — got group `{group}`."),
        };
    }

    private static string? RejectHlmtOnly(List<string> kinds, string? force, string inputKind, string? output)
    {
        if (kinds.Count > 0)
            throw new CliError(
                $"the [KINDS...] positional (render/collision/physics/all) is `.model`-only — a {inputKind} input always emits ASS over the whole scene.");
        if (force is not null)
            throw new CliError(
                $"`--force` is `.model`-only — a {inputKind} input must emit ASS (JMS has no representation for level/BSP geometry).");
        return output;
    }

    private static string? ParseForce(string? force) => force?.ToLowerInvariant() switch
    {
        null => null,
        "jms" => "jms",
        "ass" => "ass",
        var other => throw new CliError($"unknown --force `{other}`; expected `jms` or `ass`"),
    };

    //================================================================
    // hlmt → render/collision/physics
    //================================================================

    private static int RunHlmt(CliContext ctx, List<string> kinds, string? output, bool flat, string? force)
    {
        var loaded = ctx.LoadedOrThrow("extract-geometry");

        var selected = kinds.Count == 0 || kinds.Contains("all")
            ? new HashSet<Kind> { Kind.Render, Kind.Collision, Kind.Physics }
            : kinds.Select(k => k switch
            {
                "render" => (Kind?)Kind.Render,
                "collision" => Kind.Collision,
                "physics" => Kind.Physics,
                _ => null,
            }).Where(k => k is not null).Select(k => k!.Value).ToHashSet();

        string stem = Path.GetFileNameWithoutExtension(loaded.Path);
        string outRoot = output ?? ".";
        var root = loaded.Tag.Root;
        // Engine drives both the render reader (Halo 2 uses a section-based
        // structure) and the JMS version (CE 8200 / H2 8210 / H3+ 8213). The
        // .model and every tag it references share the engine.
        var game = loaded.Tag.GameOf();
        int version = game.JmsVersion();

        string? renderRef = TagRef(root, KindModelField(Kind.Render));
        string? collisionRef = TagRef(root, KindModelField(Kind.Collision));
        string? physicsRef = TagRef(root, KindModelField(Kind.Physics));

        TagFile? renderTag = renderRef is null ? null : ctx.LoadReferencedTag(renderRef, KindExt(Kind.Render));

        string? renderFormat = null;
        if (selected.Contains(Kind.Render))
        {
            string detected = renderTag is null ? "jms" : DetectRenderFormat(renderTag);
            renderFormat = force ?? detected;
        }

        bool needSkeleton = selected.Contains(Kind.Collision) || selected.Contains(Kind.Physics);
        JmsFile? renderJms = (renderTag is not null && (renderFormat == "jms" || needSkeleton))
            ? ReadRenderJms(renderTag, game) : null;
        IReadOnlyList<JmsNode>? skeleton = renderJms?.Nodes;

        var emitted = new List<(string Path, string Summary)>();
        var skipped = new List<(Kind Kind, string Reason)>();

        foreach (var kind in new[] { Kind.Render, Kind.Collision, Kind.Physics })
        {
            if (!selected.Contains(kind)) continue;
            switch (kind)
            {
                case Kind.Render:
                    if (renderTag is null) { skipped.Add((kind, "no render_model reference")); break; }
                    if (renderFormat == "ass")
                    {
                        var ass = AssFile.FromRenderModel(renderTag);
                        string path = OutputPathFor(outRoot, stem, kind, flat, "ass");
                        WriteAss(path, ass);
                        emitted.Add((path, $"[render: ASS]  {AssSummary(ass)}"));
                    }
                    else
                    {
                        var jmsR = renderJms ?? ReadRenderJms(renderTag, game);
                        string path = OutputPathFor(outRoot, stem, kind, flat, "jms");
                        WriteJms(path, jmsR, version);
                        emitted.Add((path, $"[render: JMS]  {JmsSummary(jmsR)}"));
                    }
                    break;
                case Kind.Collision:
                    if (collisionRef is null) { skipped.Add((kind, "no collision_model reference")); break; }
                    if (skeleton is null) { skipped.Add((kind, "needs render_model for skeleton")); break; }
                    {
                        var t = ctx.LoadReferencedTag(collisionRef, KindExt(kind));
                        var jms = JmsFile.FromCollisionModelWithSkeleton(t, skeleton);
                        string path = OutputPathFor(outRoot, stem, kind, flat, "jms");
                        WriteJms(path, jms, version);
                        emitted.Add((path, $"[collision] {JmsSummary(jms)}"));
                    }
                    break;
                case Kind.Physics:
                    if (physicsRef is null) { skipped.Add((kind, "no physics_model reference")); break; }
                    if (skeleton is null) { skipped.Add((kind, "needs render_model for skeleton")); break; }
                    {
                        var t = ctx.LoadReferencedTag(physicsRef, KindExt(kind));
                        // Halo 2 stores Havok shapes flat (parented by a
                        // rigid-body shape reference); Halo 3 nests them.
                        var jms = game == Game.Halo2
                            ? JmsFile.FromPhysicsModelH2WithSkeleton(t, skeleton)
                            : JmsFile.FromPhysicsModelWithSkeleton(t, skeleton);
                        string path = OutputPathFor(outRoot, stem, kind, flat, "jms");
                        WriteJms(path, jms, version);
                        emitted.Add((path, $"[physics]   {JmsSummary(jms)}"));
                    }
                    break;
            }
        }

        foreach (var (path, summary) in emitted) Console.WriteLine($"{path}: {summary}");
        foreach (var (kind, reason) in skipped) Console.Error.WriteLine($"skipped {KindStr(kind)}: {reason}");
        if (emitted.Count == 0) throw new CliError("nothing emitted — all selected kinds were skipped");
        return 0;
    }

    // mod2 → Halo CE gbxmodel render geometry → JMS (version 8200)
    private static int RunGbxmodel(CliContext ctx, string? output, bool flat)
    {
        var loaded = ctx.LoadedOrThrow("extract-geometry");
        int version = loaded.Tag.GameOf().JmsVersion();
        var jms = JmsFile.FromGbxmodel(loaded.Tag);
        string stem = Path.GetFileNameWithoutExtension(loaded.Path);
        string outRoot = output ?? ".";
        string path = OutputPathFor(outRoot, stem, Kind.Render, flat, "jms");
        WriteJms(path, jms, version);
        Console.WriteLine($"{path}: [render: JMS] {JmsSummary(jms)}");
        return 0;
    }

    /// <summary>Build the render-model JMS with the engine-correct reader.</summary>
    private static JmsFile ReadRenderJms(TagFile tag, Game game) => game switch
    {
        Game.Halo1 => JmsFile.FromGbxmodel(tag),
        Game.Halo2 => JmsFile.FromH2RenderModel(tag),
        _ => JmsFile.FromRenderModel(tag),
    };

    // coll → direct collision (CE model_collision_geometry / H2+H3 collision_model)
    private static int RunCollision(CliContext ctx, string? output, bool flat)
    {
        var loaded = ctx.LoadedOrThrow("extract-geometry");
        var game = loaded.Tag.GameOf();
        var jms = game == Game.Halo1
            ? JmsFile.FromModelCollisionGeometry(loaded.Tag)
            : JmsFile.FromCollisionModel(loaded.Tag);
        string stem = Path.GetFileNameWithoutExtension(loaded.Path);
        string path = OutputPathFor(output ?? ".", stem, Kind.Collision, flat, "jms");
        WriteJms(path, jms, game.JmsVersion());
        Console.WriteLine($"{path}: [collision] {JmsSummary(jms)}");
        return 0;
    }

    private static string DetectRenderFormat(TagFile tag)
    {
        var root = tag.Root;
        long? instanceMeshIndex = root.Field("instance mesh index")?.Value switch
        {
            TagFieldData.LongBlockIndex n => n.Value,
            TagFieldData.CustomLongBlockIndex n => n.Value,
            TagFieldData.ShortBlockIndex n => n.Value,
            TagFieldData.LongInteger n => n.Value,
            _ => null,
        };
        int placementsLen = root.Field("instance placements")?.AsBlock()?.Count ?? 0;
        return (instanceMeshIndex ?? -1) >= 0 && placementsLen > 0 ? "ass" : "jms";
    }

    //================================================================
    // scnr → per-BSP ASS, sbsp → single ASS
    //================================================================

    private static int RunScenario(CliContext ctx, string? output, bool flat)
    {
        var loaded = ctx.LoadedOrThrow("extract-geometry");
        string scenarioStem = Path.GetFileNameWithoutExtension(loaded.Path);
        string outRoot = output ?? ".";

        var bspsBlock = loaded.Tag.Root.FieldPath("structure bsps")?.AsBlock()
            ?? throw new CliError("scenario has no `structure bsps` block");
        if (bspsBlock.IsEmpty) throw new CliError("scenario has zero structure_bsps entries — nothing to extract");

        var emitted = new List<(string Path, string Summary)>();
        var warnings = new List<string>();

        for (int bi = 0; bi < bspsBlock.Count; bi++)
        {
            var entry = bspsBlock.Element(bi)!;
            string? bspRel = TagRef(entry, "structure bsp");
            string? lightingRel = TagRef(entry, "structure lighting_info");
            if (bspRel is null) { warnings.Add($"structure_bsps[{bi}]: no structure_bsp ref — skipped"); continue; }

            TagFile bspTag;
            try { bspTag = ctx.LoadReferencedTag(bspRel, "scenario_structure_bsp"); }
            catch (Exception e) { warnings.Add($"structure_bsps[{bi}]: read `{bspRel}` failed — {e.Message}"); continue; }

            var ass = AssFile.FromScenarioStructureBsp(bspTag);
            if (lightingRel is not null)
            {
                try { ass.AddLightsFromStli(ctx.LoadReferencedTag(lightingRel, "scenario_structure_lighting_info")); }
                catch (Exception e) { warnings.Add($"structure_bsps[{bi}]: lighting tag `{lightingRel}` unreadable — {e.Message}"); }
            }
            else warnings.Add($"structure_bsps[{bi}]: no lighting_info ref — emitting without lights");

            string bspStem = bspRel.Split('\\').LastOrDefault() ?? "bsp";
            string path = flat
                ? Path.Combine(outRoot, $"{scenarioStem}.{bspStem}.ass")
                : Path.Combine(outRoot, scenarioStem, "structure", $"{bspStem}.ASS");
            WriteAss(path, ass);
            emitted.Add((path, $"[bsp{bi}] {AssSummary(ass)}"));
        }

        foreach (var (path, summary) in emitted) Console.WriteLine($"{path}: {summary}");
        foreach (var w in warnings) Console.Error.WriteLine($"warning: {w}");
        if (emitted.Count == 0) throw new CliError("no ASS files emitted — all structure_bsps entries failed to load");
        return 0;
    }

    private static int RunSbsp(CliContext ctx, string? output)
    {
        var loaded = ctx.LoadedOrThrow("extract-geometry");
        string stem = Path.GetFileNameWithoutExtension(loaded.Path);
        string outRoot = output ?? ".";
        var game = loaded.Tag.GameOf();

        // Halo CE compiles levels from JMS, not ASS — emit render + collision
        // JMS for a CE structure_bsp. H2/H3 emit ASS.
        if (game == Game.Halo1)
        {
            int version = game.JmsVersion();
            var renderJms = JmsFile.FromScenarioStructureBspCe(loaded.Tag);
            string rpath = OutputPathFor(outRoot, stem, Kind.Render, false, "jms");
            WriteJms(rpath, renderJms, version);
            Console.WriteLine($"{rpath}: [sbsp render: JMS] {JmsSummary(renderJms)}");
            var collJms = JmsFile.FromScenarioStructureBspCeCollision(loaded.Tag);
            string cpath = OutputPathFor(outRoot, stem, Kind.Collision, false, "jms");
            WriteJms(cpath, collJms, version);
            Console.WriteLine($"{cpath}: [sbsp collision: JMS] {JmsSummary(collJms)}");
            return 0;
        }

        // H2 → ASS v2 (section-based geometry); H3 → ASS v7.
        int assVersion = game.AssVersion() ?? 7;
        string path = Path.Combine(outRoot, $"{stem}.ASS");
        var ass = game == Game.Halo2
            ? AssFile.FromScenarioStructureBspH2(loaded.Tag)
            : AssFile.FromScenarioStructureBsp(loaded.Tag);
        WriteAss(path, ass, assVersion);
        Console.WriteLine($"{path}: [sbsp] {AssSummary(ass)} (no lighting — pass scenario for lights)");
        return 0;
    }

    //================================================================
    // helpers
    //================================================================

    private static string OutputPathFor(string outRoot, string stem, Kind kind, bool flat, string ext) => flat
        ? Path.Combine(outRoot, $"{stem}.{KindStr(kind)}.{ext}")
        : Path.Combine(outRoot, stem, KindStr(kind), $"{stem}.{ext.ToUpperInvariant()}");

    private static void WriteJms(string path, JmsFile jms, int version = 8213)
    {
        EnsureParent(path);
        using var fs = File.Create(path);
        jms.Write(fs, version);
    }

    private static void WriteAss(string path, AssFile ass, int version = 7)
    {
        EnsureParent(path);
        using var fs = File.Create(path);
        ass.Write(fs, version);
    }

    private static void EnsureParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static string? TagRef(TagStruct root, string field)
    {
        string? p = root.ReadTagRefPath(field);
        return string.IsNullOrEmpty(p) ? null : p;
    }

    private static string JmsSummary(JmsFile j)
    {
        var parts = new List<string>();
        void Add(int n, string label) { if (n > 0) parts.Add($"{n} {label}"); }
        Add(j.Nodes.Count, "nodes");
        Add(j.Materials.Count, "mats");
        Add(j.Markers.Count, "markers");
        Add(j.Vertices.Count, "verts");
        Add(j.Triangles.Count, "tris");
        Add(j.Spheres.Count, "spheres");
        Add(j.Boxes.Count, "boxes");
        Add(j.Capsules.Count, "capsules");
        Add(j.ConvexShapes.Count, "convex");
        Add(j.Ragdolls.Count, "ragdolls");
        Add(j.Hinges.Count, "hinges");
        return string.Join(", ", parts);
    }

    private static string AssSummary(AssFile a)
    {
        int verts = a.Objects.Sum(o => o.VerticesLen);
        int tris = a.Objects.Sum(o => o.TrianglesLen);
        int lights = a.Objects.Count(o => o.Payload is AssObjectPayload.GenericLight);
        return $"{a.Materials.Count} mats, {a.Objects.Count} objects ({lights} lights), {a.Instances.Count} instances, {verts} verts, {tris} tris";
    }
}
