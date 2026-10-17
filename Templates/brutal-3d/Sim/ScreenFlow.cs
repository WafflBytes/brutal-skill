namespace BrutalGame3D.Sim;

public enum Screen
{
    Splash,
    Play,
}

/// <summary>
/// Which screen is showing. The splash enforces the BRUTAL licence (2.2): the logo is shown before gameplay,
/// long enough to be legible. It can be skipped only after <see cref="MinSplash"/> and ends by itself after
/// <see cref="SplashDuration"/>. The self-test checks these rules, so keep them if you restyle the splash.
/// </summary>
public sealed class ScreenFlow
{
    public const float SplashDuration = 2.5f;
    public const float MinSplash = 1.0f;

    public Screen Current { get; private set; } = Screen.Splash;
    public float ScreenTime { get; private set; }

    public void Step(float dt, bool skipPressed)
    {
        ScreenTime += dt;
        if (Current == Screen.Splash && (ScreenTime >= SplashDuration || (skipPressed && ScreenTime >= MinSplash)))
        {
            Current = Screen.Play;
            ScreenTime = 0;
        }
    }

    /// <summary>Logo opacity for the splash fade: in over 0.4 s, out over the last 0.5 s.</summary>
    public float SplashAlpha => Math.Clamp(MathF.Min(ScreenTime / 0.4f, (SplashDuration - ScreenTime) / 0.5f), 0f, 1f);
}
