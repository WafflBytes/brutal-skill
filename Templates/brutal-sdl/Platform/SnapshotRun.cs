using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// --snapshot: plays a scripted session through the real drawing code and saves PNGs, with no window. It draws
/// into an SDL software renderer backed by a plain surface, so it also works on machines with no display.
/// The script is driven by game time, never frame count (T33). Exit code 0 = every shot saved and not blank.
/// </summary>
public static unsafe class SnapshotRun
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var surface = Sdl.PInvoke.CreateSurface(1280, 720, SdlPixelFormat.Abgr8888);
        if (surface == null)
            throw new InvalidOperationException("CreateSurface failed");
        var renderer = Sdl.Render.CreateSoftwareRenderer(surface);
        var failures = 0;
        try
        {
            using var gfx = new Gfx(renderer);
            var world = new World();
            var flow = new ScreenFlow();

            // (time in seconds since start, input held until the next entry, screenshot taken at that time)
            (float At, InputState Input, string? Shot)[] script =
            [
                (0.0f, default, null),
                (0.8f, default, "01_splash.png"),
                (1.2f, new InputState(float2.Zero, Confirm: true, Restart: false), null), // skip is allowed after 1 s
                (1.25f, new InputState(new float2(1, 0), false, false), null),
                (2.5f, new InputState(new float2(0, 1), false, false), "02_play.png"),
                (3.5f, new InputState(new float2(-1, -1), false, false), "03_play.png"),
                (4.5f, default, "04_play.png"),
            ];

            var t = 0f;
            for (var i = 0; i < script.Length; i++)
            {
                var (at, input, shot) = script[i];
                // Run fixed steps until this entry's time, holding the previous entry's input.
                var held = i == 0 ? default : script[i - 1].Input;
                while (t + World.Dt * 0.5f < at)
                {
                    flow.Step(World.Dt, held.Confirm);
                    if (flow.Current == Screen.Play)
                        world.Step(held);
                    held = held with { Confirm = false, Restart = false }; // edge inputs last one step
                    t += World.Dt;
                }
                if (shot == null)
                    continue;
                gfx.Frame(flow, world);
                var lit = Screenshot.Save(renderer, Path.Combine(dir, shot));
                var ok = lit > 500;
                if (!ok)
                    failures++;
                Console.WriteLine(
                    $"{(ok ? "saved" : "BLANK")} {shot}: {lit} lit pixels, t={t:F2}s, screen={flow.Current}, score={world.Score}"
                );
            }
            if (flow.Current != Screen.Play)
            {
                Console.WriteLine("Script never left the splash screen.");
                failures++;
            }

            // The pause overlay over the last play frame.
            gfx.Frame(flow, world, paused: true);
            var pausedLit = Screenshot.Save(renderer, Path.Combine(dir, "05_paused.png"));
            Console.WriteLine($"{(pausedLit > 500 ? "saved" : "BLANK")} 05_paused.png: {pausedLit} lit pixels");
            if (pausedLit <= 500)
                failures++;

            // The same frame at 1920x1080: text is re-rasterised for the bigger output, so it stays sharp.
            var big = Sdl.PInvoke.CreateSurface(1920, 1080, SdlPixelFormat.Abgr8888);
            var bigRenderer = Sdl.Render.CreateSoftwareRenderer(big);
            try
            {
                using var bigGfx = new Gfx(bigRenderer);
                bigGfx.Frame(flow, world);
                var lit = Screenshot.Save(bigRenderer, Path.Combine(dir, "06_play_1080p.png"));
                Console.WriteLine($"{(lit > 500 ? "saved" : "BLANK")} 06_play_1080p.png: {lit} lit pixels");
                if (lit <= 500)
                    failures++;
            }
            finally
            {
                Sdl.Render.DestroyRenderer(bigRenderer);
                Sdl.PInvoke.DestroySurface(big);
            }
        }
        finally
        {
            Sdl.Render.DestroyRenderer(renderer);
            Sdl.PInvoke.DestroySurface(surface);
        }
        return failures;
    }
}
