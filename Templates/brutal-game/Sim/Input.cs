using Brutal.Numerics;

namespace BrutalGame.Sim;

/// <summary>
/// One fixed step's worth of player intent. <see cref="Move"/> is X right, Y down, length up to 1. The platform
/// layer fills it from the keyboard; the snapshot script and the self-test fill it directly, so the simulation
/// never sees an SDL type (brutal skill, rule R6). Add fields as your game needs them.
/// </summary>
public readonly record struct InputState(float2 Move, bool Confirm);
