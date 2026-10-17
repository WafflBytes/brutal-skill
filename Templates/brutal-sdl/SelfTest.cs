using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame.Platform;
using BrutalGame.Sim;

namespace BrutalGame;

/// <summary>
/// --selftest: headless checks on the simulation, the screen flow and the embedded assets. No window, GPU or
/// audio device needed. Add a check whenever you add a rule or an asset. Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var checks = new (string Name, Func<string?> Check)[]
        {
            ("determinism", Determinism),
            ("player stays in the arena", Bounds),
            ("collecting a dot scores and respawns it", Pickup),
            ("restart resets the world", Restart),
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

    static InputState Scripted(int tick) =>
        new(new float2(MathF.Sin(tick * 0.031f), MathF.Cos(tick * 0.017f)), false, false);

    static string? Determinism()
    {
        var a = new World(777);
        var b = new World(777);
        for (var i = 0; i < 3600; i++)
        {
            a.Step(Scripted(i));
            b.Step(Scripted(i));
        }
        if (a.Hash() != b.Hash())
            return "two runs with the same seed and inputs diverged";
        return a.Score > 0 ? null : "scripted wandering never collected a dot; the check isn't exercising pickups";
    }

    static string? Bounds()
    {
        float2[] dirs = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(1, 1), new(-1, -1)];
        foreach (var dir in dirs)
        {
            var w = new World();
            for (var i = 0; i < 600; i++)
                w.Step(new InputState(dir, false, false));
            var p = w.Player;
            if (p.X < World.ArenaMin.X || p.Y < World.ArenaMin.Y || p.X > World.ArenaMax.X || p.Y > World.ArenaMax.Y)
                return $"holding {dir} for 10 s left the arena at {p}";
        }
        return null;
    }

    static string? Pickup()
    {
        // Walk at dot 0 until it moves. Other dots collected on the way also count, so compare the score
        // with how many dots changed.
        var w = new World();
        var target = w.Dots[0];
        for (var i = 0; i < 1200 && w.Dots[0] == target; i++)
            w.Step(new InputState(float2.NormalizeOrZero(target - w.Player), false, false));
        if (w.Dots[0] == target)
            return "walking straight at a dot for 20 s never collected it";
        return w.Score >= 1 ? null : "the dot respawned but the score didn't increase";
    }

    static string? Restart()
    {
        var fresh = new World().Hash();
        var w = new World();
        for (var i = 0; i < 300; i++)
            w.Step(Scripted(i));
        w.Step(new InputState(float2.Zero, false, Restart: true));
        return w.Hash() == fresh ? null : "state after restart differs from a new world";
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

    // Asset checks load every embedded asset once, so a missing or undecodable file fails here, not mid-game.

    static unsafe string? FontRenders()
    {
        var target = Sdl.PInvoke.CreateSurface(640, 360, SdlPixelFormat.Abgr8888);
        var renderer = Sdl.Render.CreateSoftwareRenderer(target); // headless: no window or GPU
        try
        {
            using var text = new TextRenderer(renderer, World.ArenaSize.Y);
            var label = text.CreateLabel(18);
            label.Draw("Score 1234", 0, 0, 255, 255, 255);
            return label.Width is > 40 and < 200 ? null : $"unexpected text width {label.Width}";
        }
        finally
        {
            Sdl.Render.DestroyRenderer(renderer);
            Sdl.PInvoke.DestroySurface(target);
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
}
