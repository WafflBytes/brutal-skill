using Brutal.OpenGlApi;
using Brutal.SdlApi;
using Brutal.StbApi;
using BrutalGame3D.Sim;
using GL = Brutal.OpenGlApi.OpenGl.Native;

namespace BrutalGame3D.Platform;

/// <summary>
/// --snapshot: plays a scripted session (the autopilot following the coin route) through the real drawing code
/// and saves PNGs. It renders into an off-screen framebuffer of a hidden GL window, so nothing appears on screen.
/// Driven by game time, never frame count (T33). Exit code 0 = every shot saved and not blank.
/// </summary>
public static unsafe class SnapshotRun
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events);
        var failures = 0;
        try
        {
            using var gl = new GlWindow("BrutalGame3D snapshot", 320, 180, hidden: true);
            using var renderer = new Renderer();
            using var target = new OffscreenTarget(1280, 720);
            using var world = new World();
            var flow = new ScreenFlow();
            var bot = new Bot();
            var t = 0f;
            (float At, string Shot)[] shots =
            [
                (0.8f, "01_splash.png"),
                (3.5f, "02_start.png"),
                (9f, "03_rolling.png"),
                (16f, "04_play.png"),
                (24f, "05_ramp.png"),
                (32f, "06_platform.png"),
                (40f, "07_play.png"),
            ];
            var next = 0;
            while (t < 70f && (next < shots.Length || world.State == GameState.Playing))
            {
                var input = flow.Current == Screen.Play ? bot.Think(world) : default;
                var skip = t >= 1.2f && flow.Current == Screen.Splash; // skip the splash as a player would
                flow.Step(World.Dt, skip);
                if (flow.Current == Screen.Play && !skip)
                    world.Step(input);
                t += World.Dt;
                if (next < shots.Length && t >= shots[next].At)
                {
                    failures += Shot(renderer, target, flow, world, false, Path.Combine(dir, shots[next].Shot), t);
                    next++;
                }
            }
            Console.WriteLine($"autopilot: {world.Score}/{world.CoinCount} coins, state {world.State}, {world.TimeLeft:F1} s left");
            // Let the end overlay's "press Enter" hint appear, then shoot the end screen.
            for (var i = 0; i < 60; i++)
                world.Step(default);
            failures += Shot(renderer, target, flow, world, false, Path.Combine(dir, "08_end.png"), t);
            failures += Shot(renderer, target, flow, world, true, Path.Combine(dir, "09_paused_overlay.png"), t);

            // Time-up screen: a world nobody plays.
            using (var idle = new World())
            {
                while (idle.State == GameState.Playing)
                    idle.Step(default);
                for (var i = 0; i < 60; i++)
                    idle.Step(default);
                failures += Shot(renderer, target, flow, idle, false, Path.Combine(dir, "10_timeup.png"), t);
            }

            // 1080p: text is rasterised for the bigger output, so it stays sharp.
            using var big = new OffscreenTarget(1920, 1080);
            using var replay = new World();
            for (var i = 0; i < 120; i++)
                replay.Step(new InputState(new(0.3f, 1), 0.2f, false, false));
            failures += Shot(renderer, big, flow, replay, false, Path.Combine(dir, "11_play_1080p.png"), t);
            if (world.State != GameState.Won)
            {
                Console.WriteLine("FAIL: the autopilot didn't collect every coin in the snapshot run");
                failures++;
            }
        }
        finally
        {
            Sdl.Init.Quit();
        }
        return failures;
    }

    static int Shot(Renderer renderer, OffscreenTarget target, ScreenFlow flow, World world, bool paused, string path, float t)
    {
        target.Bind();
        renderer.Frame(flow, world, View.Of(world), paused, target.Width, target.Height);
        var lit = target.SavePng(path, out var distinct);
        var ok = lit > 5000 && distinct > 20;
        Console.WriteLine(
            $"{(ok ? "saved" : "BLANK")} {Path.GetFileName(path)}: {lit} lit px, {distinct} colours, t={t:F1}s, screen={flow.Current}, coins={world.Score}, ball=({world.BallPosition.X:F1},{world.BallPosition.Y:F1},{world.BallPosition.Z:F1})"
        );
        return ok ? 0 : 1;
    }
}

/// <summary>An RGBA8 + depth framebuffer for off-screen rendering and pixel read-back.</summary>
public sealed unsafe class OffscreenTarget : IDisposable
{
    GlFramebuffer _fbo;
    GlRenderbuffer _color;
    GlRenderbuffer _depth;
    public int Width { get; }
    public int Height { get; }

    public OffscreenTarget(int w, int h)
    {
        Width = w;
        Height = h;
        GlFramebuffer fbo;
        GL.glGenFramebuffers(1, &fbo);
        _fbo = fbo;
        GL.glBindFramebuffer(OpenGl.GL_FRAMEBUFFER, fbo);
        var rbs = stackalloc GlRenderbuffer[2];
        GL.glGenRenderbuffers(2, rbs);
        _color = rbs[0];
        _depth = rbs[1];
        GL.glBindRenderbuffer(OpenGl.GL_RENDERBUFFER, _color);
        GL.glRenderbufferStorage(OpenGl.GL_RENDERBUFFER, OpenGl.GL_RGBA8, w, h);
        GL.glFramebufferRenderbuffer(OpenGl.GL_FRAMEBUFFER, OpenGl.GL_COLOR_ATTACHMENT0, OpenGl.GL_RENDERBUFFER, _color);
        GL.glBindRenderbuffer(OpenGl.GL_RENDERBUFFER, _depth);
        GL.glRenderbufferStorage(OpenGl.GL_RENDERBUFFER, OpenGl.GL_DEPTH_COMPONENT24, w, h);
        GL.glFramebufferRenderbuffer(OpenGl.GL_FRAMEBUFFER, OpenGl.GL_DEPTH_ATTACHMENT, OpenGl.GL_RENDERBUFFER, _depth);
        var status = GL.glCheckFramebufferStatus(OpenGl.GL_FRAMEBUFFER);
        if (status.Id != OpenGl.GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"framebuffer incomplete: 0x{status.Id:X}");
    }

    public void Bind() => GL.glBindFramebuffer(OpenGl.GL_FRAMEBUFFER, _fbo);

    /// <summary>Reads the framebuffer, flips it (GL rows start at the bottom) and writes a PNG. Returns lit pixels.</summary>
    public int SavePng(string path, out int distinctColours)
    {
        var pixels = new byte[Width * Height * 4];
        fixed (byte* p = pixels)
            GL.glReadPixels(0, 0, Width, Height, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, p);
        var flipped = new byte[pixels.Length];
        var row = Width * 4;
        for (var y = 0; y < Height; y++)
            Array.Copy(pixels, y * row, flipped, (Height - 1 - y) * row, row);
        Stb.WritePng(path, Width, Height, 4, flipped, row);
        var lit = 0;
        var colours = new HashSet<int>();
        for (var i = 0; i < flipped.Length; i += 4)
        {
            if (flipped[i] > 40 || flipped[i + 1] > 40 || flipped[i + 2] > 40)
                lit++;
            if ((i & 63) == 0)
                colours.Add((flipped[i] >> 3) | (flipped[i + 1] >> 3 << 5) | (flipped[i + 2] >> 3 << 10));
        }
        distinctColours = colours.Count;
        return lit;
    }

    public void Dispose()
    {
        GL.glBindFramebuffer(OpenGl.GL_FRAMEBUFFER, default);
        var rbs = stackalloc GlRenderbuffer[2] { _color, _depth };
        GL.glDeleteRenderbuffers(2, rbs);
        var fbo = _fbo;
        GL.glDeleteFramebuffers(1, &fbo);
    }
}
