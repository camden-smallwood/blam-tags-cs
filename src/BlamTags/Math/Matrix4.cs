namespace BlamTags;

/// <summary>Row-major 4×4 affine matrix — a port of the Rust <c>Matrix4</c>
/// (<c>math.rs</c>). <c>A * B</c> then <see cref="Decompose"/> matches
/// Foundry's <c>A @ B</c>. Used for object-space pose-overlay corrections,
/// which need full forward-kinematic matrices (translation + rotation +
/// uniform scale) per bone.</summary>
public readonly struct Matrix4
{
    public readonly float[,] M;

    private Matrix4(float[,] m) => M = m;

    public static readonly Matrix4 Identity = new(new float[,]
    {
        { 1, 0, 0, 0 },
        { 0, 1, 0, 0 },
        { 0, 0, 1, 0 },
        { 0, 0, 0, 1 },
    });

    /// <summary>Build <c>T * R * S</c> from a translation, rotation
    /// quaternion and a <b>uniform</b> scale — Blender's
    /// <c>Matrix.LocRotScale(loc, rot, Vector.Fill(3, scale))</c>.</summary>
    public static Matrix4 FromLocRotScale(RealPoint3d t, RealQuaternion q, float s)
    {
        var r = q.Normalized();
        float x = r.I, y = r.J, z = r.K, w = r.W;
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;
        float r00 = 1.0f - 2.0f * (yy + zz);
        float r01 = 2.0f * (xy - wz);
        float r02 = 2.0f * (xz + wy);
        float r10 = 2.0f * (xy + wz);
        float r11 = 1.0f - 2.0f * (xx + zz);
        float r12 = 2.0f * (yz - wx);
        float r20 = 2.0f * (xz - wy);
        float r21 = 2.0f * (yz + wx);
        float r22 = 1.0f - 2.0f * (xx + yy);
        return new Matrix4(new float[,]
        {
            { r00 * s, r01 * s, r02 * s, t.X },
            { r10 * s, r11 * s, r12 * s, t.Y },
            { r20 * s, r21 * s, r22 * s, t.Z },
            { 0, 0, 0, 1 },
        });
    }

    /// <summary>Affine inverse. Assumes the bottom row is
    /// <c>[0,0,0,1]</c> (always true for products of
    /// <see cref="FromLocRotScale"/> matrices).</summary>
    public Matrix4 Inverse()
    {
        var a = M;
        float a00 = a[0, 0], a01 = a[0, 1], a02 = a[0, 2];
        float a10 = a[1, 0], a11 = a[1, 1], a12 = a[1, 2];
        float a20 = a[2, 0], a21 = a[2, 1], a22 = a[2, 2];
        float det = a00 * (a11 * a22 - a12 * a21) - a01 * (a10 * a22 - a12 * a20)
            + a02 * (a10 * a21 - a11 * a20);
        if (System.Math.Abs(det) < 1e-20f) return Identity;
        float inv = 1.0f / det;
        float i00 = (a11 * a22 - a12 * a21) * inv;
        float i01 = (a02 * a21 - a01 * a22) * inv;
        float i02 = (a01 * a12 - a02 * a11) * inv;
        float i10 = (a12 * a20 - a10 * a22) * inv;
        float i11 = (a00 * a22 - a02 * a20) * inv;
        float i12 = (a02 * a10 - a00 * a12) * inv;
        float i20 = (a10 * a21 - a11 * a20) * inv;
        float i21 = (a01 * a20 - a00 * a21) * inv;
        float i22 = (a00 * a11 - a01 * a10) * inv;
        float tx = a[0, 3], ty = a[1, 3], tz = a[2, 3];
        float nx = -(i00 * tx + i01 * ty + i02 * tz);
        float ny = -(i10 * tx + i11 * ty + i12 * tz);
        float nz = -(i20 * tx + i21 * ty + i22 * tz);
        return new Matrix4(new float[,]
        {
            { i00, i01, i02, nx },
            { i10, i11, i12, ny },
            { i20, i21, i22, nz },
            { 0, 0, 0, 1 },
        });
    }

    /// <summary>Decompose into <c>(translation, rotation, uniform scale)</c>.
    /// The uniform scale is the mean of the three column lengths.</summary>
    public (RealPoint3d Translation, RealQuaternion Rotation, float Scale) Decompose()
    {
        var a = M;
        var t = new RealPoint3d(a[0, 3], a[1, 3], a[2, 3]);
        float sx = (float)System.Math.Sqrt(a[0, 0] * a[0, 0] + a[1, 0] * a[1, 0] + a[2, 0] * a[2, 0]);
        float sy = (float)System.Math.Sqrt(a[0, 1] * a[0, 1] + a[1, 1] * a[1, 1] + a[2, 1] * a[2, 1]);
        float sz = (float)System.Math.Sqrt(a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2] + a[2, 2] * a[2, 2]);
        float scale = (sx + sy + sz) / 3.0f;
        float ix = SafeDiv(sx), iy = SafeDiv(sy), iz = SafeDiv(sz);
        float r00 = a[0, 0] * ix, r01 = a[0, 1] * iy, r02 = a[0, 2] * iz;
        float r10 = a[1, 0] * ix, r11 = a[1, 1] * iy, r12 = a[1, 2] * iz;
        float r20 = a[2, 0] * ix, r21 = a[2, 1] * iy, r22 = a[2, 2] * iz;
        var q = QuatFromMat3(r00, r01, r02, r10, r11, r12, r20, r21, r22);
        return (t, q, scale);
    }

    public static Matrix4 operator *(Matrix4 lhs, Matrix4 rhs)
    {
        var outM = new float[4, 4];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                outM[r, c] = lhs.M[r, 0] * rhs.M[0, c]
                    + lhs.M[r, 1] * rhs.M[1, c]
                    + lhs.M[r, 2] * rhs.M[2, c]
                    + lhs.M[r, 3] * rhs.M[3, c];
        return new Matrix4(outM);
    }

    private static float SafeDiv(float s) => System.Math.Abs(s) < 1e-12f ? 0.0f : 1.0f / s;

    /// <summary>Quaternion from a 3×3 rotation matrix (row-major) —
    /// standard Shepperd's method.</summary>
    private static RealQuaternion QuatFromMat3(
        float r00, float r01, float r02,
        float r10, float r11, float r12,
        float r20, float r21, float r22)
    {
        float trace = r00 + r11 + r22;
        RealQuaternion q;
        if (trace > 0.0f)
        {
            float s = (float)System.Math.Sqrt(trace + 1.0f) * 2.0f;
            q = new RealQuaternion((r21 - r12) / s, (r02 - r20) / s, (r10 - r01) / s, 0.25f * s);
        }
        else if (r00 > r11 && r00 > r22)
        {
            float s = (float)System.Math.Sqrt(1.0f + r00 - r11 - r22) * 2.0f;
            q = new RealQuaternion(0.25f * s, (r01 + r10) / s, (r02 + r20) / s, (r21 - r12) / s);
        }
        else if (r11 > r22)
        {
            float s = (float)System.Math.Sqrt(1.0f + r11 - r00 - r22) * 2.0f;
            q = new RealQuaternion((r01 + r10) / s, 0.25f * s, (r12 + r21) / s, (r02 - r20) / s);
        }
        else
        {
            float s = (float)System.Math.Sqrt(1.0f + r22 - r00 - r11) * 2.0f;
            q = new RealQuaternion((r02 + r20) / s, (r12 + r21) / s, 0.25f * s, (r10 - r01) / s);
        }
        return q.Normalized();
    }
}
