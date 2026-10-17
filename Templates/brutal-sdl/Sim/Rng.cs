namespace BrutalGame.Sim;

/// <summary>
/// Small deterministic xorshift generator. Same seed, same sequence on every machine and .NET version,
/// which keeps replays and the self-test reproducible.
/// </summary>
public struct Rng(uint seed)
{
    uint _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return _state = x;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / (1 << 24));

    public float Range(float min, float max) => min + (max - min) * NextFloat();
}
