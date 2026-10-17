using Brutal.Numerics;

namespace BrutalGame3D.Sim;

/// <summary>
/// One fixed step's worth of player intent. <see cref="Move"/> is camera-relative (X = right, Y = forward, length
/// up to 1); <see cref="Turn"/> turns the camera (-1 left .. 1 right). The platform layer fills it from keyboard or
/// gamepad; the snapshot script and the self-test fill it directly, so the simulation never sees an SDL type (R6).
/// </summary>
public readonly record struct InputState(float2 Move, float Turn, bool Confirm, bool Restart);
