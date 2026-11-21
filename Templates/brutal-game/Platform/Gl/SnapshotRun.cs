using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.SdlApi;
using Brutal.StbApi;
using BrutalGame.Sim;
using GL = Brutal.OpenGlApi.OpenGl.Native;

namespace BrutalGame.Platform;

/// <summary>
/// --snapshot: plays a scripted session through the real drawing code and saves PNGs. It renders into an off-screen
/// framebuffer of a hidden GL window, so nothing appears on screen (it still needs a desktop session and a GL 4.1
/// driver). Driven by game time, never frame count (T33). Exit code 0 = every shot saved and not blank.
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
            using var gl = new GlWindow("BrutalGame snapshot", 320, 180, hidden: true);
            using var renderer = new Renderer();
            using var target = new OffscreenTarget(1280, 720);
            var world = new World();
            var flow = new ScreenFlow();

            // (time in seconds since start, input held until the next entry, screenshot taken at that time)
            (float At, InputState Input, string? Shot)[] script =
            [
                (0.0f, default, null),
                (0.8f, default, "01_splash.png"),
                (1.2f, new InputState(float2.Zero, Confirm: true), null), // skip is allowed after 1 s
                (1.25f, new InputState(new float2(1, 0), false), null),
                (2.5f, new InputState(new float2(0, 1), false), "02_play.png"),
                (3.5f, new InputState(new float2(-1, -1), false), "03_play.png"),
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
                    held = held with { Confirm = false }; // edge inputs last one step
                    t += World.Dt;
                }
                if (shot != null)
                    failures += Shot(renderer, target, flow, world, false, Path.Combine(dir, shot), t);
            }
            if (flow.Current != Screen.Play)
            {
                Console.WriteLine("Script never left the splash screen.");
                failures++;
            }

            // The pause overlay over the last play frame, then the same frame at 1080p (text re-rasterised, stays sharp).
            failures += Shot(renderer, target, flow, world, true, Path.Combine(dir, "05_paused.png"), t);
            using var big = new OffscreenTarget(1920, 1080);
            failures += Shot(renderer, big, flow, world, false, Path.Combine(dir, "06_play_1080p.png"), t);
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
        renderer.Frame(flow, View.Of(world), paused, target.Width, target.Height);
        var lit = target.SavePng(path, out var distinct);
        var ok = lit > 5000 && distinct > 5;
        Console.WriteLine(
            $"{(ok ? "saved" : "BLANK")} {Path.GetFileName(path)}: {lit} lit px, {distinct} colours, t={t:F2}s, screen={flow.Current}, marker={world.Marker}"
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
