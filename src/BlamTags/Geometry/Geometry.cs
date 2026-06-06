namespace BlamTags;

/// <summary>
/// Geometry primitives shared across the format-specific exporters (JMS,
/// and the future ASS). A port of the Rust <c>geometry.rs</c>:
/// <list type="bullet">
/// <item>compression-bounds dequantization for positions/texcoords,</item>
/// <item>restart-aware triangle-strip → list conversion,</item>
/// <item>the Halo BSP edge-ring walker (collision_model / sbsp).</item>
/// </list>
/// </summary>
internal static class Geometry
{
    /// <summary>World-units → centimeter scale used everywhere positions
    /// cross into the JMS / ASS artist source format.</summary>
    public const float Scale = 100.0f;

    //==== CompressionBounds ====

    /// <summary>Per-axis dequantization bounds for one
    /// <c>compression info[i]</c> entry. Components stored 0..1 are mapped
    /// back via <c>min + value*(max-min)</c>.</summary>
    public readonly struct CompressionBounds
    {
        public bool PosCompressed { get; init; }
        public bool UvCompressed { get; init; }
        public float PxMin { get; init; }
        public float PxMax { get; init; }
        public float PyMin { get; init; }
        public float PyMax { get; init; }
        public float PzMin { get; init; }
        public float PzMax { get; init; }
        public float UMin { get; init; }
        public float UMax { get; init; }
        public float VMin { get; init; }
        public float VMax { get; init; }

        /// <summary>Identity bounds — decompress is a passthrough.</summary>
        public static CompressionBounds Identity() => new()
        {
            PosCompressed = false,
            UvCompressed = false,
            PxMin = 0, PxMax = 1, PyMin = 0, PyMax = 1, PzMin = 0, PzMax = 1,
            UMin = 0, UMax = 1, VMin = 0, VMax = 1,
        };

        public RealPoint3d DecompressPosition(RealPoint3d p)
        {
            if (!PosCompressed) return p;
            return new RealPoint3d(
                PxMin + p.X * (PxMax - PxMin),
                PyMin + p.Y * (PyMax - PyMin),
                PzMin + p.Z * (PzMax - PzMin));
        }

        public RealPoint2d DecompressTexcoord(RealPoint2d uv)
        {
            if (!UvCompressed) return uv;
            return new RealPoint2d(
                UMin + uv.X * (UMax - UMin),
                VMin + uv.Y * (VMax - VMin));
        }
    }

    /// <summary>Read <c>render geometry/compression info[0]</c>.</summary>
    public static CompressionBounds ReadCompressionBounds(TagStruct root) =>
        ReadCompressionBoundsAt(root, 0);

    /// <summary>Read <c>render geometry/compression info[index]</c>, falling
    /// back to identity if the index is out of range.</summary>
    public static CompressionBounds ReadCompressionBoundsAt(TagStruct root, int index)
    {
        var ciBlock = root.FieldPath("render geometry/compression info")?.AsBlock();
        if (ciBlock is null || index >= ciBlock.Count) return CompressionBounds.Identity();
        var ci = ciBlock.Element(index)!;

        bool posCompressed = true, uvCompressed = true;
        if (ci.Field("compression flags")?.Value is TagFieldData.WordFlags wf)
        {
            posCompressed = (wf.Value & 0x0001) != 0;
            uvCompressed = (wf.Value & 0x0002) != 0;
        }

        // Six floats packed as the sequential tuple
        // [xmin, xmax, ymin, ymax, zmin, zmax] across two real_point_3d
        // fields — despite the field type, NOT a min/max corner pair.
        var pb0 = ci.ReadPoint3d("position bounds 0");
        var pb1 = ci.ReadPoint3d("position bounds 1");
        var tb0 = ci.Field("texcoord bounds 0")?.Value is TagFieldData.RealPoint2dValue t0
            ? t0.Value : new RealPoint2d(0.0f, 1.0f);
        var tb1 = ci.Field("texcoord bounds 1")?.Value is TagFieldData.RealPoint2dValue t1
            ? t1.Value : new RealPoint2d(0.0f, 1.0f);

        return new CompressionBounds
        {
            PosCompressed = posCompressed,
            UvCompressed = uvCompressed,
            PxMin = pb0.X, PxMax = pb0.Y,
            PyMin = pb0.Z, PyMax = pb1.X,
            PzMin = pb1.Y, PzMax = pb1.Z,
            UMin = tb0.X, UMax = tb0.Y,
            VMin = tb1.X, VMax = tb1.Y,
        };
    }

    //==== Triangle-strip → list ====

    /// <summary>Restart-aware (<c>0xFFFF</c>) triangle-strip decoder:
    /// splits on restart sentinels, flips winding parity per local
    /// position, drops degenerate windows.</summary>
    public static List<(uint A, uint B, uint C)> StripToList(ReadOnlySpan<ushort> strip)
    {
        var widened = new uint[strip.Length];
        for (int i = 0; i < strip.Length; i++) widened[i] = strip[i] == 0xFFFF ? 0xFFFFFFFFu : strip[i];
        return StripToListU32(widened);
    }

    /// <summary>u32 sibling of <see cref="StripToList(ReadOnlySpan{ushort})"/> —
    /// restart sentinel is <c>0xFFFFFFFF</c>. Used for meshes indexing
    /// &gt;65k vertices via <c>raw indices32</c>.</summary>
    public static List<(uint A, uint B, uint C)> StripToListU32(ReadOnlySpan<uint> strip)
    {
        var outList = new List<(uint, uint, uint)>(System.Math.Max(0, strip.Length - 2));
        int segStart = 0;
        for (int i = 0; i <= strip.Length; i++)
        {
            if (i == strip.Length || strip[i] == 0xFFFFFFFFu)
            {
                EmitSegment(strip.Slice(segStart, i - segStart), outList);
                segStart = i + 1;
            }
        }
        return outList;
    }

    private static void EmitSegment(ReadOnlySpan<uint> segment, List<(uint, uint, uint)> outList)
    {
        for (int i = 0; i < segment.Length - 2; i++)
        {
            uint a = segment[i], b = segment[i + 1], c = segment[i + 2];
            if (a == b || b == c || a == c) continue;
            outList.Add((i % 2 == 0) ? (a, b, c) : (a, c, b));
        }
    }

    //==== BSP edge-ring walker ====

    /// <summary>Cached row of a Halo BSP <c>edges[]</c> block — pre-cached
    /// once per BSP to keep the surface-ring walk out of the facade API.</summary>
    public readonly record struct EdgeRow(
        int StartVertex, int EndVertex, int ForwardEdge, int ReverseEdge, int LeftSurface, int RightSurface);

    /// <summary>Walk one surface's edge ring, returning the ordered bounding
    /// vertex indices. Each edge belongs to two surfaces; the matching side
    /// decides which vertex to emit and which neighbour edge to follow.
    /// Returns empty on malformed rings.</summary>
    public static List<int> WalkSurfaceRing(int surfaceIndex, int firstEdge, IReadOnlyList<EdgeRow> edges)
    {
        var outList = new List<int>();
        int current = firstEdge;
        int steps = 0;
        int maxSteps = edges.Count * 2 + 8;
        while (true)
        {
            if (current < 0 || current >= edges.Count) return [];
            var e = edges[current];
            int next;
            if (e.LeftSurface == surfaceIndex) { outList.Add(e.StartVertex); next = e.ForwardEdge; }
            else if (e.RightSurface == surfaceIndex) { outList.Add(e.EndVertex); next = e.ReverseEdge; }
            else return [];
            if (next == firstEdge) break;
            current = next;
            steps++;
            if (steps > maxSteps) return [];
        }
        return outList;
    }
}
