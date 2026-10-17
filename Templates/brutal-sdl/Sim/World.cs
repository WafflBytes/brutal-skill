using Brutal.Numerics;

namespace BrutalGame.Sim;

/// <summary>
/// The whole game state: a player square collecting dots in an arena. Pure C# and deterministic: it advances
/// only through <see cref="Step"/> at a fixed timestep, so it runs identically in the window, the snapshot
/// script and the headless self-test. Replace it with your game; keep it free of SDL types.
/// </summary>
public sealed class World
{
    public const float Dt = 1f / 60f;
    public const int DotCount = 6;
    public const float PlayerSpeed = 220f;
    public const float PlayerHalfSize = 10f;
    public const float DotRadius = 6f;

    public static readonly float2 ArenaSize = new(640, 360);
    public const float HudHeight = 24f; // top strip reserved for the score

    readonly uint _seed;
    Rng _rng;

    public float2 Player { get; private set; }
    public float2[] Dots { get; } = new float2[DotCount];
    public int Score { get; private set; }
    public long Ticks { get; private set; }

    /// <summary>What happened during the last <see cref="Step"/>, for sound and effects. Reset every step.</summary>
    public int PickupsThisStep { get; private set; }
    public bool RestartedThisStep { get; private set; }
    public float Time => Ticks * Dt;

    public World(uint seed = 12345)
    {
        _seed = seed;
        Reset();
    }

    public void Reset()
    {
        _rng = new Rng(_seed);
        Player = (ArenaMin + ArenaMax) * 0.5f;
        Score = 0;
        Ticks = 0;
        for (var i = 0; i < Dots.Length; i++)
            Dots[i] = RandomDotPosition();
    }

    public static float2 ArenaMin => new(PlayerHalfSize, HudHeight + PlayerHalfSize);
    public static float2 ArenaMax => ArenaSize - new float2(PlayerHalfSize, PlayerHalfSize);

    public void Step(in InputState input)
    {
        PickupsThisStep = 0;
        RestartedThisStep = false;
        if (input.Restart)
        {
            Reset();
            RestartedThisStep = true;
            return;
        }

        // Normalize so diagonals aren't faster; NormalizeOrZero because Normalize(zero) throws (T19).
        var dir = input.Move.LengthSquared() > 1f ? float2.NormalizeOrZero(input.Move) : input.Move;
        Player = float2.Clamp(Player + dir * (PlayerSpeed * Dt), ArenaMin, ArenaMax);

        var reach = PlayerHalfSize + DotRadius;
        for (var i = 0; i < Dots.Length; i++)
        {
            if (float2.DistanceSquared(Player, Dots[i]) <= reach * reach)
            {
                Score++;
                PickupsThisStep++;
                Dots[i] = RandomDotPosition();
            }
        }
        Ticks++;
    }

    /// <summary>Order-sensitive hash of the full state, for determinism checks.</summary>
    public ulong Hash()
    {
        var h = 1469598103934665603ul;
        void Mix(float f)
        {
            h ^= (uint)BitConverter.SingleToInt32Bits(f);
            h *= 1099511628211ul;
        }
        Mix(Player.X);
        Mix(Player.Y);
        foreach (var d in Dots)
        {
            Mix(d.X);
            Mix(d.Y);
        }
        h ^= (ulong)Score;
        h ^= (ulong)Ticks << 32;
        return h;
    }

    float2 RandomDotPosition()
    {
        var min = ArenaMin + new float2(DotRadius, DotRadius);
        var max = ArenaMax - new float2(DotRadius, DotRadius);
        return new float2(_rng.Range(min.X, max.X), _rng.Range(min.Y, max.Y));
    }
}
