using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;
using BrutalGame3D.Platform;
using BrutalGame3D.Sim;

namespace BrutalGame3D;

/// <summary>
/// --selftest: headless checks on the simulation (real Jolt physics), the screen flow, the maths and the embedded
/// assets. No window, GPU or audio device needed. Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var checks = new (string Name, Func<string?> Check)[]
        {
            ("level geometry: ramps meet floor and platforms", LevelGeometry),
            ("ball settles on the floor", Settles),
            ("forward input rolls the ball forward", RollsForward),
            ("camera turn changes the move direction", CameraRelative),
            ("walls keep the ball in the arena", Walls),
            ("autopilot climbs ramps and collects every coin in time", FullRoute),
            ("time runs out: time-up, ticks, input ignored, restart", TimeUp),
            ("restart resets the world", Restart),
            ("determinism", Determinism),
            ("chase camera stays behind and above", CameraFollows),
            ("splash obeys the licence timing", SplashTiming),
            ("matrix maths", Matrices),
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

    static InputState Move(float x, float y, float turn = 0) => new(new float2(x, y), turn, false, false);

    static void Steps(World w, int n, InputState input)
    {
        for (var i = 0; i < n; i++)
            w.Step(input);
    }

    static string? LevelGeometry()
    {
        // Ramp A climbs from z=2 (floor) to z=-6 (platform A top, y=2); ramp B from x=3 to x=-7 (y=3).
        (float2 At, float Expected)[] samples =
        [
            (new(12, 2.5f), 0), (new(12, 1.5f), 0.125f), (new(12, -2), 1), (new(12, -5.8f), 1.95f), (new(12, -12), 2),
            (new(3.5f, -12), 0), (new(-2, -12), 1.5f), (new(-6.8f, -12), 2.94f), (new(-12, -12), 3), (new(0, 0), 0),
        ];
        foreach (var (at, expected) in samples)
        {
            var y = Level.SurfaceHeight(at);
            if (MathF.Abs(y - expected) > 0.08f)
                return $"surface at {at} is {y:F2}, expected {expected:F2}";
        }
        for (var i = 0; i < Level.Coins.Length; i++)
            if (MathF.Abs(Level.Coins[i].Y - Level.SurfaceHeight(Level.CoinXZ[i]) - Level.CoinHover) > 0.01f)
                return $"coin {i} isn't hovering over its surface";
        return null;
    }

    static string? Settles()
    {
        using var w = new World();
        Steps(w, 90, default);
        var p = w.BallPosition;
        return MathF.Abs(p.Y - PhysicsScene.BallRadius) < 0.05f && w.BallVelocity.Length() < 0.2f
            ? null
            : $"ball at {p} moving {w.BallVelocity.Length():F2} m/s after 1.5 s";
    }

    static string? RollsForward()
    {
        using var w = new World();
        var start = w.BallPosition;
        var q0 = w.BallRotation;
        Steps(w, 60, Move(0, 1));
        var p = w.BallPosition;
        if (p.Z > start.Z - 3)
            return $"1 s of forward moved z {start.Z:F2} -> {p.Z:F2}";
        if (MathF.Abs(p.X - start.X) > 0.3f)
            return $"drifted sideways to x={p.X:F2}";
        var speed = w.BallVelocity.Length();
        if (speed < 5 || speed > World.MaxSpeed + 1)
            return $"speed {speed:F1} m/s after 1 s";
        var q = w.BallRotation;
        var dot = MathF.Abs(q0.X * q.X + q0.Y * q.Y + q0.Z * q.Z + q0.W * q.W);
        return dot < 0.99f ? null : "the ball slid without rotating";
    }

    static string? CameraRelative()
    {
        using var w = new World();
        Steps(w, 39, Move(0, 0, 1)); // turn right ~90 degrees (2.4 rad/s * 0.65 s ~ 1.56 rad)
        var yaw = w.CameraYaw;
        if (MathF.Abs(yaw - MathF.PI / 2) > 0.1f)
            return $"yaw after turning is {yaw:F2}";
        var start = w.BallPosition;
        Steps(w, 45, Move(0, 1)); // forward is now +X
        var d = w.BallPosition - start;
        return d.X > 2.5f && MathF.Abs(d.Z) < 0.6f ? null : $"forward after a right turn moved by {d}";
    }

    static string? Walls()
    {
        float2[] dirs = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(0.7f, 0.7f), new(-0.7f, -0.7f)];
        foreach (var dir in dirs)
        {
            using var w = new World();
            var maxY = 0f;
            for (var i = 0; i < 600; i++)
            {
                w.Step(Move(dir.X, dir.Y));
                var p = w.BallPosition;
                maxY = MathF.Max(maxY, p.Y);
                if (MathF.Abs(p.X) > Level.HalfSize || MathF.Abs(p.Z) > Level.HalfSize || p.Y < 0)
                    return $"holding {dir} escaped to {p} at step {i}";
            }
        }
        return null;
    }

    static string? FullRoute()
    {
        using var w = new World();
        var bot = new Bot();
        var maxY = 0f;
        var coinEvents = 0;
        var bumps = 0;
        while (w.State == GameState.Playing)
        {
            w.Step(bot.Think(w));
            maxY = MathF.Max(maxY, w.BallPosition.Y);
            coinEvents += w.CoinsThisStep;
            if (w.BumpThisStep > 0)
                bumps++;
        }
        Console.WriteLine($"     route: {w.Score}/{w.CoinCount} coins in {World.TimeLimit - w.TimeLeft:F1} s, highest y {maxY:F2}, {bumps} bump events, waypoint {bot.WaypointIndex}/{Level.Route.Length}");
        if (w.State != GameState.Won)
            return $"stuck at waypoint {bot.WaypointIndex} ({Level.Route[Math.Min(bot.WaypointIndex, Level.Route.Length - 1)].XZ}), ball at {w.BallPosition}, {w.Score} coins";
        if (maxY < 3.4f)
            return $"never got onto platform B (highest y {maxY:F2})";
        if (coinEvents != w.CoinCount)
            return $"{coinEvents} coin events for {w.CoinCount} coins";
        if (!w.FinishedThisStep)
            return "no finish event on the winning step";
        if (bumps == 0)
            return "no impact events on a route that drops off a platform";
        return w.TimeLeft > 10 ? null : $"only {w.TimeLeft:F1} s left: the timer is too tight for players";
    }

    static string? TimeUp()
    {
        using var w = new World();
        var ticks = 0;
        var finished = 0;
        while (w.State == GameState.Playing && w.Time < 70)
        {
            w.Step(default);
            ticks += w.TickThisStep ? 1 : 0;
            finished += w.FinishedThisStep ? 1 : 0;
        }
        if (w.State != GameState.TimeUp)
            return $"state {w.State} after {w.Time:F1} s";
        if (MathF.Abs(w.Time - World.TimeLimit) > 0.05f)
            return $"time ran out at {w.Time:F2} s";
        if (ticks != 5 || finished != 1)
            return $"{ticks} countdown ticks (want 5), {finished} finish events (want 1)";
        var p = w.BallPosition;
        Steps(w, 60, Move(0, 1));
        if (float3.Distance(p, w.BallPosition) > 0.05f)
            return "the ball still obeys input after time ran out";
        w.Step(new InputState(default, 0, Confirm: true, Restart: false));
        return w.RestartedThisStep && w.State == GameState.Playing && w.TimeLeft == World.TimeLimit
            ? null
            : "Enter on the time-up screen didn't start a new round";
    }

    static string? Restart()
    {
        using var fresh = new World();
        using var w = new World();
        Steps(w, 200, Move(0.5f, 1, 0.3f));
        w.Step(new InputState(default, 0, false, Restart: true));
        if (w.Hash() != fresh.Hash())
            return "state after restart differs from a new world";
        Steps(w, 100, Move(1, 0));
        Steps(fresh, 100, Move(1, 0));
        return w.Hash() == fresh.Hash() ? null : "a restarted world diverges from a new one";
    }

    static InputState Scripted(int tick) => Move(MathF.Sin(tick * 0.031f), MathF.Cos(tick * 0.017f), MathF.Sin(tick * 0.005f) * 0.5f);

    static string? Determinism()
    {
        using var a = new World();
        using var b = new World();
        for (var i = 0; i < 1800; i++)
        {
            a.Step(Scripted(i));
            b.Step(Scripted(i));
            if (a.Hash() != b.Hash())
                return $"two runs with the same inputs diverged at step {i}";
        }
        return a.Score > 0 ? null : "scripted wandering never collected a coin; the check isn't exercising pickups";
    }

    static string? CameraFollows()
    {
        using var w = new World();
        for (var i = 0; i < 600; i++)
        {
            w.Step(Scripted(i));
            var back = w.CameraPosition - w.BallPosition;
            var flat = new float2(back.X, back.Z).Length();
            if (back.Y < 1.5f || flat > 14 || flat < 2)
                return $"camera offset {back} at step {i}";
            var behind = float3.Dot(float3.NormalizeOrZero(new float3(back.X, 0, back.Z)), World.Forward(w.CameraYaw));
            if (i > 120 && behind > -0.3f)
                return $"camera isn't behind the ball at step {i} (dot {behind:F2})";
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

    static string? Matrices()
    {
        var eye = new float3(0, 5, 10);
        var vp = Mat4.Perspective(1.0f, 16f / 9, 0.1f, 100) * Mat4.LookAt(eye, float3.Zero, new float3(0, 1, 0));
        var centre = vp.TransformPoint(float3.Zero);
        if (MathF.Abs(centre.X) > 1e-4f || MathF.Abs(centre.Y) > 1e-4f || centre.Z <= -1 || centre.Z >= 1)
            return $"look-at target projects to {centre}, expected the screen centre";
        var right = vp.TransformPoint(new float3(1, 0, 0));
        if (right.X <= 0)
            return "+X should appear to the right";
        var up = vp.TransformPoint(new float3(0, 1, 0));
        if (up.Y <= 0)
            return "+Y should appear up";
        // Model matrix from a quaternion matches Level.Rotate.
        var q = Level.Mul(Level.Yaw(0.7f), floatQuat.CreateFromAxisAngle(new float3(1, 0, 0), -0.3f));
        var v = new float3(0.3f, -0.5f, 0.8f);
        var a = Mat4.Trs(float3.Zero, q, new float3(1)).TransformPoint(v);
        var b = Level.Rotate(q, v);
        return float3.Distance(a, b) < 1e-4f ? null : $"Trs rotation {a} != quaternion rotation {b}";
    }

    static string? Performance()
    {
        using var w = new World();
        var sw = Stopwatch.StartNew();
        const int steps = 60 * 60; // one full round of game time
        for (var i = 0; i < steps; i++)
            w.Step(Scripted(i));
        var ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"     {steps} steps in {ms:F1} ms ({ms * 1000 / steps:F1} us/step)");
        return ms < 1500 ? null : $"{steps} steps took {ms:F0} ms (budget 1500 ms)";
    }

    // Asset checks load every embedded asset once, so a missing or undecodable file fails here, not mid-game.

    static unsafe string? FontRenders()
    {
        SdlTtf.Init();
        using var data = new NativeBlob(Resources.Read("font.ttf"));
        var font = SdlTtf.OpenFontIO(Sdl.PInvoke.IOFromConstMem(data.Data, (nuint)data.Length), closeio: true, 30);
        try
        {
            var surface = SdlTtf.RenderTextBlended(font, "Coins  12 / 13", 0, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
            if (surface == null)
                return "RenderTextBlended returned null";
            var w = surface->w;
            var h = surface->h;
            Sdl.PInvoke.DestroySurface(surface);
            return w is > 120 and < 400 && h is > 20 and < 60 ? null : $"unexpected text size {w}x{h}";
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
        string? Audible(string what)
        {
            audio.Mix(buffer);
            var peak = 0f;
            foreach (var v in buffer)
                peak = MathF.Max(peak, MathF.Abs(v));
            for (var i = 0; i < 12; i++)
                audio.Mix(buffer); // let it finish before the next one
            return peak < 0.05f ? $"{what} is silent (peak {peak:F3})" : null;
        }
        foreach (var sfx in Enum.GetValues<Sfx>())
        {
            audio.Play(sfx);
            if (Audible(sfx.ToString()) is { } error)
                return error;
        }
        audio.PlayBump(9);
        return Audible("PlayBump on its track");
    }
}
