namespace BlamTags;

/// <summary>
/// Vector / point / quaternion algebra used by the geometry extractors
/// (JMS, ASS). A direct port of the <c>impl</c> blocks in the Rust
/// <c>math.rs</c> — same formulas, same <c>f32</c> operation order, so
/// extracted coordinates match the oracle bit-for-bit.
///
/// The Rust side enforces point-vs-vector semantics through the type
/// system (<c>Point + Vector → Point</c>, <c>Point − Point → Vector</c>);
/// here the readonly-record-struct math types from <c>MathTypes.cs</c>
/// carry no operators, so these are spelled out as named extension
/// methods. Names mirror the Rust method names.
/// </summary>
internal static class MathExtensions
{
    //==== RealVector3d ====

    public static float Dot(this RealVector3d a, RealVector3d b) => a.I * b.I + a.J * b.J + a.K * b.K;

    public static RealVector3d Cross(this RealVector3d a, RealVector3d b) => new(
        a.J * b.K - a.K * b.J,
        a.K * b.I - a.I * b.K,
        a.I * b.J - a.J * b.I);

    public static float LengthSquared(this RealVector3d v) => v.Dot(v);
    public static float Length(this RealVector3d v) => MathF.Sqrt(v.LengthSquared());

    public static RealVector3d Normalized(this RealVector3d v)
    {
        float m = v.Length();
        return m < 1e-12f ? default : new RealVector3d(v.I / m, v.J / m, v.K / m);
    }

    public static RealPoint3d AsPoint(this RealVector3d v) => new(v.I, v.J, v.K);
    public static float[] ToArray(this RealVector3d v) => [v.I, v.J, v.K];

    public static RealVector3d Add(this RealVector3d a, RealVector3d b) => new(a.I + b.I, a.J + b.J, a.K + b.K);
    public static RealVector3d Sub(this RealVector3d a, RealVector3d b) => new(a.I - b.I, a.J - b.J, a.K - b.K);
    public static RealVector3d Mul(this RealVector3d v, float k) => new(v.I * k, v.J * k, v.K * k);
    public static RealVector3d Neg(this RealVector3d v) => new(-v.I, -v.J, -v.K);
    public static bool IsZero(this RealVector3d v) => v.I == 0f && v.J == 0f && v.K == 0f;

    //==== RealPoint3d ====

    public static RealVector3d AsVector(this RealPoint3d p) => new(p.X, p.Y, p.Z);
    public static float[] ToArray(this RealPoint3d p) => [p.X, p.Y, p.Z];

    /// <summary>Translate a point by a vector (<c>Point + Vector → Point</c>).</summary>
    public static RealPoint3d Add(this RealPoint3d p, RealVector3d v) => new(p.X + v.I, p.Y + v.J, p.Z + v.K);

    /// <summary>Displacement between two points (<c>Point − Point → Vector</c>).</summary>
    public static RealVector3d Sub(this RealPoint3d a, RealPoint3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static RealPoint3d Mul(this RealPoint3d p, float k) => new(p.X * k, p.Y * k, p.Z * k);

    public static float DistanceSquaredTo(this RealPoint3d a, RealPoint3d b) => a.Sub(b).LengthSquared();

    //==== RealPlane3d ====

    public static RealVector3d Normal(this RealPlane3d p) => new(p.I, p.J, p.K);

    /// <summary>Cramer's-rule intersection of three planes (Halo's
    /// <c>n·p + d = 0</c> convention), or null when they don't meet at a
    /// single point.</summary>
    public static RealPoint3d? TripleIntersection(RealPlane3d a, RealPlane3d b, RealPlane3d c)
    {
        var n1 = a.Normal();
        var n2 = b.Normal();
        var n3 = c.Normal();
        float det = n1.Dot(n2.Cross(n3));
        if (MathF.Abs(det) < 1e-9f) return null;
        var p = n2.Cross(n3).Mul(-a.D)
            .Add(n3.Cross(n1).Mul(-b.D))
            .Add(n1.Cross(n2).Mul(-c.D))
            .Mul(1.0f / det);
        return new RealPoint3d(p.I, p.J, p.K);
    }

    //==== RealPoint2d ====

    public static float[] ToArray(this RealPoint2d p) => [p.X, p.Y];

    //==== RealQuaternion ====

    public static float Dot(this RealQuaternion a, RealQuaternion b) =>
        a.I * b.I + a.J * b.J + a.K * b.K + a.W * b.W;

    public static float[] ToArray(this RealQuaternion q) => [q.I, q.J, q.K, q.W];

    public static RealQuaternion Conjugate(this RealQuaternion q) => new(-q.I, -q.J, -q.K, q.W);
    public static RealQuaternion Neg(this RealQuaternion q) => new(-q.I, -q.J, -q.K, -q.W);

    public static float LengthSquared(this RealQuaternion q) => q.Dot(q);

    /// <summary>Unit-normalize. Returns the input unchanged on a
    /// zero/non-finite magnitude (mirrors Rust's <c>fast_quaternion_normalize</c>).</summary>
    public static RealQuaternion Normalized(this RealQuaternion q)
    {
        float mag2 = q.LengthSquared();
        if (mag2 <= 0.0f || !float.IsFinite(mag2)) return q;
        float inv = 1.0f / MathF.Sqrt(mag2);
        return new RealQuaternion(q.I * inv, q.J * inv, q.K * inv, q.W * inv);
    }

    /// <summary>Normalized linear interpolation, short-arc: flips <paramref name="b"/>
    /// when the dot is negative so the interpolation takes the shorter path.</summary>
    public static RealQuaternion Nlerp(this RealQuaternion a, RealQuaternion b, float t)
    {
        float dot = a.Dot(b);
        float s = dot < 0.0f ? -1.0f : 1.0f;
        float omt = 1.0f - t;
        return new RealQuaternion(
            a.I * omt + s * b.I * t,
            a.J * omt + s * b.J * t,
            a.K * omt + s * b.K * t,
            a.W * omt + s * b.W * t).Normalized();
    }

    /// <summary>Hamilton product <c>a * b</c>.</summary>
    public static RealQuaternion Mul(this RealQuaternion a, RealQuaternion b)
    {
        float ax = a.I, ay = a.J, az = a.K, aw = a.W;
        float bx = b.I, by = b.J, bz = b.K, bw = b.W;
        return new RealQuaternion(
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz);
    }

    /// <summary>Apply this rotation to a vector (two-cross-product form).</summary>
    public static RealVector3d Rotate(this RealQuaternion q, RealVector3d v)
    {
        var qv = new RealVector3d(q.I, q.J, q.K);
        var t = qv.Cross(v).Mul(2.0f);
        return v.Add(new RealVector3d(
            q.W * t.I + qv.J * t.K - qv.K * t.J,
            q.W * t.J + qv.K * t.I - qv.I * t.K,
            q.W * t.K + qv.I * t.J - qv.J * t.I));
    }

    private static readonly RealQuaternion Identity = new(0, 0, 0, 1);

    /// <summary>Quaternion from three orthonormal rotation-matrix column
    /// vectors. Trace-and-largest-diagonal extraction.</summary>
    public static RealQuaternion FromBasisColumns(RealVector3d c0, RealVector3d c1, RealVector3d c2)
    {
        float m00 = c0.I, m10 = c0.J, m20 = c0.K;
        float m01 = c1.I, m11 = c1.J, m21 = c1.K;
        float m02 = c2.I, m12 = c2.J, m22 = c2.K;
        float trace = m00 + m11 + m22;
        if (trace > 0.0f)
        {
            float s = MathF.Sqrt(trace + 1.0f) * 2.0f;
            return new RealQuaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
        }
        if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1.0f + m00 - m11 - m22) * 2.0f;
            return new RealQuaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        if (m11 > m22)
        {
            float s = MathF.Sqrt(1.0f + m11 - m00 - m22) * 2.0f;
            return new RealQuaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        else
        {
            float s = MathF.Sqrt(1.0f + m22 - m00 - m11) * 2.0f;
            return new RealQuaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
        }
    }

    /// <summary>Shortest-arc rotation between two vectors (TagTool's
    /// <c>QuaternionFromVector</c>). Degenerate inputs collapse to identity
    /// or a 180° rotation about an arbitrary perpendicular.</summary>
    public static RealQuaternion ShortestArc(RealVector3d from, RealVector3d to)
    {
        var toN = to.Normalized();
        if (toN.IsZero()) return Identity;
        var fromN = from.Normalized();
        if (fromN.IsZero()) return Identity;
        float dot = fromN.Dot(toN);
        if (dot > 0.999999f) return Identity;
        if (dot < -0.999999f)
        {
            var perp = MathF.Abs(fromN.I) < 0.9f
                ? new RealVector3d(1.0f, 0.0f, 0.0f)
                : new RealVector3d(0.0f, 1.0f, 0.0f);
            var axis = fromN.Cross(perp).Normalized();
            return new RealQuaternion(axis.I, axis.J, axis.K, 0.0f);
        }
        var cross = fromN.Cross(toN);
        float ss = MathF.Sqrt((1.0f + dot) * 2.0f);
        float invS = 1.0f / ss;
        return new RealQuaternion(cross.I * invS, cross.J * invS, cross.K * invS, ss * 0.5f);
    }
}
