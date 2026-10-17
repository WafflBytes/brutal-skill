using Brutal.Numerics;

namespace BrutalGame3D.Platform;

/// <summary>
/// Column-major 4x4 matrix with OpenGL conventions (column vectors, right-handed, clip z in [-1, 1]). Hand-written
/// because the skill notes Numerics' float4x4 convention isn't verified for GL upload. M[c * 4 + r].
/// </summary>
public unsafe struct Mat4
{
    public fixed float M[16];

    public static Mat4 Identity
    {
        get
        {
            var m = new Mat4();
            m.M[0] = m.M[5] = m.M[10] = m.M[15] = 1;
            return m;
        }
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new Mat4();
        for (var c = 0; c < 4; c++)
        for (var row = 0; row < 4; row++)
        {
            var s = 0f;
            for (var k = 0; k < 4; k++)
                s += a.M[k * 4 + row] * b.M[c * 4 + k];
            r.M[c * 4 + row] = s;
        }
        return r;
    }

    public static Mat4 Perspective(float fovY, float aspect, float near, float far)
    {
        var f = 1 / MathF.Tan(fovY / 2);
        var m = new Mat4();
        m.M[0] = f / aspect;
        m.M[5] = f;
        m.M[10] = (far + near) / (near - far);
        m.M[11] = -1;
        m.M[14] = 2 * far * near / (near - far);
        return m;
    }

    public static Mat4 Ortho(float left, float right, float bottom, float top)
    {
        var m = Identity;
        m.M[0] = 2 / (right - left);
        m.M[5] = 2 / (top - bottom);
        m.M[10] = -1;
        m.M[12] = -(right + left) / (right - left);
        m.M[13] = -(top + bottom) / (top - bottom);
        return m;
    }

    public static Mat4 LookAt(float3 eye, float3 target, float3 up)
    {
        var f = float3.NormalizeOrZero(target - eye);
        var s = float3.NormalizeOrZero(float3.Cross(f, up));
        var u = float3.Cross(s, f);
        var m = Identity;
        m.M[0] = s.X;
        m.M[4] = s.Y;
        m.M[8] = s.Z;
        m.M[1] = u.X;
        m.M[5] = u.Y;
        m.M[9] = u.Z;
        m.M[2] = -f.X;
        m.M[6] = -f.Y;
        m.M[10] = -f.Z;
        m.M[12] = -float3.Dot(s, eye);
        m.M[13] = -float3.Dot(u, eye);
        m.M[14] = float3.Dot(f, eye);
        return m;
    }

    /// <summary>Translation * Rotation(q) * Scale.</summary>
    public static Mat4 Trs(float3 t, floatQuat q, float3 s)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        var m = Identity;
        m.M[0] = (1 - 2 * (y * y + z * z)) * s.X;
        m.M[1] = 2 * (x * y + z * w) * s.X;
        m.M[2] = 2 * (x * z - y * w) * s.X;
        m.M[4] = 2 * (x * y - z * w) * s.Y;
        m.M[5] = (1 - 2 * (x * x + z * z)) * s.Y;
        m.M[6] = 2 * (y * z + x * w) * s.Y;
        m.M[8] = 2 * (x * z + y * w) * s.Z;
        m.M[9] = 2 * (y * z - x * w) * s.Z;
        m.M[10] = (1 - 2 * (x * x + y * y)) * s.Z;
        m.M[12] = t.X;
        m.M[13] = t.Y;
        m.M[14] = t.Z;
        return m;
    }

    public float3 TransformPoint(float3 p)
    {
        var x = M[0] * p.X + M[4] * p.Y + M[8] * p.Z + M[12];
        var y = M[1] * p.X + M[5] * p.Y + M[9] * p.Z + M[13];
        var z = M[2] * p.X + M[6] * p.Y + M[10] * p.Z + M[14];
        var w = M[3] * p.X + M[7] * p.Y + M[11] * p.Z + M[15];
        return new float3(x, y, z) / w;
    }
}
