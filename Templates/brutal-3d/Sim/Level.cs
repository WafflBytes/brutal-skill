using Brutal.Numerics;

namespace BrutalGame3D.Sim;

public enum BlockKind
{
    Floor,
    Wall,
    Platform,
    Ramp,
}

/// <summary>A static oriented box: the only collision primitive the level uses (Jolt mesh shapes don't work, T36).</summary>
public readonly record struct Block(float3 Center, float3 HalfExtents, floatQuat Rotation, BlockKind Kind);

/// <summary>A point the test/demo autopilot steers through; <see cref="Coin"/> &gt;= 0 marks a coin to collect there.</summary>
public readonly record struct Waypoint(float2 XZ, int Coin = -1);

/// <summary>
/// The arena: floor, outer and inner walls, two raised platforms with ramps, and the coins. Pure data plus a little
/// geometry (ray casts against the boxes, used for coin placement and the ball's shadow). No SDL or Jolt types.
/// </summary>
public static class Level
{
    public const float HalfSize = 20f; // playable floor is [-20, 20] on x and z
    public const float WallHeight = 1.2f;
    public static readonly float3 BallStart = new(0, 0.5f, 4);

    public static readonly Block[] Blocks = BuildBlocks();
    public static readonly float2[] CoinXZ =
    [
        new(-6, 2), // 0
        new(-17, 0), // 1
        new(-14, 14), // 2
        new(-4, 14), // 3
        new(12, 14), // 4
        new(16, 2), // 5
        new(12, -2), // 6 on ramp A
        new(12, -12), // 7 platform A
        new(16, -16), // 8 platform A corner
        new(0, -6), // 9
        new(-2, -12), // 10 on ramp B
        new(-12, -12), // 11 platform B
        new(-15, -15), // 12 platform B corner
    ];

    public const float CoinHover = 0.65f; // coin centre above the surface under it
    public static readonly float3[] Coins = CoinXZ.Select(c => new float3(c.X, SurfaceHeight(c) + CoinHover, c.Y)).ToArray();

    /// <summary>A route that collects every coin; the self-test drives it to prove the level can be finished in time.</summary>
    public static readonly Waypoint[] Route =
    [
        new(new(-6, 2), 0),
        new(new(-17, 0), 1),
        new(new(-16.5f, 10)),
        new(new(-14, 14), 2),
        new(new(-4, 14), 3),
        new(new(6, 17.8f)),
        new(new(12, 14), 4),
        new(new(16, 2), 5),
        new(new(12, 5)),
        new(new(12, -2), 6),
        new(new(12, -8)),
        new(new(12, -12), 7),
        new(new(16, -16), 8),
        new(new(8, -12)),
        new(new(4, -8)),
        new(new(0, -6), 9),
        new(new(6, -12)),
        new(new(-2, -12), 10),
        new(new(-9, -12)),
        new(new(-12, -12), 11),
        new(new(-15, -15), 12),
    ];

    static Block[] BuildBlocks()
    {
        var b = new List<Block>();
        var id = floatQuat.Identity;
        // Floor: top surface at y = 0, a little larger than the walls.
        b.Add(new(new float3(0, -0.5f, 0), new float3(HalfSize + 1, 0.5f, HalfSize + 1), id, BlockKind.Floor));

        // Outer walls.
        var h = WallHeight / 2;
        b.Add(new(new float3(0, h, -HalfSize - 0.5f), new float3(HalfSize + 1, h, 0.5f), id, BlockKind.Wall));
        b.Add(new(new float3(0, h, HalfSize + 0.5f), new float3(HalfSize + 1, h, 0.5f), id, BlockKind.Wall));
        b.Add(new(new float3(-HalfSize - 0.5f, h, 0), new float3(0.5f, h, HalfSize), id, BlockKind.Wall));
        b.Add(new(new float3(HalfSize + 0.5f, h, 0), new float3(0.5f, h, HalfSize), id, BlockKind.Wall));

        // Inner walls in the south half.
        b.Add(new(new float3(-8, h, 8), new float3(6, h, 0.5f), id, BlockKind.Wall));
        b.Add(new(new float3(6, h, 11), new float3(0.5f, h, 5), id, BlockKind.Wall));

        // Platform A (north-east), top at y = 2, with a ramp up its south side.
        b.Add(new(new float3(12, 1, -12), new float3(6, 1, 6), id, BlockKind.Platform));
        b.Add(Ramp(bottom: new float2(12, 2), dir: new float2(0, -1), run: 8, rise: 2, halfWidth: 3));

        // Platform B (north-west), top at y = 3, with a ramp up its east side.
        b.Add(new(new float3(-12, 1.5f, -12), new float3(5, 1.5f, 5), id, BlockKind.Platform));
        b.Add(Ramp(bottom: new float2(3, -12), dir: new float2(-1, 0), run: 10, rise: 3, halfWidth: 2));

        // A few low blocks to bump into.
        b.Add(new(new float3(-12, 0.4f, -2), new float3(1, 0.4f, 1), Yaw(0.6f), BlockKind.Wall));
        b.Add(new(new float3(1.5f, 0.4f, 10), new float3(1, 0.4f, 1), Yaw(-0.4f), BlockKind.Wall));
        return b.ToArray();
    }

    /// <summary>
    /// A ramp whose top surface starts on the floor at <paramref name="bottom"/> and climbs <paramref name="rise"/>
    /// over <paramref name="run"/> along <paramref name="dir"/>. It's a box rotated by yaw then pitch; its low end
    /// is extended a little under the floor so the ball rolls on without a lip.
    /// </summary>
    static Block Ramp(float2 bottom, float2 dir, float run, float rise, float halfWidth)
    {
        const float halfThickness = 0.25f;
        const float extend = 0.4f;
        var angle = MathF.Atan2(rise, run);
        var d = new float3(dir.X, 0, dir.Y);
        var slope = d * MathF.Cos(angle) + new float3(0, MathF.Sin(angle), 0);
        var normal = new float3(0, MathF.Cos(angle), 0) - d * MathF.Sin(angle);
        var length = MathF.Sqrt(run * run + rise * rise) + extend;
        var low = new float3(bottom.X, 0, bottom.Y) - slope * extend; // the plane, continued under the floor
        var topMid = low + slope * (length / 2);
        var center = topMid - normal * halfThickness;
        // Local +Z runs up the slope: pitch about X by -angle, then yaw so +Z points along dir.
        var yaw = MathF.Atan2(dir.X, dir.Y);
        var q = Mul(Yaw(yaw), floatQuat.CreateFromAxisAngle(new float3(1, 0, 0), -angle));
        return new(center, new float3(halfWidth, halfThickness, length / 2), q, BlockKind.Ramp);
    }

    public static floatQuat Yaw(float radians) => floatQuat.CreateFromAxisAngle(new float3(0, 1, 0), radians);

    /// <summary>Hamilton product a*b: rotates by b first, then a. Written out so the convention is explicit.</summary>
    public static floatQuat Mul(floatQuat a, floatQuat b) =>
        new(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
        );

    /// <summary>Rotates v by the unit quaternion q.</summary>
    public static float3 Rotate(floatQuat q, float3 v)
    {
        var u = new float3(q.X, q.Y, q.Z);
        var t = float3.Cross(u, v) * 2;
        return v + t * q.W + float3.Cross(u, t);
    }

    static floatQuat Conjugate(floatQuat q) => new(-q.X, -q.Y, -q.Z, q.W);

    /// <summary>Height of the highest surface at (x, z), for placing coins.</summary>
    public static float SurfaceHeight(float2 xz) =>
        CastDown(new float3(xz.X, 50, xz.Y), out var hit, out _) ? hit.Y : 0;

    /// <summary>
    /// Casts a ray straight down from <paramref name="from"/> against every block. Returns the nearest hit point and
    /// the surface normal there (used for the ball's shadow).
    /// </summary>
    public static bool CastDown(float3 from, out float3 hit, out float3 normal)
    {
        var best = float.MaxValue;
        hit = default;
        normal = new float3(0, 1, 0);
        foreach (var block in Blocks)
        {
            var inv = Conjugate(block.Rotation);
            var o = Rotate(inv, from - block.Center);
            var d = Rotate(inv, new float3(0, -1, 0));
            var tMin = 0f;
            var tMax = float.MaxValue;
            var axis = -1;
            var sign = 0f;
            var miss = false;
            for (var i = 0; i < 3 && !miss; i++)
            {
                var oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
                var di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                var e = i == 0 ? block.HalfExtents.X : i == 1 ? block.HalfExtents.Y : block.HalfExtents.Z;
                if (MathF.Abs(di) < 1e-6f)
                {
                    miss = oi < -e || oi > e;
                    continue;
                }
                var t1 = (-e - oi) / di;
                var t2 = (e - oi) / di;
                var s = -1f; // entering through the -e face
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                    s = 1f;
                }
                if (t1 > tMin)
                {
                    tMin = t1;
                    axis = i;
                    sign = s;
                }
                tMax = MathF.Min(tMax, t2);
                miss = tMin > tMax;
            }
            if (miss || axis < 0 || tMin >= best)
                continue;
            best = tMin;
            hit = from + new float3(0, -tMin, 0);
            var local = axis == 0 ? new float3(sign, 0, 0) : axis == 1 ? new float3(0, sign, 0) : new float3(0, 0, sign);
            normal = Rotate(block.Rotation, local);
        }
        return best < float.MaxValue;
    }
}
