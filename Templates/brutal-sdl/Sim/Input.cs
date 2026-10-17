using Brutal.Numerics;

namespace BrutalGame.Sim;

/// <summary>
/// One fixed step's worth of player intent. The platform layer fills it from the keyboard; the snapshot script
/// and the self-test fill it directly, so the simulation never sees an SDL type (brutal skill, rule R6).
/// </summary>
public readonly record struct InputState(float2 Move, bool Confirm, bool Restart);
