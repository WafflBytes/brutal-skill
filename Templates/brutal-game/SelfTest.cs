using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;
using BrutalGame.Platform;
using BrutalGame.Sim;

namespace BrutalGame;

/// <summary>
/// --selftest: headless checks on the simulation, the screen flow and the embedded assets. No window, GPU or
/// audio device needed. Add a check for every rule you add to Sim/ and every asset you embed.
/// Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var checks = new (string Name, Func<string?> Check)[]
        {
            ("determinism", Determinism),
            ("marker stays in the field", Bounds),
            ("splash obeys the licence timing", SplashTiming),
            ("performance budget", Performance),
            ("embedded font renders text", FontRenders),
            ("every sound decodes and is audible", SoundsPlay),
        };
        var failed = 0;
        foreach (var (name, check) in checks)
        {
            string? error;
            try
            {
                error = check();
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
            }
            Console.WriteLine(error == null ? $"PASS {name}" : $"FAIL {name}: {error}");
            if (error != null)
                failed++;
        }
        Console.WriteLine(failed == 0 ? "Self-test passed." : $"{failed} check(s) failed.");
        return failed;
    }

    static InputState Scripted(int tick) => new(new float2(MathF.Sin(tick * 0.031f), MathF.Cos(tick * 0.017f)), false);

    static string? Determinism()
    {
        var a = new World();
        var b = new World();
        for (var i = 0; i < 3600; i++)
        {
            a.Step(Scripted(i));
            b.Step(Scripted(i));
        }
        if (a.Hash() != b.Hash())
            return "two runs with the same inputs diverged";
        return a.Marker != new World().Marker ? null : "scripted input never moved the marker; the check isn't exercising Step";
    }

    static string? Bounds()
    {
        float2[] dirs = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(1, 1), new(-1, -1)];
        foreach (var dir in dirs)
        {
            var w = new World();
            for (var i = 0; i < 600; i++)
                w.Step(new InputState(dir, false));
            var p = w.Marker;
            if (p.X < World.MarkerMin.X || p.Y < World.MarkerMin.Y || p.X > World.MarkerMax.X || p.Y > World.MarkerMax.Y)
                return $"holding {dir} for 10 s left the field at {p}";
        }
        return null;
    }

    static string? SplashTiming()
    {
        var early = new ScreenFlow();
        for (var t = 0f; t < ScreenFlow.MinSplash - 0.1f; t += World.Dt)
            early.Step(World.Dt, skipPressed: true);
        if (early.Current != Screen.Splash)
            return "the splash could be skipped before the minimum time";

        var skip = new ScreenFlow();
        for (var t = 0f; t < ScreenFlow.MinSplash + 0.05f; t += World.Dt)
            skip.Step(World.Dt, skipPressed: false);
        skip.Step(World.Dt, skipPressed: true);
        if (skip.Current != Screen.Play)
            return "skipping after the minimum time didn't work";

        var wait = new ScreenFlow();
        for (var t = 0f; t < ScreenFlow.SplashDuration + 0.1f; t += World.Dt)
            wait.Step(World.Dt, skipPressed: false);
        return wait.Current == Screen.Play ? null : "the splash never ended by itself";
    }

    static string? Performance()
    {
        var w = new World();
        var sw = Stopwatch.StartNew();
        const int steps = 60 * 60 * 10; // ten minutes of game time
        for (var i = 0; i < steps; i++)
            w.Step(Scripted(i));
        var ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"     {steps} steps in {ms:F1} ms ({ms * 1000 / steps:F2} us/step)");
        return ms < 500 ? null : $"{steps} steps took {ms:F0} ms (budget 500 ms)";
    }

    // Asset checks load every embedded asset once, so a missing or undecodable file fails here, not mid-game.

    static unsafe string? FontRenders()
    {
        SdlTtf.Init();
        using var data = new NativeBlob(Resources.Read("font.ttf")); // SDL_ttf reads the font lazily: keep it alive
        var font = SdlTtf.OpenFontIO(Sdl.PInvoke.IOFromConstMem(data.Data, (nuint)data.Length), closeio: true, 18);
        try
        {
            var surface = SdlTtf.RenderTextBlended(font, "Score 1234", 0, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
            if (surface == null)
                return "RenderTextBlended returned null";
            var width = surface->w;
            Sdl.PInvoke.DestroySurface(surface);
            return width is > 40 and < 200 ? null : $"unexpected text width {width}";
        }
        finally
        {
            SdlTtf.CloseFont(font);
            SdlTtf.Quit();
        }
    }

    static string? SoundsPlay()
    {
        using var audio = Audio.OpenOffline(); // mixes into memory: no audio device needed
        var buffer = new float[48000 / 10 * 2]; // 0.1 s stereo
        foreach (var sfx in Enum.GetValues<Sfx>())
        {
            audio.Play(sfx);
            audio.Mix(buffer);
            var peak = 0f;
            foreach (var v in buffer)
                peak = MathF.Max(peak, MathF.Abs(v));
            if (peak < 0.05f)
                return $"{sfx} is silent (peak {peak:F3})";
            for (var i = 0; i < 10; i++)
                audio.Mix(buffer); // let it finish before the next one
        }
        return null;
    }
}
