using System.Globalization;
using System.Text;

namespace BlamTags;

/// <summary>
/// ASS (Bungie Amalgam) static-scene export — a port of the Rust
/// <c>ass.rs</c>. ASS is the level-geometry counterpart to JMS (same
/// family, authored for static scene structure). H3 targets version 7;
/// sections HEADER / MATERIALS / OBJECTS / INSTANCES.
///
/// <see cref="FromScenarioStructureBsp"/> reconstructs a full scene from a
/// <c>scenario_structure_bsp</c> (clusters, instanced geometry, portals,
/// weather polyhedra, markers, environment objects, collision BSP).
/// <see cref="FromRenderModel"/> handles render_models with instance
/// geometry (the brute / decorators / level objects — JMS has no INSTANCES
/// section to carry per-placement transforms). <see cref="AddLightsFromStli"/>
/// layers real lighting + per-material BM_LIGHTING_* metadata from a paired
/// <c>.stli</c> tag. Shares <see cref="Geometry"/> with the JMS path.
/// </summary>
public sealed class AssFile
{
    public string HeaderTool { get; init; } = "";
    public string HeaderToolVersion { get; init; } = "";
    public string HeaderUser { get; init; } = "";
    public string HeaderMachine { get; init; } = "";
    public List<AssMaterial> Materials { get; init; } = new();
    public List<AssObject> Objects { get; init; } = new();
    public List<AssInstance> Instances { get; init; } = new();

    //================================================================
    // scenario_structure_bsp
    //================================================================

    public static AssFile FromScenarioStructureBsp(TagFile tag)
    {
        var root = tag.Root;
        var materials = ReadMaterials(root);

        var clusters = root.FieldPath("clusters")?.AsBlock() ?? throw Missing("clusters");
        var meshes = root.FieldPath("render geometry/meshes")?.AsBlock()
            ?? throw Missing("render geometry/meshes");
        var pmt = root.FieldPath("render geometry/per mesh temporary")?.AsBlock()
            ?? throw Missing("render geometry/per mesh temporary");

        var objects = new List<AssObject>();
        var instances = new List<AssInstance>();

        // INSTANCE 0 is always "Scene Root".
        instances.Add(new AssInstance { ObjectIndex = -1, Name = "Scene Root", UniqueId = 0, ParentId = -1 });

        // Clusters → MESH OBJECTs at origin (already in world units).
        var clusterBounds = Geometry.CompressionBounds.Identity();
        for (int ci = 0; ci < clusters.Count; ci++)
        {
            var cluster = clusters.Element(ci)!;
            long meshIdx = cluster.ReadIntAny("mesh index") ?? -1;
            if (meshIdx < 0 || meshIdx >= meshes.Count) continue;
            if (meshIdx >= pmt.Count) continue;
            var mesh = meshes.Element((int)meshIdx)!;
            var meshPmt = pmt.Element((int)meshIdx)!;
            var obj = BuildClusterObject(mesh, meshPmt, clusterBounds, false);
            if (obj.VerticesLen == 0) continue;
            int objectIndex = objects.Count;
            objects.Add(obj);
            instances.Add(new AssInstance { ObjectIndex = objectIndex, Name = $"cluster_{ci}", UniqueId = instances.Count, ParentId = 0 });
        }

        // Cluster portals → `+portal_N` MESHes (fan-triangulated).
        int portalMatIdx = EnsureSpecialMaterial(materials, "+portal");
        var portals = root.FieldPath("cluster portals")?.AsBlock();
        if (portals is not null)
        {
            for (int pi = 0; pi < portals.Count; pi++)
            {
                var portal = portals.Element(pi)!;
                var vertsBlock = portal.Field("vertices")?.AsBlock();
                if (vertsBlock is null || vertsBlock.Count < 3) continue;
                var verts = new List<AssVertex>(vertsBlock.Count);
                for (int vi = 0; vi < vertsBlock.Count; vi++)
                {
                    var p = vertsBlock.Element(vi)!.ReadPoint3d("point");
                    verts.Add(NewVertex(p.Mul(Geometry.Scale)));
                }
                var tris = new List<AssTriangle>(System.Math.Max(0, verts.Count - 2));
                for (int k = 1; k < verts.Count - 1; k++)
                    tris.Add(new AssTriangle(portalMatIdx, 0, (uint)k, (uint)k + 1));
                int objectIndex = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) });
                instances.Add(new AssInstance { ObjectIndex = objectIndex, Name = $"+portal_{pi}", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        // Instanced geometry definitions → OBJECTs (content-deduped) +
        // placements → INSTANCEs.
        var defs = root.FieldPath("resource interface/raw_resources[0]/raw_items/instanced geometries definitions")?.AsBlock();
        var instBlock = root.FieldPath("instanced geometry instances")?.AsBlock();
        if (defs is not null && instBlock is not null)
        {
            var defObjectIndex = new int?[defs.Count];
            var contentToObjectIndex = new Dictionary<string, int>();
            for (int di = 0; di < defs.Count; di++)
            {
                var def = defs.Element(di)!;
                long meshIdx = def.ReadIntAny("mesh index") ?? -1;
                int compIdx = (int)System.Math.Max(0, def.ReadIntAny("compression index") ?? 0);
                if (meshIdx < 0 || meshIdx >= meshes.Count || meshIdx >= pmt.Count) continue;
                var bounds = Geometry.ReadCompressionBoundsAt(root, compIdx);
                bool flipWinding = ComputeAxisFlip(bounds);
                var mesh = meshes.Element((int)meshIdx)!;
                var meshPmt = pmt.Element((int)meshIdx)!;
                var obj = BuildClusterObject(mesh, meshPmt, bounds, flipWinding);
                if (obj.VerticesLen == 0) continue;
                string key = ObjectContentKey(obj);
                if (contentToObjectIndex.TryGetValue(key, out int existing))
                {
                    defObjectIndex[di] = existing;
                }
                else
                {
                    int idx = objects.Count;
                    contentToObjectIndex[key] = idx;
                    defObjectIndex[di] = idx;
                    objects.Add(obj);
                }
            }
            for (int ii = 0; ii < instBlock.Count; ii++)
            {
                var inst = instBlock.Element(ii)!;
                long defIdx = inst.ReadIntAny("instance definition") ?? -1;
                if (defIdx < 0 || defIdx >= defObjectIndex.Length) continue;
                if (defObjectIndex[(int)defIdx] is not int objectIndex) continue;
                float scale = inst.ReadReal("scale") ?? 1.0f;
                var f = inst.ReadVec3("forward");
                var l = inst.ReadVec3("left");
                var u = inst.ReadVec3("up");
                var p = inst.ReadPoint3d("position");
                var rot = MathExtensions.FromBasisColumns(f, l, u);
                string name = inst.ReadStringId("name") ?? $"instance_{ii}";
                instances.Add(new AssInstance
                {
                    ObjectIndex = objectIndex,
                    Name = name,
                    UniqueId = instances.Count,
                    ParentId = 0,
                    LocalRotation = rot,
                    LocalTranslation = p.Mul(Geometry.Scale),
                    LocalScale = scale,
                });
            }
        }

        // Weather polyhedra → `+weather_N` MESHes (convex hull from planes).
        int weatherMatIdx = EnsureSpecialMaterial(materials, "+weather");
        var wpBlock = root.FieldPath("weather polyhedra")?.AsBlock();
        if (wpBlock is not null)
        {
            for (int wi = 0; wi < wpBlock.Count; wi++)
            {
                var wp = wpBlock.Element(wi)!;
                var planesBlock = wp.Field("planes")?.AsBlock();
                if (planesBlock is null) continue;
                var planes = new List<RealPlane3d>(planesBlock.Count);
                for (int pi = 0; pi < planesBlock.Count; pi++)
                    planes.Add(planesBlock.Element(pi)!.ReadPlane3d("plane"));
                if (planes.Count < 4) continue;
                var (verts, tris) = PolyhedronFromPlanes(planes, weatherMatIdx);
                if (verts.Count == 0) continue;
                int objectIndex = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) });
                instances.Add(new AssInstance { ObjectIndex = objectIndex, Name = $"+weather_{wi}", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        // sbsp markers → SPHERE primitives.
        var markersBlock = root.FieldPath("markers")?.AsBlock();
        if (markersBlock is not null)
        {
            for (int mi = 0; mi < markersBlock.Count; mi++)
            {
                var m = markersBlock.Element(mi)!;
                string name = m.ReadStringId("name") ?? $"marker_{mi}";
                var pos = m.ReadPoint3d("position");
                var rot = m.ReadQuat("rotation");
                int objectIndex = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Sphere(-1, 10.0f) });
                instances.Add(new AssInstance
                {
                    ObjectIndex = objectIndex,
                    Name = name,
                    UniqueId = instances.Count,
                    ParentId = 0,
                    LocalRotation = rot,
                    LocalTranslation = pos.Mul(Geometry.Scale),
                });
            }
        }

        // Environment objects → xref-only OBJECTs + placement INSTANCEs.
        var envObjects = root.FieldPath("environment objects")?.AsBlock();
        var envPalette = root.FieldPath("environment object palette")?.AsBlock();
        if (envObjects is not null && envPalette is not null)
        {
            var paletteObjectIndex = new int?[envPalette.Count];
            for (int pi = 0; pi < envPalette.Count; pi++)
            {
                var pal = envPalette.Element(pi)!;
                string xref = pal.ReadTagRefPath("object") ?? "";
                if (xref.Length == 0) continue;
                string xrefName = FileStem(xref, "env_object");
                paletteObjectIndex[pi] = objects.Count;
                objects.Add(new AssObject
                {
                    XrefFilepath = xref,
                    XrefObjectname = xrefName,
                    Payload = new AssObjectPayload.Mesh(new(), new()),
                });
            }
            for (int ei = 0; ei < envObjects.Count; ei++)
            {
                var placement = envObjects.Element(ei)!;
                long pi = placement.ReadIntAny("palette index") ?? -1;
                if (pi < 0 || pi >= paletteObjectIndex.Length) continue;
                if (paletteObjectIndex[(int)pi] is not int objectIndex) continue;
                var pos = placement.ReadPoint3d("position");
                var rot = placement.ReadQuat("rotation");
                float scale = placement.ReadReal("scale") ?? 1.0f;
                string name = placement.ReadStringId("name") ?? $"env_object_{ei}";
                instances.Add(new AssInstance
                {
                    ObjectIndex = objectIndex,
                    Name = name,
                    UniqueId = instances.Count,
                    ParentId = 0,
                    LocalRotation = rot,
                    LocalTranslation = pos.Mul(Geometry.Scale),
                    LocalScale = scale,
                });
            }
        }

        // Structure collision BSP → single `@CollideOnly` MESH.
        var collBlock = root.FieldPath("resource interface/raw_resources[0]/raw_items/collision bsp")?.AsBlock();
        if (collBlock is not null)
        {
            int collMatIdx = EnsureSpecialMaterial(materials, "@collision_only");
            var collVerts = new List<AssVertex>();
            var collTris = new List<AssTriangle>();
            uint nextIndex = 0;
            for (int ci = 0; ci < collBlock.Count; ci++)
            {
                var bsp = collBlock.Element(ci)!;
                var surfaces = bsp.Field("surfaces")?.AsBlock();
                var edges = bsp.Field("edges")?.AsBlock();
                var bspVerts = bsp.Field("vertices")?.AsBlock();
                if (surfaces is null || edges is null || bspVerts is null) continue;
                var edgeCache = new List<Geometry.EdgeRow>(edges.Count);
                for (int k = 0; k < edges.Count; k++)
                {
                    var e = edges.Element(k)!;
                    edgeCache.Add(new Geometry.EdgeRow(
                        (int)(e.ReadIntAny("start vertex") ?? -1),
                        (int)(e.ReadIntAny("end vertex") ?? -1),
                        (int)(e.ReadIntAny("forward edge") ?? -1),
                        (int)(e.ReadIntAny("reverse edge") ?? -1),
                        (int)(e.ReadIntAny("left surface") ?? -1),
                        (int)(e.ReadIntAny("right surface") ?? -1)));
                }
                var bspPoints = new RealPoint3d[bspVerts.Count];
                for (int k = 0; k < bspVerts.Count; k++)
                    bspPoints[k] = bspVerts.Element(k)!.ReadPoint3d("point").Mul(Geometry.Scale);
                for (int si = 0; si < surfaces.Count; si++)
                {
                    var surface = surfaces.Element(si)!;
                    int firstEdge = (int)(surface.ReadIntAny("first edge") ?? -1);
                    if (firstEdge < 0) continue;
                    var polygon = Geometry.WalkSurfaceRing(si, firstEdge, edgeCache);
                    if (polygon.Count < 3) continue;
                    uint baseForFan = nextIndex;
                    foreach (int vi in polygon)
                    {
                        var pos = vi >= 0 && vi < bspPoints.Length ? bspPoints[vi] : default;
                        collVerts.Add(NewVertex(pos));
                    }
                    uint n = (uint)polygon.Count;
                    for (uint k = 1; k < n - 1; k++)
                        collTris.Add(new AssTriangle(collMatIdx, baseForFan, baseForFan + k, baseForFan + k + 1));
                    nextIndex += n;
                }
            }
            if (collVerts.Count > 0)
            {
                int objectIndex = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(collVerts, collTris) });
                instances.Add(new AssInstance { ObjectIndex = objectIndex, Name = "@CollideOnly", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        return new AssFile
        {
            HeaderTool = "blam-tags",
            HeaderToolVersion = "0.1",
            HeaderUser = "blam-tag-shell",
            HeaderMachine = "",
            Materials = materials,
            Objects = objects,
            Instances = instances,
        };
    }

    //================================================================
    // render_model (instance geometry)
    //================================================================

    public static AssFile FromRenderModel(TagFile tag)
    {
        var root = tag.Root;
        var materials = new List<AssMaterial>();
        var objects = new List<AssObject>();
        var instances = new List<AssInstance>();

        instances.Add(new AssInstance { ObjectIndex = -1, Name = "Scene Root", UniqueId = 0, ParentId = -1 });

        var nodes = ReadRmNodesLocal(root);
        var nodeToInstance = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) nodeToInstance[i] = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            int parentInst = n.Parent < 0 ? 0
                : (n.Parent < nodeToInstance.Length ? nodeToInstance[n.Parent] : 0);
            if (parentInst < 0) parentInst = 0;
            int instIdx = instances.Count;
            instances.Add(new AssInstance
            {
                ObjectIndex = -1,
                Name = n.Name,
                UniqueId = instIdx,
                ParentId = parentInst,
                LocalRotation = n.Rotation,
                LocalTranslation = n.Translation.Mul(Geometry.Scale),
            });
            nodeToInstance[i] = instIdx;
        }

        var bounds = Geometry.ReadCompressionBounds(root);
        var meshes = root.FieldPath("render geometry/meshes")?.AsBlock()
            ?? throw Missing("render geometry/meshes");
        var pmt = root.FieldPath("render geometry/per mesh temporary")?.AsBlock()
            ?? throw Missing("render geometry/per mesh temporary");
        var matsBlock = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var regionsBlock = root.FieldPath("regions")?.AsBlock() ?? throw Missing("regions");

        for (int ri = 0; ri < regionsBlock.Count; ri++)
        {
            var region = regionsBlock.Element(ri)!;
            string regionName = region.ReadStringId("name") ?? "";
            var perms = region.Field("permutations")?.AsBlock();
            if (perms is null) continue;
            for (int pi = 0; pi < perms.Count; pi++)
            {
                var perm = perms.Element(pi)!;
                string permName = perm.ReadStringId("name") ?? "";
                long meshIdx = perm.ReadIntAny("mesh index") ?? -1;
                long meshCount = perm.ReadIntAny("mesh count") ?? 0;
                if (meshIdx < 0 || meshCount <= 0) continue;
                for (int off = 0; off < meshCount; off++)
                {
                    int mi = (int)meshIdx + off;
                    if (mi >= meshes.Count || mi >= pmt.Count) continue;
                    var mesh = meshes.Element(mi)!;
                    var meshPmt = pmt.Element(mi)!;
                    string cellLabel = $"{permName} {regionName}";
                    var obj = BuildRenderModelObject(mesh, meshPmt, matsBlock, bounds, materials, cellLabel);
                    if (obj.VerticesLen == 0) continue;
                    int objectIndex = objects.Count;
                    objects.Add(obj);
                    long? rigidNode = mesh.ReadIntAny("rigid node index") is { } rn && rn >= 0 ? rn : null;
                    int parentInst = 0;
                    if (rigidNode is { } node && node >= 0 && node < nodeToInstance.Length && nodeToInstance[node] >= 0)
                        parentInst = nodeToInstance[node];
                    instances.Add(new AssInstance
                    {
                        ObjectIndex = objectIndex,
                        Name = $"{regionName}:{permName}",
                        UniqueId = instances.Count,
                        ParentId = parentInst,
                    });
                }
            }
        }

        long instanceMeshIndex = root.ReadIntAny("instance mesh index") ?? -1;
        if (instanceMeshIndex >= 0)
        {
            int imi = (int)instanceMeshIndex;
            if (imi < meshes.Count && imi < pmt.Count)
            {
                var placements = root.Field("instance placements")?.AsBlock();
                if (placements is not null && !placements.IsEmpty)
                {
                    var mesh = meshes.Element(imi)!;
                    var meshPmt = pmt.Element(imi)!;
                    var obj = BuildRenderModelObject(mesh, meshPmt, matsBlock, bounds, materials, "instance_mesh");
                    int? imiObjectIndex = null;
                    if (obj.VerticesLen > 0)
                    {
                        imiObjectIndex = objects.Count;
                        objects.Add(obj);
                    }
                    if (imiObjectIndex is int objectIndex)
                    {
                        for (int ii = 0; ii < placements.Count; ii++)
                        {
                            var placement = placements.Element(ii)!;
                            string name = placement.ReadStringId("name") ?? $"instance_{ii}";
                            int nodeIdx = (int)(placement.ReadIntAny("node_index") ?? -1);
                            float scale = placement.ReadReal("scale") ?? 1.0f;
                            var f = placement.ReadVec3("forward");
                            var l = placement.ReadVec3("left");
                            var u = placement.ReadVec3("up");
                            var p = placement.ReadPoint3d("position");
                            var rot = MathExtensions.FromBasisColumns(f, l, u);
                            int parentInst = nodeIdx >= 0 && nodeIdx < nodeToInstance.Length && nodeToInstance[nodeIdx] >= 0
                                ? nodeToInstance[nodeIdx] : 0;
                            instances.Add(new AssInstance
                            {
                                ObjectIndex = objectIndex,
                                Name = name,
                                UniqueId = instances.Count,
                                ParentId = parentInst,
                                LocalRotation = rot,
                                LocalTranslation = p.Mul(Geometry.Scale),
                                LocalScale = scale,
                            });
                        }
                    }
                }
            }
        }

        var groups = root.Field("marker groups")?.AsBlock();
        if (groups is not null)
        {
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups.Element(gi)!;
                string groupName = group.ReadStringId("name") ?? "";
                var markers = group.Field("markers")?.AsBlock();
                if (markers is null) continue;
                for (int mi = 0; mi < markers.Count; mi++)
                {
                    var m = markers.Element(mi)!;
                    int nodeIdx = (int)(m.ReadIntAny("node index") ?? -1);
                    var translation = m.ReadPoint3d("translation");
                    var rotation = m.ReadQuat("rotation");
                    float radius = m.ReadReal("scale") ?? 0.01f;
                    int objectIndex = objects.Count;
                    objects.Add(new AssObject { Payload = new AssObjectPayload.Sphere(-1, radius * Geometry.Scale) });
                    int parentInst = nodeIdx >= 0 && nodeIdx < nodeToInstance.Length && nodeToInstance[nodeIdx] >= 0
                        ? nodeToInstance[nodeIdx] : 0;
                    instances.Add(new AssInstance
                    {
                        ObjectIndex = objectIndex,
                        Name = $"#{groupName}",
                        UniqueId = instances.Count,
                        ParentId = parentInst,
                        LocalRotation = rotation,
                        LocalTranslation = translation.Mul(Geometry.Scale),
                    });
                }
            }
        }

        return new AssFile
        {
            HeaderTool = "MAX",
            HeaderToolVersion = "8.0",
            HeaderUser = "blam-tags",
            HeaderMachine = "",
            Materials = materials,
            Objects = objects,
            Instances = instances,
        };
    }

    //================================================================
    // Halo 2 scenario_structure_bsp → ASS v2
    //================================================================

    /// <summary>Reconstruct the ASS scene for a Halo 2 structure_bsp. H2 keeps
    /// geometry inline (not in a raw-resource): cluster geom under
    /// <c>cluster data[0]/section</c>, instanced geom under <c>render info/
    /// render data[0]/section</c>, collision in the top-level <c>collision
    /// bsp</c>. Emitted ASS targets version 2.</summary>
    public static AssFile FromScenarioStructureBspH2(TagFile tag)
    {
        var root = tag.Root;
        var materials = ReadMaterialsH2(root);
        int materialsCount = materials.Count;
        var objects = new List<AssObject>();
        var instances = new List<AssInstance>();

        // INSTANCE 0 = Scene Root.
        instances.Add(new AssInstance { ObjectIndex = -1, Name = "Scene Root", UniqueId = 0, ParentId = -1 });

        // Clusters → MESH OBJECTs at origin.
        var clusters = root.FieldPath("clusters")?.AsBlock() ?? throw Missing("clusters");
        for (int ci = 0; ci < clusters.Count; ci++)
        {
            var section = clusters.Element(ci)!.FieldPath("cluster data[0]/section")?.AsStruct();
            if (section is null) continue;
            var obj = BuildH2SectionObject(section, materialsCount);
            if (obj.VerticesLen == 0) continue;
            int oi = objects.Count; objects.Add(obj);
            instances.Add(new AssInstance { ObjectIndex = oi, Name = $"cluster_{ci}", UniqueId = instances.Count, ParentId = 0 });
        }

        // Cluster portals → `+portal`-named MESHes.
        int portalMat = EnsureSpecialMaterial(materials, "+portal");
        var portals = root.FieldPath("cluster portals")?.AsBlock();
        if (portals is not null)
        {
            for (int pi = 0; pi < portals.Count; pi++)
            {
                var vb = portals.Element(pi)!.Field("vertices")?.AsBlock();
                if (vb is null || vb.Count < 3) continue;
                var verts = new List<AssVertex>(vb.Count);
                for (int vi = 0; vi < vb.Count; vi++)
                {
                    var p = vb.Element(vi)!.ReadPointOrVec("point");
                    verts.Add(new AssVertex { Position = p.Mul(Geometry.Scale), Normal = new RealVector3d(0, 0, 1), Uvs = new() { default } });
                }
                var tris = new List<AssTriangle>();
                for (int k = 1; k < verts.Count - 1; k++) tris.Add(new AssTriangle(portalMat, 0, (uint)k, (uint)k + 1));
                int oi = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) });
                instances.Add(new AssInstance { ObjectIndex = oi, Name = $"+portal_{pi}", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        // Instanced geometries → one MESH per definition (content-deduped).
        var defs = root.FieldPath("instanced geometries definitions")?.AsBlock();
        var instBlock = root.FieldPath("instanced geometry instances")?.AsBlock();
        if (defs is not null && instBlock is not null)
        {
            var defObjectIndex = new int?[defs.Count];
            var contentToIndex = new Dictionary<string, int>();
            for (int di = 0; di < defs.Count; di++)
            {
                var section = defs.Element(di)!.FieldPath("render info/render data[0]/section")?.AsStruct();
                if (section is null) continue;
                var obj = BuildH2SectionObject(section, materialsCount);
                if (obj.VerticesLen == 0) continue;
                string key = ObjectContentKey(obj);
                if (contentToIndex.TryGetValue(key, out int existing)) defObjectIndex[di] = existing;
                else { int idx = objects.Count; contentToIndex[key] = idx; defObjectIndex[di] = idx; objects.Add(obj); }
            }
            for (int ii = 0; ii < instBlock.Count; ii++)
            {
                var inst = instBlock.Element(ii)!;
                long defIdx = inst.ReadIntAny("instance definition") ?? -1;
                if (defIdx < 0 || defIdx >= defObjectIndex.Length || defObjectIndex[defIdx] is not int oi) continue;
                var rot = MathExtensions.FromBasisColumns(inst.ReadVec3("forward"), inst.ReadVec3("left"), inst.ReadVec3("up"));
                instances.Add(new AssInstance
                {
                    ObjectIndex = oi,
                    Name = inst.ReadStringId("name") ?? $"instance_{ii}",
                    UniqueId = instances.Count, ParentId = 0,
                    LocalRotation = rot,
                    LocalTranslation = inst.ReadPointOrVec("position").Mul(Geometry.Scale),
                    LocalScale = inst.ReadReal("scale") ?? 1.0f,
                });
            }
        }

        // Weather polyhedra → `+weather`-named hull MESHes.
        int weatherMat = EnsureSpecialMaterial(materials, "+weather");
        var wpBlock = root.FieldPath("weather polyhedra")?.AsBlock();
        if (wpBlock is not null)
        {
            for (int wi = 0; wi < wpBlock.Count; wi++)
            {
                var planesBlock = wpBlock.Element(wi)!.Field("planes")?.AsBlock();
                if (planesBlock is null) continue;
                var planes = new List<RealPlane3d>(planesBlock.Count);
                for (int pi = 0; pi < planesBlock.Count; pi++) planes.Add(planesBlock.Element(pi)!.ReadPlane3d("plane"));
                if (planes.Count < 4) continue;
                var (verts, tris) = PolyhedronFromPlanes(planes, weatherMat);
                if (verts.Count == 0) continue;
                int oi = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) });
                instances.Add(new AssInstance { ObjectIndex = oi, Name = $"+weather_{wi}", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        // Markers → SPHERE primitives.
        var markersBlock = root.FieldPath("markers")?.AsBlock();
        if (markersBlock is not null)
        {
            for (int mi = 0; mi < markersBlock.Count; mi++)
            {
                var m = markersBlock.Element(mi)!;
                int oi = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Sphere(-1, 10.0f) });
                instances.Add(new AssInstance
                {
                    ObjectIndex = oi, Name = m.ReadString("name") ?? $"marker_{mi}",
                    UniqueId = instances.Count, ParentId = 0,
                    LocalRotation = m.ReadQuat("rotation"),
                    LocalTranslation = m.ReadPointOrVec("position").Mul(Geometry.Scale),
                });
            }
        }

        // Environment objects → XREF OBJECTs.
        var envObjects = root.FieldPath("environment objects")?.AsBlock();
        var envPalette = root.FieldPath("environment object palette")?.AsBlock();
        if (envObjects is not null && envPalette is not null)
        {
            var paletteObjectIndex = new int?[envPalette.Count];
            for (int pi = 0; pi < envPalette.Count; pi++)
            {
                var pal = envPalette.Element(pi)!;
                string xref = pal.ReadTagRefPath("definition") ?? pal.ReadTagRefPath("object") ?? "";
                if (string.IsNullOrEmpty(xref)) continue;
                paletteObjectIndex[pi] = objects.Count;
                objects.Add(new AssObject { XrefFilepath = xref, XrefObjectname = FileStem(xref, "env_object"), Payload = new AssObjectPayload.Mesh(new(), new()) });
            }
            for (int ei = 0; ei < envObjects.Count; ei++)
            {
                var placement = envObjects.Element(ei)!;
                long pi = placement.ReadIntAny("palette_index") ?? placement.ReadIntAny("palette index") ?? -1;
                if (pi < 0 || pi >= paletteObjectIndex.Length || paletteObjectIndex[pi] is not int oi) continue;
                instances.Add(new AssInstance
                {
                    ObjectIndex = oi, Name = placement.ReadString("name") ?? $"env_object_{ei}",
                    UniqueId = instances.Count, ParentId = 0,
                    LocalRotation = placement.ReadQuat("rotation"),
                    LocalTranslation = placement.ReadPointOrVec("translation").Mul(Geometry.Scale),
                });
            }
        }

        // Structure collision BSP → single `@CollideOnly` MESH.
        var collBlock = root.FieldPath("collision bsp")?.AsBlock();
        if (collBlock is not null)
        {
            int collMat = EnsureSpecialMaterial(materials, "@collision_only");
            var (verts, tris) = BuildH2CollisionMesh(collBlock, collMat);
            if (verts.Count > 0)
            {
                int oi = objects.Count;
                objects.Add(new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) });
                instances.Add(new AssInstance { ObjectIndex = oi, Name = "@CollideOnly", UniqueId = instances.Count, ParentId = 0 });
            }
        }

        return new AssFile
        {
            HeaderTool = "blam-tags", HeaderToolVersion = "0.1", HeaderUser = "blam-tag-shell", HeaderMachine = "",
            Materials = materials, Objects = objects, Instances = instances,
        };
    }

    private static List<AssMaterial> ReadMaterialsH2(TagStruct root)
    {
        var block = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var outList = new List<AssMaterial>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var m = block.Element(i)!;
            string path = m.ReadTagRefPath("shader") ?? m.ReadTagRefPath("old shader") ?? "";
            outList.Add(new AssMaterial { Name = FileStem(path, "default"), LightmapVariant = "", BmStrings = new() });
        }
        return outList;
    }

    /// <summary>Build a MESH from an H2 sbsp section. Unlike H2 render_model
    /// (triangle strips with 0xFFFF restart), H2 sbsp cluster/IGD geometry is
    /// triangle LISTS — each part's strip range is consecutive index triples.</summary>
    private static AssObject BuildH2SectionObject(TagStruct section, int materialsCount)
    {
        var rawV = section.Field("raw vertices")?.AsBlock();
        var strip = section.Field("strip indices")?.AsBlock();
        var parts = section.Field("parts")?.AsBlock();
        if (rawV is null || strip is null || parts is null) return AssObject.EmptyMesh();

        var stripIdx = new uint[strip.Count];
        for (int k = 0; k < strip.Count; k++) stripIdx[k] = (uint)(strip.Element(k)!.ReadIntAny("index") ?? 0);

        var verts = new List<AssVertex>();
        var tris = new List<AssTriangle>();
        foreach (var part in parts.Elements())
        {
            int mat = (int)(part.ReadIntAny("material") ?? 0);
            if (mat < 0 || mat >= materialsCount) mat = 0;
            int start = (int)System.Math.Max(part.ReadIntAny("strip start index") ?? 0, 0);
            int len = (int)System.Math.Max(part.ReadIntAny("strip length") ?? 0, 0);
            if (start >= stripIdx.Length) continue;
            int end = System.Math.Min(start + len, stripIdx.Length);
            for (int t = start; t + 3 <= end; t += 3)
            {
                uint baseIdx = (uint)verts.Count;
                bool ok = true;
                for (int j = 0; j < 3; j++)
                {
                    var v = rawV.Element((int)stripIdx[t + j]);
                    if (v is null) { ok = false; break; }
                    var jv = JmsFile.ReadH2Vertex(v);
                    var uv = jv.Uvs.Count > 0 ? jv.Uvs[0] : default;
                    verts.Add(new AssVertex { Position = jv.Position, Normal = jv.Normal, Uvs = new() { new RealPoint3d(uv.X, uv.Y, 0f) } });
                }
                if (ok) tris.Add(new AssTriangle(mat, baseIdx, baseIdx + 1, baseIdx + 2));
                else verts.RemoveRange((int)baseIdx, verts.Count - (int)baseIdx);
            }
        }
        return new AssObject { Payload = new AssObjectPayload.Mesh(verts, tris) };
    }

    /// <summary>Build a single fanned `@CollideOnly` mesh from a collision-bsp
    /// block (edge-ring walk per surface). Vertices are world-space.</summary>
    private static (List<AssVertex>, List<AssTriangle>) BuildH2CollisionMesh(TagBlock collBlock, int materialIndex)
    {
        var verts = new List<AssVertex>();
        var tris = new List<AssTriangle>();
        foreach (var bsp in collBlock.Elements())
        {
            var surfaces = bsp.Field("surfaces")?.AsBlock();
            var edges = bsp.Field("edges")?.AsBlock();
            var bspVerts = bsp.Field("vertices")?.AsBlock();
            if (surfaces is null || edges is null || bspVerts is null) continue;
            var edgeCache = new List<Geometry.EdgeRow>(edges.Count);
            for (int k = 0; k < edges.Count; k++)
            {
                var e = edges.Element(k)!;
                edgeCache.Add(new Geometry.EdgeRow(
                    (int)(e.ReadIntAny("start vertex") ?? -1), (int)(e.ReadIntAny("end vertex") ?? -1),
                    (int)(e.ReadIntAny("forward edge") ?? -1), (int)(e.ReadIntAny("reverse edge") ?? -1),
                    (int)(e.ReadIntAny("left surface") ?? -1), (int)(e.ReadIntAny("right surface") ?? -1)));
            }
            var points = new RealPoint3d[bspVerts.Count];
            for (int k = 0; k < bspVerts.Count; k++) points[k] = bspVerts.Element(k)!.ReadPointOrVec("point").Mul(Geometry.Scale);
            for (int si = 0; si < surfaces.Count; si++)
            {
                int firstEdge = (int)(surfaces.Element(si)!.ReadIntAny("first edge") ?? -1);
                if (firstEdge < 0) continue;
                var polygon = Geometry.WalkSurfaceRing(si, firstEdge, edgeCache);
                if (polygon.Count < 3) continue;
                uint baseFan = (uint)verts.Count;
                foreach (int vi in polygon)
                {
                    var pos = vi >= 0 && vi < points.Length ? points[vi] : default;
                    verts.Add(new AssVertex { Position = pos, Normal = new RealVector3d(0, 0, 1), Uvs = new() { default } });
                }
                for (uint k = 1; k < polygon.Count - 1; k++) tris.Add(new AssTriangle(materialIndex, baseFan, baseFan + k, baseFan + k + 1));
            }
        }
        return (verts, tris);
    }

    //================================================================
    // stli lighting
    //================================================================

    public void AddLightsFromStli(TagFile stli)
    {
        var root = stli.Root;

        var miBlock = root.FieldPath("material info")?.AsBlock();
        if (miBlock is not null)
        {
            for (int i = 0; i < miBlock.Count; i++)
            {
                if (i >= Materials.Count) break;
                var mi = miBlock.Element(i)!;
                float power = mi.ReadReal("emissive power") ?? 0.0f;
                if (power <= 0.0f) continue;
                var color = mi.ReadRgb("emissive color");
                float quality = mi.ReadReal("emissive quality") ?? 0.0f;
                float focus = mi.ReadReal("emissive focus") ?? 0.0f;
                long matFlags = mi.ReadIntAny("flags") ?? 0;
                bool attenuationEnabled = (matFlags & 0x0001) != 0;
                float attenFalloff = mi.ReadReal("attenuation falloff") ?? 0.0f;
                float attenCutoff = mi.ReadReal("attenuation cutoff") ?? 0.0f;
                float frustumBlend = mi.ReadReal("frustum blend") ?? 0.0f;
                float frustumFalloff = Degrees(mi.ReadReal("frustum falloff angle") ?? 0.0f);
                float frustumCutoff = Degrees(mi.ReadReal("frustum cutoffoff angle") ?? mi.ReadReal("frustum cutoff angle") ?? 0.0f);
                Materials[i].BmStrings.Add(
                    $"BM_LIGHTING_BASIC {Fr(power)} {Fr(color.Red)} {Fr(color.Green)} {Fr(color.Blue)} {Fr(quality)} 0 {Fr(focus)}");
                Materials[i].BmStrings.Add(
                    $"BM_LIGHTING_ATTEN {(attenuationEnabled ? 1 : 0)} {Fr(attenFalloff * Geometry.Scale)} {Fr(attenCutoff * Geometry.Scale)}");
                Materials[i].BmStrings.Add(
                    $"BM_LIGHTING_FRUS {Fr(frustumBlend)} {Fr(frustumFalloff)} {Fr(frustumCutoff)}");
            }
        }

        var defs = root.FieldPath("generic light definitions")?.AsBlock();
        var insts = root.FieldPath("generic light instances")?.AsBlock();
        if (defs is null || insts is null) return;

        var defObjectIndex = new int?[defs.Count];
        for (int di = 0; di < defs.Count; di++)
        {
            var d = defs.Element(di)!;
            var kind = (d.ReadIntAny("type") ?? 0) switch
            {
                0 => AssLightKind.OmniLgt,
                1 => AssLightKind.SpotLgt,
                2 => AssLightKind.DirectLgt,
                3 => AssLightKind.AmbientLgt,
                _ => AssLightKind.OmniLgt,
            };
            var color = d.ReadRgb("color");
            float intensity = d.ReadReal("intensity") ?? 0.0f;
            float hotspotSize = Degrees(d.ReadReal("hotspot size") ?? 0.0f);
            float hotspotFalloff = Degrees(d.ReadReal("hotspot falloff size") ?? 0.0f);
            long flags = d.ReadIntAny("flags") ?? 0;
            bool useNear = (flags & 0x0001) != 0;
            bool useFar = (flags & 0x0002) != 0;
            var near = d.ReadRealBounds("near attenuation bounds");
            var far = d.ReadRealBounds("far attenuation bounds");
            int shape = (int)(d.ReadIntAny("shape") ?? 1);
            float aspect = d.ReadReal("aspect") ?? 1.0f;

            var light = new AssLight
            {
                Kind = kind,
                Color = color,
                Intensity = intensity,
                HotspotSize = hotspotSize,
                HotspotFalloff = hotspotFalloff,
                UseNearAttenuation = useNear,
                NearAttenMin = near.Lower * Geometry.Scale,
                NearAttenMax = near.Upper * Geometry.Scale,
                UseFarAttenuation = useFar,
                FarAttenMin = far.Lower * Geometry.Scale,
                FarAttenMax = far.Upper * Geometry.Scale,
                Shape = shape,
                Aspect = aspect,
            };
            defObjectIndex[di] = Objects.Count;
            Objects.Add(new AssObject { Payload = new AssObjectPayload.GenericLight(light) });
        }

        for (int ii = 0; ii < insts.Count; ii++)
        {
            var inst = insts.Element(ii)!;
            long defIdx = inst.ReadIntAny("definition index") ?? -1;
            if (defIdx < 0 || defIdx >= defObjectIndex.Length) continue;
            if (defObjectIndex[(int)defIdx] is not int objectIndex) continue;
            var origin = inst.ReadPoint3d("origin");
            var forward = inst.ReadVec3("forward");
            var up = inst.ReadVec3("up");
            var left = up.Cross(forward);
            var rot = MathExtensions.FromBasisColumns(forward, left, up);
            Instances.Add(new AssInstance
            {
                ObjectIndex = objectIndex,
                Name = $"light_{ii}",
                UniqueId = Instances.Count,
                ParentId = 0,
                LocalRotation = rot,
                LocalTranslation = origin.Mul(Geometry.Scale),
            });
        }
    }

    //================================================================
    // walkers
    //================================================================

    private static List<AssMaterial> ReadMaterials(TagStruct root)
    {
        var block = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var outList = new List<AssMaterial>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var m = block.Element(i)!;
            string shaderName = FileStem(m.ReadTagRefPath("render method") ?? "", "default");
            float lightmapRes = 1.0f;
            var props = m.Field("properties")?.AsBlock();
            if (props is not null)
            {
                for (int p = 0; p < props.Count; p++)
                {
                    var prop = props.Element(p)!;
                    long propType = prop.ReadIntAny("type") ?? -1;
                    if (propType == 0 && prop.ReadReal("real-value") is { } v) lightmapRes = v;
                }
            }
            outList.Add(new AssMaterial
            {
                Name = shaderName,
                LightmapVariant = "",
                BmStrings = new() { "BM_FLAGS 0000000000000000000000", FormatBmLmres(lightmapRes) },
            });
        }
        return outList;
    }

    private static string FormatBmLmres(float res) =>
        $"BM_LMRES {Fr(res)} 1 0.0000000000 0.0000000000 0.0000000000 0 0.0000000000 0.0000000000 0.0000000000 0 0";

    private static AssObject BuildClusterObject(
        TagStruct mesh, TagStruct meshPmt, Geometry.CompressionBounds bounds, bool flipWinding)
    {
        var rawV = meshPmt.Field("raw vertices")?.AsBlock();
        var parts = mesh.Field("parts")?.AsBlock();
        if (rawV is null || parts is null) return AssObject.EmptyMesh();
        var subparts = mesh.Field("subparts")?.AsBlock();

        var indices = ReadIndexPool(meshPmt);
        if (indices is null) return AssObject.EmptyMesh();

        // H3 sbsp meshes are ALWAYS triangle lists (the schema enum lies).
        var triPool = new List<(int Mat, uint A, uint B, uint C)>();
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts.Element(pi)!;
            int materialIndex = (int)(part.ReadIntAny("render method index") ?? 0);
            long subStart = part.ReadIntAny("subpart start") ?? 0;
            long subCount = part.ReadIntAny("subpart count") ?? 0;
            long partStart = part.ReadIntAny("index start") ?? 0;
            long partCount = part.ReadIntAny("index count") ?? 0;

            void EmitRange(long startI, long countI)
            {
                if (countI <= 0) return;
                int start = startI < 0 ? (ushort)(short)startI : (int)startI;
                int count = (int)countI;
                if (start >= indices.Count) return;
                int end = System.Math.Min(start + count, indices.Count);
                for (int j = start; j + 2 < end; j += 3)
                    triPool.Add((materialIndex, indices[j], indices[j + 1], indices[j + 2]));
            }

            if (subparts is not null && subCount > 0)
            {
                for (int subOff = 0; subOff < subCount; subOff++)
                {
                    int si = (int)subStart + subOff;
                    if (si >= subparts.Count) break;
                    var sp = subparts.Element(si)!;
                    EmitRange(sp.ReadIntAny("index start") ?? 0, sp.ReadIntAny("index count") ?? 0);
                }
                continue;
            }
            EmitRange(partStart, partCount);
        }

        if (flipWinding)
            for (int i = 0; i < triPool.Count; i++)
            {
                var t = triPool[i];
                triPool[i] = (t.Mat, t.A, t.C, t.B);
            }

        var vertexRemap = new Dictionary<uint, uint>();
        var vertices = new List<AssVertex>();
        var triangles = new List<AssTriangle>(triPool.Count);
        foreach (var (mat, a, b, c) in triPool)
        {
            uint va = RemapVertex(vertexRemap, vertices, rawV, a, bounds);
            uint vb = RemapVertex(vertexRemap, vertices, rawV, b, bounds);
            uint vc = RemapVertex(vertexRemap, vertices, rawV, c, bounds);
            triangles.Add(new AssTriangle(mat, va, vb, vc));
        }
        return new AssObject { Payload = new AssObjectPayload.Mesh(vertices, triangles) };
    }

    private static AssObject BuildRenderModelObject(
        TagStruct mesh, TagStruct meshPmt, TagBlock matsBlock,
        Geometry.CompressionBounds bounds, List<AssMaterial> materials, string cellLabel)
    {
        var rawV = meshPmt.Field("raw vertices")?.AsBlock();
        var parts = mesh.Field("parts")?.AsBlock();
        if (rawV is null || parts is null) return AssObject.EmptyMesh();

        var indices = ReadIndexPool(meshPmt);
        if (indices is null) return AssObject.EmptyMesh();

        var v = mesh.Field("index buffer type")?.Value;
        bool isStrip = v is null || (v is TagFieldData.CharEnum ce && ce.Name == "triangle strip");

        var vertexRemap = new Dictionary<uint, uint>();
        var vertices = new List<AssVertex>();
        var triangles = new List<AssTriangle>();
        var subparts = mesh.Field("subparts")?.AsBlock();

        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts.Element(pi)!;
            long shaderIdx = part.ReadIntAny("render method index") ?? 0;
            string shaderName = shaderIdx >= 0 && shaderIdx < matsBlock.Count
                ? FileStem(matsBlock.Element((int)shaderIdx)!.ReadTagRefPath("render method") ?? "", "default")
                : "default";

            int materialIndex = -1;
            for (int mIdx = 0; mIdx < materials.Count; mIdx++)
                if (materials[mIdx].Name == shaderName && materials[mIdx].LightmapVariant == cellLabel)
                {
                    materialIndex = mIdx;
                    break;
                }
            if (materialIndex < 0)
            {
                materials.Add(new AssMaterial { Name = shaderName, LightmapVariant = cellLabel, BmStrings = new() });
                materialIndex = materials.Count - 1;
            }
            int matIdx = materialIndex;

            long partStart = part.ReadIntAny("index start") ?? 0;
            long partCount = part.ReadIntAny("index count") ?? 0;
            long subStart = part.ReadIntAny("subpart start") ?? 0;
            long subCount = part.ReadIntAny("subpart count") ?? 0;

            void EmitRange(long startI, long countI)
            {
                if (countI <= 0) return;
                int start = startI < 0 ? (ushort)(short)startI : (int)startI;
                int count = (int)countI;
                if (start >= indices.Count) return;
                int end = System.Math.Min(start + count, indices.Count);
                var slice = indices.GetRange(start, end - start);
                List<(uint, uint, uint)> tris = isStrip
                    ? Geometry.StripToListU32(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(slice))
                    : ListTriangles(slice);
                foreach (var (a, b, c) in tris)
                {
                    uint va = RemapVertex(vertexRemap, vertices, rawV, a, bounds);
                    uint vb = RemapVertex(vertexRemap, vertices, rawV, b, bounds);
                    uint vc = RemapVertex(vertexRemap, vertices, rawV, c, bounds);
                    triangles.Add(new AssTriangle(matIdx, va, vb, vc));
                }
            }

            if (subparts is not null && subCount > 0)
            {
                for (int subOff = 0; subOff < subCount; subOff++)
                {
                    int si = (int)subStart + subOff;
                    if (si >= subparts.Count) break;
                    var sp = subparts.Element(si)!;
                    EmitRange(sp.ReadIntAny("index start") ?? 0, sp.ReadIntAny("index count") ?? 0);
                }
                continue;
            }
            EmitRange(partStart, partCount);
        }
        return new AssObject { Payload = new AssObjectPayload.Mesh(vertices, triangles) };
    }

    private static List<(uint, uint, uint)> ListTriangles(List<uint> slice)
    {
        var outList = new List<(uint, uint, uint)>(slice.Count / 3);
        for (int i = 0; i + 2 < slice.Count; i += 3)
            outList.Add((slice[i], slice[i + 1], slice[i + 2]));
        return outList;
    }

    /// <summary>u16 <c>raw indices</c> first, else u32 <c>raw indices32</c>;
    /// null when neither is populated.</summary>
    private static List<uint>? ReadIndexPool(TagStruct meshPmt)
    {
        var u16 = meshPmt.Field("raw indices")?.AsBlock();
        var u32 = meshPmt.Field("raw indices32")?.AsBlock();
        if (u16 is not null && u16.Count > 0)
        {
            var outList = new List<uint>(u16.Count);
            for (int k = 0; k < u16.Count; k++)
                outList.Add((uint)(u16.Element(k)!.ReadIntAny("word") ?? 0));
            return outList;
        }
        if (u32 is not null && u32.Count > 0)
        {
            var outList = new List<uint>(u32.Count);
            for (int k = 0; k < u32.Count; k++)
                outList.Add((uint)(u32.Element(k)!.ReadIntAny("dword") ?? 0));
            return outList;
        }
        return null;
    }

    private static uint RemapVertex(
        Dictionary<uint, uint> map, List<AssVertex> outList, TagBlock rawV, uint srcIdx, Geometry.CompressionBounds bounds)
    {
        if (map.TryGetValue(srcIdx, out uint existing)) return existing;
        uint newIdx = (uint)outList.Count;
        var elem = rawV.Element((int)srcIdx);
        outList.Add(elem is null ? DefaultVertex() : ReadVertex(elem, bounds));
        map[srcIdx] = newIdx;
        return newIdx;
    }

    private static AssVertex ReadVertex(TagStruct v, Geometry.CompressionBounds bounds)
    {
        var position = bounds.DecompressPosition(v.ReadPoint3d("position")).Mul(Geometry.Scale);
        var normal = v.ReadPoint3d("normal").AsVector();
        var uv = bounds.DecompressTexcoord(v.ReadPoint2d("texcoord"));
        return new AssVertex
        {
            Position = position,
            Normal = normal,
            Color = default,
            NodeSet = new(),
            Uvs = new() { new RealPoint3d(uv.X, 1.0f - uv.Y, 0.0f) },
        };
    }

    private static AssVertex NewVertex(RealPoint3d position) => new()
    {
        Position = position,
        Normal = new RealVector3d(0, 0, 1),
        Color = default,
        NodeSet = new(),
        Uvs = new() { default },
    };

    private static AssVertex DefaultVertex() => new()
    {
        Position = default,
        Normal = new RealVector3d(0, 0, 1),
        Color = default,
        NodeSet = new(),
        Uvs = new() { default },
    };

    private static (List<AssVertex>, List<AssTriangle>) PolyhedronFromPlanes(List<RealPlane3d> planes, int materialIndex)
    {
        int n = planes.Count;
        if (n < 4) return (new(), new());

        var candidates = new List<RealPoint3d>();
        const float eps = 1e-3f;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = j + 1; k < n; k++)
                {
                    if (MathExtensions.TripleIntersection(planes[i], planes[j], planes[k]) is not { } p) continue;
                    bool inside = true;
                    foreach (var plane in planes)
                        if (plane.Normal().Dot(p.AsVector()) + plane.D > eps) { inside = false; break; }
                    if (inside) candidates.Add(p);
                }
        if (candidates.Count < 4) return (new(), new());

        var unique = new List<RealPoint3d>();
        float dedupEpsSq = (eps * 10.0f) * (eps * 10.0f);
        foreach (var c in candidates)
        {
            bool dup = false;
            foreach (var u in unique)
                if (c.DistanceSquaredTo(u) < dedupEpsSq) { dup = true; break; }
            if (!dup) unique.Add(c);
        }

        var vertices = unique.Select(p => NewVertex(p.Mul(Geometry.Scale))).ToList();

        var tris = new List<AssTriangle>();
        float faceEps = eps * 100.0f;
        foreach (var plane in planes)
        {
            var normal = plane.Normal();
            var onPlane = new List<uint>();
            for (int vi = 0; vi < unique.Count; vi++)
                if (MathF.Abs(normal.Dot(unique[vi].AsVector()) + plane.D) < faceEps) onPlane.Add((uint)vi);
            if (onPlane.Count < 3) continue;
            var centroid = default(RealPoint3d);
            foreach (uint vi in onPlane)
            {
                var p = unique[(int)vi];
                centroid = new RealPoint3d(centroid.X + p.X, centroid.Y + p.Y, centroid.Z + p.Z);
            }
            float inv = 1.0f / onPlane.Count;
            centroid = centroid.Mul(inv);
            var perpSeed = MathF.Abs(normal.I) < 0.9f
                ? new RealVector3d(1, 0, 0) : new RealVector3d(0, 1, 0);
            var uAxis = normal.Cross(perpSeed).Normalized();
            var vAxis = normal.Cross(uAxis).Normalized();
            var withAngle = onPlane.Select(vi =>
            {
                var offset = unique[(int)vi].Sub(centroid);
                float u = uAxis.Dot(offset);
                float vv = vAxis.Dot(offset);
                return (Angle: MathF.Atan2(vv, u), Vi: vi);
            }).ToList();
            withAngle.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            var sorted = withAngle.Select(t => t.Vi).ToList();
            for (int k = 1; k < sorted.Count - 1; k++)
                tris.Add(new AssTriangle(materialIndex, sorted[0], sorted[k], sorted[k + 1]));
        }
        return (vertices, tris);
    }

    private static int EnsureSpecialMaterial(List<AssMaterial> materials, string marker)
    {
        for (int i = 0; i < materials.Count; i++)
            if (materials[i].Name == marker) return i;
        materials.Add(new AssMaterial
        {
            Name = marker,
            LightmapVariant = "",
            BmStrings = new()
            {
                "BM_FLAGS 0000000000000000000000",
                "BM_LMRES 1.0000000000 1 0.0000000000 0.0000000000 0.0000000000 0 0.0000000000 0.0000000000 0.0000000000 0 0",
            },
        });
        return materials.Count - 1;
    }

    private static string ObjectContentKey(AssObject obj)
    {
        if (obj.Payload is not AssObjectPayload.Mesh m) return "";
        var key = new List<byte>(m.Vertices.Count * 12 + m.Triangles.Count * 16);
        foreach (var v in m.Vertices)
        {
            key.AddRange(BitConverter.GetBytes(v.Position.X));
            key.AddRange(BitConverter.GetBytes(v.Position.Y));
            key.AddRange(BitConverter.GetBytes(v.Position.Z));
        }
        foreach (var t in m.Triangles)
        {
            key.AddRange(BitConverter.GetBytes(t.Material));
            key.AddRange(BitConverter.GetBytes(t.V0));
            key.AddRange(BitConverter.GetBytes(t.V1));
            key.AddRange(BitConverter.GetBytes(t.V2));
        }
        return Convert.ToBase64String(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(key));
    }

    private static bool ComputeAxisFlip(Geometry.CompressionBounds b)
    {
        if (!b.PosCompressed) return false;
        int flips = (b.PxMax < b.PxMin ? 1 : 0) + (b.PyMax < b.PyMin ? 1 : 0) + (b.PzMax < b.PzMin ? 1 : 0);
        return flips % 2 == 1;
    }

    private static List<RmNode> ReadRmNodesLocal(TagStruct root)
    {
        var block = root.FieldPath("nodes")?.AsBlock() ?? throw Missing("nodes");
        var outList = new List<RmNode>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var n = block.Element(i)!;
            string name = n.ReadStringId("name") ?? "";
            int parent = n.ReadBlockIndex("parent node");
            var d = n.Field("default")?.AsStruct();
            var rotation = d is not null ? d.ReadQuat("rotation") : new RealQuaternion(0, 0, 0, 1);
            var translation = d is not null ? d.ReadPoint3d("translation") : default;
            outList.Add(new RmNode(name, parent, rotation, translation));
        }
        return outList;
    }

    private readonly record struct RmNode(string Name, int Parent, RealQuaternion Rotation, RealPoint3d Translation);

    /// <summary>Rust <c>Path::file_stem</c> on a <c>\</c>-or-<c>/</c> path.</summary>
    private static string FileStem(string raw, string fallback)
    {
        string p = raw.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        string name = slash >= 0 ? p[(slash + 1)..] : p;
        if (name.Length == 0) return fallback;
        int dot = name.LastIndexOf('.');
        string stem = dot <= 0 ? name : name[..dot];
        return stem.Length == 0 ? fallback : stem;
    }

    // Rust's `f32::to_degrees()` multiplies by this precomputed f32 constant
    // (180/π) rather than dividing at runtime — replicate it bit-for-bit.
    private const float PisIn180 = 57.2957795130823208767981548141051703f;
    private static float Degrees(float radians) => radians * PisIn180;

    private static InvalidOperationException Missing(string path) =>
        new($"scenario_structure_bsp is missing required field: {path}");

    //================================================================
    // version-7 text writer
    //================================================================

    public void Write(Stream stream, int version = 7)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = false };
        Write(w, version);
        w.Flush();
    }

    public string ToText(int version = 7)
    {
        var sb = new StringBuilder();
        using var w = new StringWriter(sb);
        Write(w, version);
        return sb.ToString();
    }

    /// <summary>Write the ASS at the given format version (Halo 2 → 2,
    /// Halo 3 → 7). Version deltas (from the H3 Blender exporter): material
    /// BM_ lighting strings v4+, vertex color v6+, 3-component UVs v5+, and
    /// the node-set / triangle layout switches to tab-separated single lines
    /// at v3+ (older writes them on separate lines).</summary>
    private void Write(TextWriter w, int version)
    {
        void NL() => w.Write('\n');
        void L(string s) { w.Write(s); w.Write('\n'); }

        // HEADER
        L(";### HEADER ###");
        L(N(version));
        L($"\"{HeaderTool}\"");
        L($"\"{HeaderToolVersion}\"");
        L($"\"{HeaderUser}\"");
        L($"\"{HeaderMachine}\"");
        NL();

        // MATERIALS
        L(";### MATERIALS ###");
        L(N(Materials.Count));
        for (int i = 0; i < Materials.Count; i++)
        {
            var m = Materials[i];
            NL();
            L($";MATERIAL {i}");
            L($"\"{m.Name}\"");
            L($"\"{m.LightmapVariant}\"");
            if (version >= 4)
            {
                L(N(m.BmStrings.Count));
                foreach (var s in m.BmStrings) L($"\"{s}\"");
            }
        }
        NL();

        // OBJECTS
        L(";### OBJECTS ###");
        L(N(Objects.Count));
        for (int i = 0; i < Objects.Count; i++)
        {
            var obj = Objects[i];
            NL();
            L($";OBJECT {i}");
            L($"\"{obj.ClassStr}\"");
            L($"\"{obj.XrefFilepath}\"");
            L($"\"{obj.XrefObjectname}\"");
            switch (obj.Payload)
            {
                case AssObjectPayload.Mesh mesh:
                    w.Write(N(mesh.Vertices.Count));
                    foreach (var v in mesh.Vertices)
                    {
                        NL();
                        WriteFloats(w, v.Position.ToArray());
                        WriteFloats(w, v.Normal.ToArray());
                        if (version >= 6)
                            WriteFloats(w, [v.Color.Red, v.Color.Green, v.Color.Blue]);
                        w.Write(N(v.NodeSet.Count));
                        foreach (var (idx, weight) in v.NodeSet)
                            w.Write(version >= 3 ? $"\n{N(idx)}\t{Fr(weight)}" : $"\n{N(idx)}\n{Fr(weight)}");
                        w.Write($"\n{N(v.Uvs.Count)}");
                        foreach (var uv in v.Uvs)
                            w.Write(version >= 5 ? $"\n{Fr(uv.X)}\t{Fr(uv.Y)}\t{Fr(uv.Z)}\n" : $"\n{Fr(uv.X)}\t{Fr(uv.Y)}");
                    }
                    w.Write($"\n{N(mesh.Triangles.Count)}");
                    foreach (var t in mesh.Triangles)
                        w.Write(version >= 3 ? $"\n{N(t.Material)}\t\t{t.V0}\t{t.V1}\t{t.V2}" : $"\n{N(t.Material)}\n{t.V0}\n{t.V1}\n{t.V2}");
                    NL();
                    break;
                case AssObjectPayload.GenericLight gl:
                    var l = gl.Light;
                    L($"\"{l.Kind.AsStr()}\"");
                    WriteFloats(w, [l.Color.Red, l.Color.Green, l.Color.Blue]);
                    L(Fr(l.Intensity));
                    L(Fr(l.HotspotSize));
                    L(Fr(l.HotspotFalloff));
                    L(l.UseNearAttenuation ? "1" : "0");
                    L(Fr(l.NearAttenMin));
                    L(Fr(l.NearAttenMax));
                    L(l.UseFarAttenuation ? "1" : "0");
                    L(Fr(l.FarAttenMin));
                    L(Fr(l.FarAttenMax));
                    L(N(l.Shape));
                    L(Fr(l.Aspect));
                    break;
                case AssObjectPayload.Sphere sph:
                    L(N(sph.Material));
                    L(Fr(sph.Radius));
                    break;
            }
        }
        NL();

        // INSTANCES
        L(";### INSTANCES ###");
        L(N(Instances.Count));
        for (int i = 0; i < Instances.Count; i++)
        {
            var inst = Instances[i];
            NL();
            L($";INSTANCE {i}");
            L(N(inst.ObjectIndex));
            L($"\"{inst.Name}\"");
            L(N(inst.UniqueId));
            L(N(inst.ParentId));
            L(N(inst.InheritanceFlag));
            WriteFloats(w, inst.LocalRotation.ToArray());
            WriteFloats(w, inst.LocalTranslation.ToArray());
            L(Fr(inst.LocalScale));
            WriteFloats(w, inst.PivotRotation.ToArray());
            WriteFloats(w, inst.PivotTranslation.ToArray());
            L(Fr(inst.PivotScale));
            foreach (int nodeIndex in inst.BoneGroups) L(N(nodeIndex));
        }
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>write_floats style: tab-separated, last with newline, -0→+0.</summary>
    private static void WriteFloats(TextWriter w, float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i] == 0f ? 0f : values[i];
            w.Write(v.ToString("F10", CultureInfo.InvariantCulture));
            w.Write(i + 1 < values.Length ? '\t' : '\n');
        }
    }

    /// <summary>Inline <c>{:.10}</c> formatter — NO -0 normalization (matches
    /// Rust's inline <c>write!("{:.10}")</c> calls for uv/light/scale fields).</summary>
    private static string Fr(float v) => v.ToString("F10", CultureInfo.InvariantCulture);
}

//================================================================
// ASS value types
//================================================================

public sealed class AssMaterial
{
    public string Name { get; set; } = "";
    public string LightmapVariant { get; set; } = "";
    public List<string> BmStrings { get; init; } = new();
}

public sealed class AssVertex
{
    public RealPoint3d Position { get; set; }
    public RealVector3d Normal { get; set; }
    public RealRgbColor Color { get; set; }
    public List<(int Index, float Weight)> NodeSet { get; init; } = new();
    public List<RealPoint3d> Uvs { get; init; } = new();
}

public readonly record struct AssTriangle(int Material, uint V0, uint V1, uint V2);

public sealed class AssObject
{
    public string XrefFilepath { get; set; } = "";
    public string XrefObjectname { get; set; } = "";
    public required AssObjectPayload Payload { get; set; }

    public string ClassStr => Payload switch
    {
        AssObjectPayload.Mesh => "MESH",
        AssObjectPayload.GenericLight => "GENERIC_LIGHT",
        AssObjectPayload.Sphere => "SPHERE",
        _ => "MESH",
    };

    public int VerticesLen => Payload is AssObjectPayload.Mesh m ? m.Vertices.Count : 0;
    public int TrianglesLen => Payload is AssObjectPayload.Mesh m ? m.Triangles.Count : 0;

    public static AssObject EmptyMesh() => new() { Payload = new AssObjectPayload.Mesh(new(), new()) };
}

public abstract record AssObjectPayload
{
    private AssObjectPayload() { }
    public sealed record Mesh(List<AssVertex> Vertices, List<AssTriangle> Triangles) : AssObjectPayload;
    public sealed record GenericLight(AssLight Light) : AssObjectPayload;
    public sealed record Sphere(int Material, float Radius) : AssObjectPayload;
}

public sealed class AssLight
{
    public AssLightKind Kind { get; init; }
    public RealRgbColor Color { get; init; }
    public float Intensity { get; init; }
    public float HotspotSize { get; init; }
    public float HotspotFalloff { get; init; }
    public bool UseNearAttenuation { get; init; }
    public float NearAttenMin { get; init; }
    public float NearAttenMax { get; init; }
    public bool UseFarAttenuation { get; init; }
    public float FarAttenMin { get; init; }
    public float FarAttenMax { get; init; }
    public int Shape { get; init; }
    public float Aspect { get; init; }
}

public enum AssLightKind { SpotLgt, DirectLgt, OmniLgt, AmbientLgt }

internal static class AssLightKindExtensions
{
    public static string AsStr(this AssLightKind k) => k switch
    {
        AssLightKind.SpotLgt => "SPOT_LGT",
        AssLightKind.DirectLgt => "DIRECT_LGT",
        AssLightKind.OmniLgt => "OMNI_LGT",
        AssLightKind.AmbientLgt => "AMBIENT_LGT",
        _ => "OMNI_LGT",
    };
}

public sealed class AssInstance
{
    public int ObjectIndex { get; set; }
    public string Name { get; set; } = "";
    public int UniqueId { get; set; }
    public int ParentId { get; set; } = -1;
    public int InheritanceFlag { get; set; }
    public RealQuaternion LocalRotation { get; set; } = new(0, 0, 0, 1);
    public RealPoint3d LocalTranslation { get; set; }
    public float LocalScale { get; set; } = 1.0f;
    public RealQuaternion PivotRotation { get; set; } = new(0, 0, 0, 1);
    public RealPoint3d PivotTranslation { get; set; }
    public float PivotScale { get; set; } = 1.0f;
    public List<int> BoneGroups { get; init; } = new();
}
