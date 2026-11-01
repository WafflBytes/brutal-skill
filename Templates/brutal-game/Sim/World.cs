using Brutal.Numerics;

namespace BrutalGame.Sim;

/// <summary>
/// The game state. This is a placeholder: one marker you move around a field, so every layer (input, the fixed
/// step, drawing, the self-test, --snapshot, --smoke) has something to work on. Replace it with your game.
/// Keep it pure C# and deterministic: it advances only through <see cref="Step"/>, so it runs identically in the
/// window, the snapshot script and the headless self-test, and it never sees an SDL or GL type (skill rule R6).
/// Units are abstract: the SDL renderer draws 40 pixels per unit, the GL renderer one world unit per unit.
/// </summary>
public sealed class World
{
    public const float Dt = 1f / 60f;
    public const float MarkerSpeed = 6f; // units per second
    public const float MarkerHalfSize = 0.5f;

    /// <summary>The field the marker moves in, from (0, 0) to this size. X is right, Y is down (towards the camera in 3D).</summary>
    public static readonly float2 FieldSize = new(16, 9);

    public float2 Marker { get; private set; }
    public long Ticks { get; private set; }
    public float Time => Ticks * Dt;

    public World() => Marker = FieldSize * 0.5f;

    public static float2 MarkerMin => new(MarkerHalfSize, MarkerHalfSize);
    public static float2 MarkerMax => FieldSize - new float2(MarkerHalfSize, MarkerHalfSize);

    public void Step(in InputState input)
    {
        // Normalize so diagonals aren't faster; NormalizeOrZero because Normalize(zero) throws (T19).
        var dir = input.Move.LengthSquared() > 1f ? float2.NormalizeOrZero(input.Move) : input.Move;
        Marker = float2.Clamp(Marker + dir * (MarkerSpeed * Dt), MarkerMin, MarkerMax);
        Ticks++;
    }

    /// <summary>Order-sensitive hash of the full state, for determinism checks. Mix in every field you add.</summary>
    public ulong Hash()
    {
        var h = 1469598103934665603ul;
        void Mix(float f)
        {
            h ^= (uint)BitConverter.SingleToInt32Bits(f);
            h *= 1099511628211ul;
        }
        Mix(Marker.X);
        Mix(Marker.Y);
        h ^= (ulong)Ticks << 32;
        return h;
    }
}
