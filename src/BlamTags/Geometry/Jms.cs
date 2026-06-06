using System.Globalization;

namespace BlamTags;

/// <summary>
/// JMS (Bungie Joint Model Skeleton) export — a port of the Rust
/// <c>jms.rs</c>. Reconstructs a JMS v8213 static-geometry asset from a
/// parsed <c>render_model</c> (mesh + skeleton + markers), and from
/// <c>collision_model</c> / <c>physics_model</c> tags (BSP geometry and
/// Havok shape/constraint primitives).
///
/// Targets the H3 / Reach / H4 MCC source-style pipeline where render
/// meshes store their vertex/index buffers inline under
/// <c>render geometry/per mesh temporary[i]</c> (no resource indirection).
/// </summary>
public sealed class JmsFile
{
    public List<JmsNode> Nodes { get; init; } = new();
    public List<JmsMaterial> Materials { get; init; } = new();
    public List<JmsMarker> Markers { get; init; } = new();
    public List<JmsVertex> Vertices { get; init; } = new();
    public List<JmsTriangle> Triangles { get; init; } = new();
    public List<JmsSphere> Spheres { get; init; } = new();
    public List<JmsBox> Boxes { get; init; } = new();
    public List<JmsCapsule> Capsules { get; init; } = new();
    public List<JmsConvex> ConvexShapes { get; init; } = new();
    public List<JmsRagdoll> Ragdolls { get; init; } = new();
    public List<JmsHinge> Hinges { get; init; } = new();

    //================================================================
    // render_model
    //================================================================

    /// <summary>Walk a parsed <c>render_model</c> and reconstruct the JMS
    /// scene from its inline geometry, nodes, marker_groups, and the
    /// region × permutation × material walk.</summary>
    public static JmsFile FromRenderModel(TagFile tag)
    {
        var root = tag.Root;
        // Tag stores node default rotation/translation LOCAL to the parent;
        // JMS expects WORLD-space bind pose, so chain forward.
        var localNodes = ReadNodes(root);
        var worldNodes = ChainLocalToWorld(localNodes);
        var bounds = Geometry.ReadCompressionBounds(root);
        var (materials, partMaterialMap, meshEmitOrder) = BuildMaterials(root);
        var markers = ReadMarkers(root);
        var (vertices, triangles) = BuildGeometry(root, partMaterialMap, meshEmitOrder, bounds);
        AppendInstanceGeometry(root, materials, vertices, triangles, bounds);
        return new JmsFile
        {
            Nodes = worldNodes,
            Materials = materials,
            Markers = markers,
            Vertices = vertices,
            Triangles = triangles,
        };
    }

    //================================================================
    // collision_model
    //================================================================

    public static JmsFile FromCollisionModel(TagFile tag) => BuildCollisionModel(tag, null);

    public static JmsFile FromCollisionModelWithSkeleton(TagFile tag, IReadOnlyList<JmsNode> skeleton) =>
        BuildCollisionModel(tag, skeleton);

    private static JmsFile BuildCollisionModel(TagFile tag, IReadOnlyList<JmsNode>? skeleton)
    {
        var root = tag.Root;
        var nodes = ReadPhmoNodes(root);
        Dictionary<string, (RealQuaternion Rot, RealPoint3d Trans)>? boneXform = null;
        if (skeleton is not null)
        {
            boneXform = new();
            foreach (var n in skeleton) boneXform[n.Name] = (n.Rotation, n.Translation);
        }

        var materialsBlock = root.FieldPath("materials")?.AsBlock()
            ?? throw Missing("materials");
        var regionsBlock = root.FieldPath("regions")?.AsBlock()
            ?? throw Missing("regions");

        var materials = new List<JmsMaterial>();
        var vertices = new List<JmsVertex>();
        var triangles = new List<JmsTriangle>();

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
                var bsps = perm.Field("bsps")?.AsBlock();
                if (bsps is null) continue;
                for (int bi = 0; bi < bsps.Count; bi++)
                {
                    var bspElem = bsps.Element(bi)!;
                    short nodeIdx = (short)(bspElem.ReadIntAny("node index") ?? -1);
                    var bsp = bspElem.Field("bsp")?.AsStruct();
                    if (bsp is null) continue;
                    var surfaces = bsp.Field("surfaces")?.AsBlock();
                    var edges = bsp.Field("edges")?.AsBlock();
                    var bspVerts = bsp.Field("vertices")?.AsBlock();
                    if (surfaces is null || edges is null || bspVerts is null) continue;

                    (RealQuaternion Rot, RealPoint3d Trans)? boneWorld = null;
                    if (boneXform is not null && nodeIdx >= 0 && nodeIdx < nodes.Count)
                    {
                        if (boneXform.TryGetValue(nodes[nodeIdx].Name, out var bw)) boneWorld = bw;
                    }

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

                    var vertPoints = new RealPoint3d[bspVerts.Count];
                    for (int k = 0; k < bspVerts.Count; k++)
                    {
                        var local = bspVerts.Element(k)!.ReadPoint3d("point").Mul(Geometry.Scale);
                        vertPoints[k] = boneWorld is { } bw
                            ? bw.Trans.Add(bw.Rot.Rotate(local.AsVector()))
                            : local;
                    }

                    for (int si = 0; si < surfaces.Count; si++)
                    {
                        var surface = surfaces.Element(si)!;
                        int firstEdge = (int)(surface.ReadIntAny("first edge") ?? -1);
                        if (firstEdge < 0) continue;
                        int surfaceMaterial = (int)(surface.ReadIntAny("material") ?? -1);

                        var polygon = Geometry.WalkSurfaceRing(si, firstEdge, edgeCache);
                        if (polygon.Count < 3) continue;

                        string shaderName = surfaceMaterial >= 0 && surfaceMaterial < materialsBlock.Count
                            ? (materialsBlock.Element(surfaceMaterial)!.ReadStringId("name") ?? "")
                            : "default";
                        string cellLabel = $"{permName} {regionName}";
                        int jmsIdx = FindOrAddMaterial(materials, shaderName, cellLabel);

                        void EmitVert(int vi)
                        {
                            var pos = vi >= 0 && vi < vertPoints.Length ? vertPoints[vi] : default;
                            vertices.Add(new JmsVertex
                            {
                                Position = pos,
                                Normal = new RealVector3d(0, 0, 1),
                                NodeSets = new() { (nodeIdx, 1.0f) },
                                Uvs = new() { new RealPoint2d(0, 0) },
                            });
                        }
                        for (int k = 1; k < polygon.Count - 1; k++)
                        {
                            int a = polygon[0], b = polygon[k], c = polygon[k + 1];
                            uint baseIdx = (uint)vertices.Count;
                            EmitVert(a); EmitVert(b); EmitVert(c);
                            triangles.Add(new JmsTriangle(jmsIdx, baseIdx, baseIdx + 1, baseIdx + 2));
                        }
                    }
                }
            }
        }

        return new JmsFile { Nodes = nodes, Materials = materials, Vertices = vertices, Triangles = triangles };
    }

    //================================================================
    // physics_model
    //================================================================

    public static JmsFile FromPhysicsModel(TagFile tag) => BuildPhysicsModel(tag, null);

    public static JmsFile FromPhysicsModelWithSkeleton(TagFile tag, IReadOnlyList<JmsNode> skeleton) =>
        BuildPhysicsModel(tag, skeleton);

    private static JmsFile BuildPhysicsModel(TagFile tag, IReadOnlyList<JmsNode>? skeleton)
    {
        var root = tag.Root;
        var nodes = ReadPhmoNodes(root);
        if (skeleton is not null)
        {
            var byName = new Dictionary<string, JmsNode>();
            foreach (var n in skeleton) byName[n.Name] = n;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (byName.TryGetValue(nodes[i].Name, out var src))
                    nodes[i] = nodes[i] with { Rotation = src.Rotation, Translation = src.Translation };
            }
        }
        var materials = ReadPhmoMaterials(root);
        var parents = BuildPhmoParentLookup(root);
        var spheres = ReadPhmoSpheres(root, parents);
        var boxes = ReadPhmoBoxes(root, parents);
        var capsules = ReadPhmoPills(root, parents);
        var convex = ReadPhmoPolyhedra(root, parents);
        var ragdolls = ReadPhmoRagdolls(root);
        var hinges = ReadPhmoHinges(root, false);
        hinges.AddRange(ReadPhmoHinges(root, true));
        return new JmsFile
        {
            Nodes = nodes,
            Materials = materials,
            Spheres = spheres,
            Boxes = boxes,
            Capsules = capsules,
            ConvexShapes = convex,
            Ragdolls = ragdolls,
            Hinges = hinges,
        };
    }

    //================================================================
    // node / material / marker / geometry walkers (render_model)
    //================================================================

    private static List<JmsNode> ReadNodes(TagStruct root)
    {
        var block = root.FieldPath("nodes")?.AsBlock() ?? throw Missing("nodes");
        var outList = new List<JmsNode>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var n = block.Element(i)!;
            outList.Add(new JmsNode(
                n.ReadStringId("name") ?? "",
                n.ReadBlockIndex("parent node"),
                n.ReadQuat("default rotation"),
                n.ReadPoint3d("default translation").Mul(Geometry.Scale)));
        }
        return outList;
    }

    /// <summary>Local (parent-relative) → world (root-relative) bind pose.
    /// Forward-iteration works because nodes are stored parent-before-child.</summary>
    private static List<JmsNode> ChainLocalToWorld(List<JmsNode> local)
    {
        var outList = new List<JmsNode>(local.Count);
        for (int i = 0; i < local.Count; i++)
        {
            var n = local[i];
            if (n.Parent < 0 || n.Parent >= i)
            {
                outList.Add(n); // root or forward reference — treat as already-world
            }
            else
            {
                var parent = outList[n.Parent];
                outList.Add(n with
                {
                    Rotation = parent.Rotation.Mul(n.Rotation),
                    Translation = parent.Translation.Add(parent.Rotation.Rotate(n.Translation.AsVector())),
                });
            }
        }
        return outList;
    }

    private static List<JmsMarker> ReadMarkers(TagStruct root)
    {
        var block = root.FieldPath("marker groups")?.AsBlock() ?? throw Missing("marker groups");
        var outList = new List<JmsMarker>();
        for (int i = 0; i < block.Count; i++)
        {
            var g = block.Element(i)!;
            string groupName = g.ReadStringId("name") ?? "";
            var inner = g.Field("markers")?.AsBlock();
            if (inner is null) continue;
            for (int j = 0; j < inner.Count; j++)
            {
                var m = inner.Element(j)!;
                outList.Add(new JmsMarker(
                    groupName,
                    (short)(m.ReadIntAny("node index") ?? -1),
                    m.ReadQuat("rotation"),
                    m.ReadPoint3d("translation").Mul(Geometry.Scale),
                    -1.0f));
            }
        }
        return outList;
    }

    /// <summary>Region × permutation walk → materials list, a
    /// <c>(mesh,part) → material</c> map, and the mesh-emit order.</summary>
    private static (List<JmsMaterial>, Dictionary<(int, int), int>, List<int>) BuildMaterials(TagStruct root)
    {
        var matsBlock = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var regionsBlock = root.FieldPath("regions")?.AsBlock() ?? throw Missing("regions");
        var meshes = root.FieldPath("render geometry/meshes")?.AsBlock()
            ?? throw Missing("render geometry/meshes");

        var materials = new List<JmsMaterial>();
        var partMaterialMap = new Dictionary<(int, int), int>();
        var meshEmitOrder = new List<int>();

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
                    if (mi >= meshes.Count) continue;
                    if (!meshEmitOrder.Contains(mi)) meshEmitOrder.Add(mi);
                    var mesh = meshes.Element(mi)!;
                    var parts = mesh.Field("parts")?.AsBlock();
                    if (parts is null) continue;
                    for (int partI = 0; partI < parts.Count; partI++)
                    {
                        var part = parts.Element(partI)!;
                        long shaderIdx = part.ReadIntAny("render method index") ?? 0;
                        string shaderName = shaderIdx >= 0 && shaderIdx < matsBlock.Count
                            ? FileStem(matsBlock.Element((int)shaderIdx)!.ReadTagRefPath("render method") ?? "")
                            : "default";
                        string cellLabel = $"{permName} {regionName}";
                        int jmsIdx = FindOrAddMaterial(materials, shaderName, cellLabel);
                        partMaterialMap[(mi, partI)] = jmsIdx;
                    }
                }
            }
        }
        return (materials, partMaterialMap, meshEmitOrder);
    }

    private static (List<JmsVertex>, List<JmsTriangle>) BuildGeometry(
        TagStruct root, Dictionary<(int, int), int> partMaterialMap, List<int> meshEmitOrder, Geometry.CompressionBounds bounds)
    {
        var pmtBlock = root.FieldPath("render geometry/per mesh temporary")?.AsBlock()
            ?? throw Missing("render geometry/per mesh temporary");
        var meshesBlock = root.FieldPath("render geometry/meshes")?.AsBlock()
            ?? throw Missing("render geometry/meshes");

        var vertices = new List<JmsVertex>();
        var triangles = new List<JmsTriangle>();

        foreach (int mi in meshEmitOrder)
        {
            if (mi >= pmtBlock.Count) continue;
            var pmt = pmtBlock.Element(mi)!;
            var mesh = meshesBlock.Element(mi)!;

            short? rigidFallback = RigidFallbackNode(mesh);

            var rawV = pmt.Field("raw vertices")?.AsBlock()
                ?? throw Missing("per mesh temporary[i]/raw vertices");
            var indices = ReadIndexPool(pmt);
            bool isStrip = IsStrip(mesh);

            var parts = mesh.Field("parts")?.AsBlock() ?? throw Missing("meshes[i]/parts");
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var part = parts.Element(pi)!;
                int materialIndex = partMaterialMap.TryGetValue((mi, pi), out var m) ? m : 0;
                long startI = part.ReadIntAny("index start") ?? 0;
                long countI = part.ReadIntAny("index count") ?? 0;
                if (countI <= 0) continue;
                int start = startI < 0 ? (ushort)(short)startI : (int)startI;
                int count = (int)countI;
                if (start >= indices.Count) continue;
                int end = System.Math.Min(start + count, indices.Count);
                var partIndices = indices.GetRange(start, end - start);

                void EmitVert(uint vi)
                {
                    var vElem = rawV.Element((int)vi);
                    if (vElem is null) return;
                    var jv = ReadVertex(vElem, bounds);
                    if (jv.NodeSets.Count == 0 && rigidFallback is { } node)
                        jv.NodeSets.Add((node, 1.0f));
                    vertices.Add(jv);
                }
                var tris = TriangulatePart(partIndices, isStrip);
                foreach (var (a, b, c) in tris)
                {
                    uint baseIdx = (uint)vertices.Count;
                    EmitVert(a); EmitVert(b); EmitVert(c);
                    triangles.Add(new JmsTriangle(materialIndex, baseIdx, baseIdx + 1, baseIdx + 2));
                }
            }
        }
        return (vertices, triangles);
    }

    /// <summary>Bake <c>instance placements[]</c> as extra triangles
    /// referencing <c>meshes[instance_mesh_index].subparts[i]</c>, one bone
    /// per placement, each with its own (forward,left,up,position+scale)
    /// transform.</summary>
    private static void AppendInstanceGeometry(
        TagStruct root, List<JmsMaterial> materials, List<JmsVertex> vertices,
        List<JmsTriangle> triangles, Geometry.CompressionBounds bounds)
    {
        long instanceMeshIndex = root.ReadIntAny("instance mesh index") ?? -1;
        if (instanceMeshIndex < 0) return;

        var placements = root.Field("instance placements")?.AsBlock();
        if (placements is null || placements.IsEmpty) return;

        var matsBlock = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var meshesBlock = root.FieldPath("render geometry/meshes")?.AsBlock()
            ?? throw Missing("render geometry/meshes");
        var pmtBlock = root.FieldPath("render geometry/per mesh temporary")?.AsBlock()
            ?? throw Missing("render geometry/per mesh temporary");

        int imi = (int)instanceMeshIndex;
        if (imi >= meshesBlock.Count || imi >= pmtBlock.Count) return;
        var mesh = meshesBlock.Element(imi)!;
        var pmt = pmtBlock.Element(imi)!;

        var rawV = pmt.Field("raw vertices")?.AsBlock() ?? throw Missing("per mesh temporary[i]/raw vertices");
        var indices = ReadIndexPool(pmt);
        bool isStrip = IsStrip(mesh);

        var parts = mesh.Field("parts")?.AsBlock() ?? throw Missing("meshes[i]/parts");
        var subparts = mesh.Field("subparts")?.AsBlock() ?? throw Missing("meshes[i]/subparts");

        for (int ii = 0; ii < placements.Count; ii++)
        {
            var placement = placements.Element(ii)!;
            string name = placement.ReadStringId("name") ?? $"instance_{ii}";
            short nodeIndex = (short)(placement.ReadIntAny("node_index") ?? -1);
            float scale = placement.ReadReal("scale") ?? 1.0f;
            var forward = placement.ReadVec3("forward");
            var left = placement.ReadVec3("left");
            var up = placement.ReadVec3("up");
            var position = placement.ReadPoint3d("position").Mul(Geometry.Scale);

            var subpart = subparts.Element(ii);
            if (subpart is null) continue;
            long partIndex = subpart.ReadIntAny("part index") ?? -1;
            long startI = subpart.ReadIntAny("index start") ?? 0;
            long countI = subpart.ReadIntAny("index count") ?? 0;
            if (countI <= 0) continue;
            int start = startI < 0 ? (ushort)(short)startI : (int)startI;
            int count = (int)countI;
            if (start >= indices.Count) continue;
            int end = System.Math.Min(start + count, indices.Count);
            var partIndices = indices.GetRange(start, end - start);

            string shaderName = "default";
            if (partIndex >= 0 && partIndex < parts.Count)
            {
                var part = parts.Element((int)partIndex)!;
                long shaderIdx = part.ReadIntAny("render method index") ?? 0;
                if (shaderIdx >= 0 && shaderIdx < matsBlock.Count)
                    shaderName = FileStem(matsBlock.Element((int)shaderIdx)!.ReadTagRefPath("render method") ?? "");
            }

            int slot = materials.Count + 1;
            int materialIndex = materials.Count;
            materials.Add(new JmsMaterial(shaderName, $"({slot}) {name}"));

            void EmitVert(uint vi)
            {
                var vElem = rawV.Element((int)vi);
                if (vElem is null) return;
                var jv = ReadVertex(vElem, bounds);
                var p = jv.Position;
                float sx = p.X * scale, sy = p.Y * scale, sz = p.Z * scale;
                jv.Position = new RealPoint3d(
                    forward.I * sx + left.I * sy + up.I * sz + position.X,
                    forward.J * sx + left.J * sy + up.J * sz + position.Y,
                    forward.K * sx + left.K * sy + up.K * sz + position.Z);
                var nrm = jv.Normal;
                jv.Normal = new RealVector3d(
                    forward.I * nrm.I + left.I * nrm.J + up.I * nrm.K,
                    forward.J * nrm.I + left.J * nrm.J + up.J * nrm.K,
                    forward.K * nrm.I + left.K * nrm.J + up.K * nrm.K);
                jv.NodeSets.Clear();
                if (nodeIndex >= 0) jv.NodeSets.Add((nodeIndex, 1.0f));
                vertices.Add(jv);
            }
            var tris = TriangulatePart(partIndices, isStrip);
            foreach (var (a, b, c) in tris)
            {
                uint baseIdx = (uint)vertices.Count;
                EmitVert(a); EmitVert(b); EmitVert(c);
                triangles.Add(new JmsTriangle(materialIndex, baseIdx, baseIdx + 1, baseIdx + 2));
            }
        }
    }

    private static short? RigidFallbackNode(TagStruct mesh)
    {
        long vt = mesh.Field("vertex type")?.Value is TagFieldData.CharEnum ce ? ce.Value : -1;
        if (vt is 1 or 5)
        {
            long? n = mesh.ReadIntAny("rigid node index");
            if (n is >= 0) return (short)n.Value;
        }
        return null;
    }

    private static bool IsStrip(TagStruct mesh)
    {
        var v = mesh.Field("index buffer type")?.Value;
        return v is null || (v is TagFieldData.CharEnum ce && ce.Name == "triangle strip");
    }

    /// <summary>Read whichever raw-index pool is populated (u16 <c>raw
    /// indices</c> or u32 <c>raw indices32</c>), widened to u32.</summary>
    private static List<uint> ReadIndexPool(TagStruct pmt)
    {
        var u16 = pmt.Field("raw indices")?.AsBlock();
        var u32 = pmt.Field("raw indices32")?.AsBlock();
        if (u16 is not null && u16.Count > 0)
        {
            var outList = new List<uint>(u16.Count);
            for (int k = 0; k < u16.Count; k++)
                outList.Add((uint)(u16.Element(k)!.ReadIntAny("word") ?? 0) & 0xFFFF);
            return outList;
        }
        if (u32 is not null && u32.Count > 0)
        {
            var outList = new List<uint>(u32.Count);
            for (int k = 0; k < u32.Count; k++)
                outList.Add((uint)(u32.Element(k)!.ReadIntAny("dword") ?? 0));
            return outList;
        }
        throw Missing("per mesh temporary[i]/raw indices");
    }

    private static List<(uint, uint, uint)> TriangulatePart(List<uint> partIndices, bool isStrip)
    {
        if (isStrip) return Geometry.StripToListU32(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(partIndices));
        var outList = new List<(uint, uint, uint)>(partIndices.Count / 3);
        for (int i = 0; i + 2 < partIndices.Count; i += 3)
            outList.Add((partIndices[i], partIndices[i + 1], partIndices[i + 2]));
        return outList;
    }

    private static JmsVertex ReadVertex(TagStruct v, Geometry.CompressionBounds bounds)
    {
        var position = bounds.DecompressPosition(v.ReadPoint3d("position")).Mul(Geometry.Scale);
        var normal = v.ReadPoint3d("normal").AsVector();
        var texcoord = bounds.DecompressTexcoord(v.ReadPoint2d("texcoord"));
        var nodeSets = new List<(short, float)>(4);
        var idxArr = v.Field("node indices")?.AsArray();
        var wtArr = v.Field("node weights")?.AsArray();
        if (idxArr is not null && wtArr is not null)
        {
            int n = System.Math.Min(idxArr.Count, wtArr.Count);
            for (int k = 0; k < n; k++)
            {
                short idx = idxArr.Element(k)!.Fields().FirstOrDefault()?.Value switch
                {
                    TagFieldData.CharInteger c => (short)c.Value,
                    TagFieldData.ByteInteger b => (short)b.Value,
                    _ => -1,
                };
                float wt = wtArr.Element(k)!.Fields().FirstOrDefault()?.Value is TagFieldData.Real r ? r.Value : 0.0f;
                if (wt > 0.0f) nodeSets.Add((idx, wt));
            }
        }
        return new JmsVertex
        {
            Position = position,
            Normal = normal,
            NodeSets = nodeSets,
            Uvs = new() { new RealPoint2d(texcoord.X, 1.0f - texcoord.Y) },
        };
    }

    //================================================================
    // physics_model / collision_model shared walkers
    //================================================================

    private static List<JmsNode> ReadPhmoNodes(TagStruct root)
    {
        var block = root.FieldPath("nodes")?.AsBlock() ?? throw Missing("nodes");
        var outList = new List<JmsNode>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var n = block.Element(i)!;
            outList.Add(new JmsNode(n.ReadStringId("name") ?? "", n.ReadBlockIndex("parent"),
                new RealQuaternion(0, 0, 0, 1), default));
        }
        return outList;
    }

    private static List<JmsMaterial> ReadPhmoMaterials(TagStruct root)
    {
        var block = root.FieldPath("materials")?.AsBlock() ?? throw Missing("materials");
        var outList = new List<JmsMaterial>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            string name = block.Element(i)!.ReadStringId("name") ?? "";
            outList.Add(new JmsMaterial(name, name));
        }
        return outList;
    }

    private const long ShapeSphere = 0, ShapePill = 1, ShapeBox = 2, ShapePolyhedron = 4;

    private static Dictionary<(long, long), int> BuildPhmoParentLookup(TagStruct root)
    {
        var outMap = new Dictionary<(long, long), int>();
        var rbs = root.FieldPath("rigid bodies")?.AsBlock();
        if (rbs is null) return outMap;
        for (int i = 0; i < rbs.Count; i++)
        {
            var rb = rbs.Element(i)!;
            int nodeIdx = (int)(rb.ReadIntAny("node") ?? -1);
            var sr = rb.Field("shape reference")?.AsStruct();
            if (sr is null) continue;
            long? shapeType = sr.ReadIntAny("shape type");
            long? shapeIdx = sr.ReadIntAny("shape");
            if (shapeType is null || shapeIdx is null) continue;
            outMap[(shapeType.Value, shapeIdx.Value)] = nodeIdx;
        }
        return outMap;
    }

    private static int ParentFor(Dictionary<(long, long), int> lookup, long shapeType, int idx) =>
        lookup.TryGetValue((shapeType, idx), out var v) ? v : -1;

    private static List<JmsSphere> ReadPhmoSpheres(TagStruct root, Dictionary<(long, long), int> parents)
    {
        var block = root.FieldPath("spheres")?.AsBlock();
        if (block is null) return new();
        var outList = new List<JmsSphere>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var s = block.Element(i)!;
            var bse = s.Field("base")?.AsStruct();
            if (bse is null) continue;
            outList.Add(new JmsSphere(
                bse.ReadStringId("name") ?? "",
                ParentFor(parents, ShapeSphere, i),
                (int)(bse.ReadIntAny("material") ?? 0),
                new RealQuaternion(0, 0, 0, 1),
                default,
                (s.ReadReal("radius") ?? 0.0f) * Geometry.Scale));
        }
        return outList;
    }

    private static List<JmsBox> ReadPhmoBoxes(TagStruct root, Dictionary<(long, long), int> parents)
    {
        var block = root.FieldPath("boxes")?.AsBlock();
        if (block is null) return new();
        var outList = new List<JmsBox>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var b = block.Element(i)!;
            var bse = b.Field("base")?.AsStruct();
            var cts = b.Field("convex transform shape")?.AsStruct();
            if (bse is null || cts is null) continue;
            var half = b.ReadVec3("half extents");
            float convexRadius = b.Field("box shape")?.AsStruct()?.ReadReal("radius") ?? 0.0f;
            outList.Add(new JmsBox(
                bse.ReadStringId("name") ?? "",
                ParentFor(parents, ShapeBox, i),
                (int)(bse.ReadIntAny("material") ?? 0),
                RotationFromBasis(cts),
                cts.ReadVec3("translation").AsPoint().Mul(Geometry.Scale),
                (half.I + convexRadius) * 2.0f * Geometry.Scale,
                (half.J + convexRadius) * 2.0f * Geometry.Scale,
                (half.K + convexRadius) * 2.0f * Geometry.Scale));
        }
        return outList;
    }

    private static List<JmsCapsule> ReadPhmoPills(TagStruct root, Dictionary<(long, long), int> parents)
    {
        var block = root.FieldPath("pills")?.AsBlock();
        if (block is null) return new();
        var outList = new List<JmsCapsule>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var p = block.Element(i)!;
            var bse = p.Field("base")?.AsStruct();
            if (bse is null) continue;
            float radius = p.Field("capsule shape")?.AsStruct()?.ReadReal("radius") ?? 0.0f;
            var bottom = p.ReadVec3("bottom");
            var top = p.ReadVec3("top");
            var dir = bottom.Sub(top);
            var unit = dir.Normalized();
            var anchor = bottom.Add(unit.Mul(radius));
            float height = top.Sub(bottom).Length() * Geometry.Scale;
            var axis = top.Sub(bottom);
            var rot = MathExtensions.ShortestArc(new RealVector3d(0, 0, -1), axis);
            outList.Add(new JmsCapsule(
                bse.ReadStringId("name") ?? "",
                ParentFor(parents, ShapePill, i),
                (int)(bse.ReadIntAny("material") ?? 0),
                rot,
                anchor.AsPoint().Mul(Geometry.Scale),
                height,
                radius * Geometry.Scale));
        }
        return outList;
    }

    private static List<JmsConvex> ReadPhmoPolyhedra(TagStruct root, Dictionary<(long, long), int> parents)
    {
        var block = root.FieldPath("polyhedra")?.AsBlock();
        if (block is null) return new();
        var fourVectors = root.FieldPath("polyhedron four vectors")?.AsBlock();
        var outList = new List<JmsConvex>(block.Count);
        int fvOffset = 0;
        for (int i = 0; i < block.Count; i++)
        {
            var p = block.Element(i)!;
            var bse = p.Field("base")?.AsStruct();
            if (bse is null) continue;
            int fvSize = (int)(p.ReadIntAny("four vectors size") ?? 0);
            var verts = new List<RealPoint3d>();
            if (fourVectors is not null)
            {
                for (int k = 0; k < fvSize; k++)
                {
                    var fv = fourVectors.Element(fvOffset + k);
                    if (fv is null) continue;
                    var xv = fv.ReadVec3("four vectors x");
                    var yv = fv.ReadVec3("four vectors y");
                    var zv = fv.ReadVec3("four vectors z");
                    float xw = fv.ReadReal("havok w four vectors x") ?? 0.0f;
                    float yw = fv.ReadReal("havok w four vectors y") ?? 0.0f;
                    float zw = fv.ReadReal("havok w four vectors z") ?? 0.0f;
                    verts.Add(new RealPoint3d(xv.I, yv.I, zv.I).Mul(Geometry.Scale));
                    verts.Add(new RealPoint3d(xv.J, yv.J, zv.J).Mul(Geometry.Scale));
                    verts.Add(new RealPoint3d(xv.K, yv.K, zv.K).Mul(Geometry.Scale));
                    verts.Add(new RealPoint3d(xw, yw, zw).Mul(Geometry.Scale));
                }
            }
            // Dedupe by bit pattern (the 4-vector packing leaves padding).
            var seen = new HashSet<(uint, uint, uint)>();
            var deduped = new List<RealPoint3d>(verts.Count);
            foreach (var vtx in verts)
            {
                var key = (BitConverter.SingleToUInt32Bits(vtx.X),
                    BitConverter.SingleToUInt32Bits(vtx.Y), BitConverter.SingleToUInt32Bits(vtx.Z));
                if (seen.Add(key)) deduped.Add(vtx);
            }
            outList.Add(new JmsConvex(
                bse.ReadStringId("name") ?? "",
                ParentFor(parents, ShapePolyhedron, i),
                (int)(bse.ReadIntAny("material") ?? 0),
                new RealQuaternion(0, 0, 0, 1),
                default,
                deduped));
            fvOffset += fvSize;
        }
        return outList;
    }

    private static List<JmsRagdoll> ReadPhmoRagdolls(TagStruct root)
    {
        var block = root.FieldPath("ragdoll constraints")?.AsBlock();
        if (block is null) return new();
        var outList = new List<JmsRagdoll>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var r = block.Element(i)!;
            var bodies = r.Field("constraint bodies")?.AsStruct();
            if (bodies is null) continue;
            var (aRot, aTrans) = ConstraintFrame(bodies, "a");
            var (bRot, bTrans) = ConstraintFrame(bodies, "b");
            outList.Add(new JmsRagdoll
            {
                Name = bodies.ReadStringId("name") ?? "",
                Attached = (int)(bodies.ReadIntAny("node a") ?? -1),
                Referenced = (int)(bodies.ReadIntAny("node b") ?? -1),
                AttachedRotation = aRot.Neg(),
                AttachedTranslation = aTrans,
                ReferencedRotation = bRot.Neg(),
                ReferencedTranslation = bTrans,
                MinTwist = r.ReadReal("min twist") ?? 0.0f,
                MaxTwist = r.ReadReal("max twist") ?? 0.0f,
                MinCone = r.ReadReal("min cone") ?? 0.0f,
                MaxCone = r.ReadReal("max cone") ?? 0.0f,
                MinPlane = r.ReadReal("min plane") ?? 0.0f,
                MaxPlane = r.ReadReal("max plane") ?? 0.0f,
                // MCC schema carries the typo `max friciton torque`.
                FrictionLimit = r.ReadReal("max friciton torque") ?? r.ReadReal("max friction torque") ?? 0.0f,
            });
        }
        return outList;
    }

    private static List<JmsHinge> ReadPhmoHinges(TagStruct root, bool limited)
    {
        string blockName = limited ? "limited hinge constraints" : "hinge constraints";
        var block = root.FieldPath(blockName)?.AsBlock();
        if (block is null) return new();
        var outList = new List<JmsHinge>(block.Count);
        for (int i = 0; i < block.Count; i++)
        {
            var h = block.Element(i)!;
            var bodies = h.Field("constraint bodies")?.AsStruct();
            if (bodies is null) continue;
            var (aRot, aTrans) = ConstraintFrame(bodies, "a");
            var (bRot, bTrans) = ConstraintFrame(bodies, "b");
            outList.Add(new JmsHinge
            {
                Name = bodies.ReadStringId("name") ?? "",
                BodyA = (int)(bodies.ReadIntAny("node a") ?? -1),
                BodyB = (int)(bodies.ReadIntAny("node b") ?? -1),
                ARotation = aRot,
                ATranslation = aTrans,
                BRotation = bRot,
                BTranslation = bTrans,
                IsLimited = limited ? 1 : 0,
                FrictionLimit = h.ReadReal("limit friction") ?? 0.0f,
                MinAngle = h.ReadReal("limit min angle") ?? 0.0f,
                MaxAngle = h.ReadReal("limit max angle") ?? 0.0f,
            });
        }
        return outList;
    }

    private static (RealQuaternion, RealPoint3d) ConstraintFrame(TagStruct bodies, string side)
    {
        var f = bodies.ReadVec3($"{side} forward");
        var l = bodies.ReadVec3($"{side} left");
        var u = bodies.ReadVec3($"{side} up");
        var p = bodies.ReadPoint3d($"{side} position");
        return (MathExtensions.FromBasisColumns(f, l, u), p.Mul(Geometry.Scale));
    }

    private static RealQuaternion RotationFromBasis(TagStruct cts)
    {
        var rowI = cts.ReadVec3("rotation i");
        var rowJ = cts.ReadVec3("rotation j");
        var rowK = cts.ReadVec3("rotation k");
        return MathExtensions.FromBasisColumns(
            new RealVector3d(rowI.I, rowJ.I, rowK.I),
            new RealVector3d(rowI.J, rowJ.J, rowK.J),
            new RealVector3d(rowI.K, rowJ.K, rowK.K));
    }

    //================================================================
    // helpers
    //================================================================

    /// <summary>Find an existing <c>(shader, perm-region)</c> cell or append
    /// a new material with a 1-based slot number; returns its index.</summary>
    private static int FindOrAddMaterial(List<JmsMaterial> materials, string shaderName, string cellLabel)
    {
        for (int i = 0; i < materials.Count; i++)
            if (materials[i].Name == shaderName && materials[i].MaterialName.EndsWith(cellLabel, StringComparison.Ordinal))
                return i;
        int slot = materials.Count + 1;
        materials.Add(new JmsMaterial(shaderName, $"({slot}) {cellLabel}"));
        return materials.Count - 1;
    }

    /// <summary>Rust <c>Path::file_stem</c> semantics on a backslash-or-slash
    /// path: the basename minus a final extension, "default" when empty.</summary>
    private static string FileStem(string raw)
    {
        string p = raw.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        string name = slash >= 0 ? p[(slash + 1)..] : p;
        if (name.Length == 0) return "default";
        int dot = name.LastIndexOf('.');
        string stem = dot <= 0 ? name : name[..dot];
        return stem.Length == 0 ? "default" : stem;
    }

    private static InvalidOperationException Missing(string path) =>
        new($"render_model is missing required field: {path}");

    //================================================================
    // 8213 text writer
    //================================================================

    /// <summary>Write the JMS as version-8213 text into <paramref name="w"/>.
    /// Newlines are LF and floats are fixed 10-place — matching the Rust
    /// writer byte-for-byte.</summary>
    public void Write(Stream stream)
    {
        using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1 << 16, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = false,
        };
        Write(w);
        w.Flush();
    }

    /// <summary>Render the full JMS text to a string (LF newlines).</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        using var w = new StringWriter(sb) { NewLine = "\n" };
        Write(w);
        return sb.ToString();
    }

    private void Write(TextWriter w)
    {
        void L(string s) { w.Write(s); w.Write('\n'); }
        void Blank() => w.Write('\n');

        L(";### VERSION ###");
        L("8213");
        Blank();

        L(";### NODES ###");
        L(I(Nodes.Count));
        L(";\t<name>");
        L(";\t<parent node index>");
        L(";\t<default rotation <i,j,k,w>>");
        L(";\t<default translation <x,y,z>>");
        Blank();
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            L($";NODE {i}");
            L(n.Name);
            L(I(n.Parent));
            WriteFloats(w, n.Rotation.ToArray());
            WriteFloats(w, n.Translation.ToArray());
            Blank();
        }

        L(";### MATERIALS ###");
        L(I(Materials.Count));
        L(";\t<name>");
        L(";\t<material name>");
        Blank();
        for (int i = 0; i < Materials.Count; i++)
        {
            L($";MATERIAL {i}");
            L(Materials[i].Name);
            L(Materials[i].MaterialName);
            Blank();
        }

        L(";### MARKERS ###");
        L(I(Markers.Count));
        L(";\t<name>");
        L(";\t<node index>");
        L(";\t<rotation <i,j,k,w>>");
        L(";\t<translation <x,y,z>>");
        L(";\t<radius>");
        Blank();
        for (int i = 0; i < Markers.Count; i++)
        {
            var m = Markers[i];
            L($";MARKER {i}");
            L(m.Name);
            L(I(m.NodeIndex));
            WriteFloats(w, m.Rotation.ToArray());
            WriteFloats(w, m.Translation.ToArray());
            WriteFloats(w, [m.Radius]);
            Blank();
        }

        L(";### INSTANCE XREF PATHS ###");
        L("0");
        L(";\t<path>");
        L(";\t<name>");
        Blank();

        L(";### INSTANCE MARKERS ###");
        L("0");
        L(";\t<name>");
        L(";\t<unique identifier>");
        L(";\t<path index>");
        L(";\t<rotation <i,j,k,w>>");
        L(";\t<translation <x,y,z>>");
        Blank();

        L(";### VERTICES ###");
        L(I(Vertices.Count));
        L(";\t<position>");
        L(";\t<normal>");
        L(";\t<node influences count>");
        L(";\t\t<node influences <index, weight>>");
        L(";\t\t<...>");
        L(";\t<texture coordinate count>");
        L(";\t\t<texture coordinates <u,v>>");
        L(";\t\t<...>");
        L(";\t\t<vertex color <r,g,b>>");
        L(";\t\t<...>");
        Blank();
        for (int i = 0; i < Vertices.Count; i++)
        {
            var v = Vertices[i];
            L($";VERTEX {i}");
            WriteFloats(w, v.Position.ToArray());
            WriteFloats(w, v.Normal.ToArray());
            L(I(v.NodeSets.Count));
            foreach (var (idx, wt) in v.NodeSets)
            {
                L(I(idx));
                WriteFloats(w, [wt]);
            }
            L(I(v.Uvs.Count));
            foreach (var uv in v.Uvs) WriteFloats(w, uv.ToArray());
            WriteFloats(w, [0.0f, 0.0f, 0.0f]); // vertex color always zero per TagTool
            Blank();
        }

        L(";### TRIANGLES ###");
        L(I(Triangles.Count));
        L(";\t<material index>");
        L(";\t<vertex indices <v0,v1,v2>>");
        Blank();
        for (int i = 0; i < Triangles.Count; i++)
        {
            var t = Triangles[i];
            L($";TRIANGLE {i}");
            L(I(t.Material));
            L($"{t.V0}\t{t.V1}\t{t.V2}");
            Blank();
        }

        L(";### SPHERES ###");
        L(I(Spheres.Count));
        foreach (var h in new[] { "<name>", "<parent>", "<material>", "<rotation <i,j,k,w>>", "<translation <x,y,z>>", "<radius>" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < Spheres.Count; i++)
        {
            var s = Spheres[i];
            L($";SPHERE {i}");
            L(s.Name);
            L(I(s.Parent));
            L(I(s.Material));
            WriteFloats(w, s.Rotation.ToArray());
            WriteFloats(w, s.Translation.ToArray());
            WriteFloats(w, [s.Radius]);
            Blank();
        }

        L(";### BOXES ###");
        L(I(Boxes.Count));
        foreach (var h in new[] { "<name>", "<parent>", "<material>", "<rotation <i,j,k,w>>", "<translation <x,y,z>>", "<width (x)>", "<length (y)>", "<height (z)>" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < Boxes.Count; i++)
        {
            var b = Boxes[i];
            L($";BOX {i}");
            L(b.Name);
            L(I(b.Parent));
            L(I(b.Material));
            WriteFloats(w, b.Rotation.ToArray());
            WriteFloats(w, b.Translation.ToArray());
            WriteFloats(w, [b.Width]);
            WriteFloats(w, [b.Length]);
            WriteFloats(w, [b.Height]);
            Blank();
        }

        L(";### CAPSULES ###");
        L(I(Capsules.Count));
        foreach (var h in new[] { "<name>", "<parent>", "<material>", "<rotation <i,j,k,w>>", "<translation <x,y,z>>", "<height>", "<radius>" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < Capsules.Count; i++)
        {
            var c = Capsules[i];
            L($";CAPSULE {i}");
            L(c.Name);
            L(I(c.Parent));
            L(I(c.Material));
            WriteFloats(w, c.Rotation.ToArray());
            WriteFloats(w, c.Translation.ToArray());
            WriteFloats(w, [c.Height]);
            WriteFloats(w, [c.Radius]);
            Blank();
        }

        L(";### CONVEX SHAPES ###");
        L(I(ConvexShapes.Count));
        foreach (var h in new[] { "<name>", "<parent>", "<material>", "<rotation <i,j,k,w>>", "<translation <x,y,z>>", "<vertex count>", "<...vertices>" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < ConvexShapes.Count; i++)
        {
            var c = ConvexShapes[i];
            L($";CONVEX SHAPE {i}");
            L(c.Name);
            L(I(c.Parent));
            L(I(c.Material));
            WriteFloats(w, c.Rotation.ToArray());
            WriteFloats(w, c.Translation.ToArray());
            L(I(c.Vertices.Count));
            foreach (var v in c.Vertices) WriteFloats(w, v.ToArray());
            Blank();
        }

        L(";### RAGDOLLS ###");
        L(I(Ragdolls.Count));
        foreach (var h in new[] { "<name>", "<attached index>", "<referenced index>", "<attached transform>", "<reference transform>", "<min twist>", "<max twist>", "<min cone>", "<max cone>", "<min plane>", "<max plane>", "<friction limit>" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < Ragdolls.Count; i++)
        {
            var r = Ragdolls[i];
            L($";RAGDOLL {i}");
            L(r.Name);
            L(I(r.Attached));
            L(I(r.Referenced));
            WriteFloats(w, r.AttachedRotation.ToArray());
            WriteFloats(w, r.AttachedTranslation.ToArray());
            WriteFloats(w, r.ReferencedRotation.ToArray());
            WriteFloats(w, r.ReferencedTranslation.ToArray());
            WriteFloats(w, [r.MinTwist]);
            WriteFloats(w, [r.MaxTwist]);
            WriteFloats(w, [r.MinCone]);
            WriteFloats(w, [r.MaxCone]);
            WriteFloats(w, [r.MinPlane]);
            WriteFloats(w, [r.MaxPlane]);
            WriteFloats(w, [r.FrictionLimit]);
            Blank();
        }

        L(";### HINGES ###");
        L(I(Hinges.Count));
        foreach (var h in new[] { "<name>", "<body A index>", "<body B index>", "<body A transform>", "<body B transform>", "<is limited>", "<friction limit>", "<min angle>", "<max angle" })
            L($";\t{h}");
        Blank();
        for (int i = 0; i < Hinges.Count; i++)
        {
            var h = Hinges[i];
            L($";HINGE {i}");
            L(h.Name);
            L(I(h.BodyA));
            L(I(h.BodyB));
            WriteFloats(w, h.ARotation.ToArray());
            WriteFloats(w, h.ATranslation.ToArray());
            WriteFloats(w, h.BRotation.ToArray());
            WriteFloats(w, h.BTranslation.ToArray());
            L(I(h.IsLimited));
            WriteFloats(w, [h.FrictionLimit]);
            WriteFloats(w, [h.MinAngle]);
            WriteFloats(w, [h.MaxAngle]);
            Blank();
        }

        foreach (var (name, helps) in EmptySectionsTrailing)
        {
            L($";### {name} ###");
            L("0");
            foreach (var h in helps) L($";\t{h}");
            Blank();
        }
        Blank();
    }

    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static void WriteFloats(TextWriter w, float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i] == 0f ? 0f : values[i]; // normalize -0.0 → 0.0
            w.Write(v.ToString("F10", CultureInfo.InvariantCulture));
            w.Write(i + 1 < values.Length ? '\t' : '\n');
        }
    }

    private static readonly (string Name, string[] Helps)[] EmptySectionsTrailing =
    [
        ("CAR_WHEEL", ["<name>", "<chassis index>", "<wheel index>", "<chassis transform>", "<wheel transform>", "<suspension transform>", "<suspension min limit>", "<suspension max limit>"]),
        ("POINT_TO_POINT", ["<name>", "<body A index>", "<body B index>", "<body A transform>", "<body B transform>", "<constraint type>", "<x min>", "<x max>", "<y min>", "<y max>", "<z min>", "<z max>", "<spring length>"]),
        ("PRISMATIC", ["<name>", "<body A index>", "<body B index>", "<body A transform>", "<body B transform>", "<is limited>", "<friction limit>", "<min limit>", "<max limit>"]),
        ("BOUNDING SPHERE", ["<translation <x,y,z>>", "<radius>"]),
        ("SKYLIGHT", ["<direction <x,y,z>>", "<radiant intensity <x,y,z>>", "<solid angle>"]),
    ];
}

//================================================================
// JMS value records
//================================================================

/// <summary>JMS skeletal node (bone). <c>Parent = -1</c> for roots.</summary>
public readonly record struct JmsNode(string Name, short Parent, RealQuaternion Rotation, RealPoint3d Translation);

/// <summary>JMS material — shader basename + <c>(slot) perm region</c> cell.</summary>
public readonly record struct JmsMaterial(string Name, string MaterialName);

/// <summary>JMS marker (one per marker_group variant). Radius <c>-1</c> = unset.</summary>
public readonly record struct JmsMarker(string Name, short NodeIndex, RealQuaternion Rotation, RealPoint3d Translation, float Radius);

/// <summary>JMS vertex — JMS doesn't share verts; each triangle owns three.</summary>
public sealed class JmsVertex
{
    public RealPoint3d Position { get; set; }
    public RealVector3d Normal { get; set; }
    public List<(short Index, float Weight)> NodeSets { get; init; } = new();
    public List<RealPoint2d> Uvs { get; init; } = new();
}

/// <summary>JMS triangle: material slot + three vertex indices.</summary>
public readonly record struct JmsTriangle(int Material, uint V0, uint V1, uint V2);

public readonly record struct JmsSphere(string Name, int Parent, int Material, RealQuaternion Rotation, RealPoint3d Translation, float Radius);

public readonly record struct JmsBox(string Name, int Parent, int Material, RealQuaternion Rotation, RealPoint3d Translation, float Width, float Length, float Height);

public readonly record struct JmsCapsule(string Name, int Parent, int Material, RealQuaternion Rotation, RealPoint3d Translation, float Height, float Radius);

public readonly record struct JmsConvex(string Name, int Parent, int Material, RealQuaternion Rotation, RealPoint3d Translation, List<RealPoint3d> Vertices);

public sealed record JmsRagdoll
{
    public string Name { get; init; } = "";
    public int Attached { get; init; }
    public int Referenced { get; init; }
    public RealQuaternion AttachedRotation { get; init; }
    public RealPoint3d AttachedTranslation { get; init; }
    public RealQuaternion ReferencedRotation { get; init; }
    public RealPoint3d ReferencedTranslation { get; init; }
    public float MinTwist { get; init; }
    public float MaxTwist { get; init; }
    public float MinCone { get; init; }
    public float MaxCone { get; init; }
    public float MinPlane { get; init; }
    public float MaxPlane { get; init; }
    public float FrictionLimit { get; init; }
}

public sealed record JmsHinge
{
    public string Name { get; init; } = "";
    public int BodyA { get; init; }
    public int BodyB { get; init; }
    public RealQuaternion ARotation { get; init; }
    public RealPoint3d ATranslation { get; init; }
    public RealQuaternion BRotation { get; init; }
    public RealPoint3d BTranslation { get; init; }
    public int IsLimited { get; init; }
    public float FrictionLimit { get; init; }
    public float MinAngle { get; init; }
    public float MaxAngle { get; init; }
}
