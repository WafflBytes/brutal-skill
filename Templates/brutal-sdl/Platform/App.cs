using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// Window, main loop and frame pacing for interactive play. Main thread only (T16).
/// Owns every native handle and frees them in reverse order.
/// </summary>
public sealed unsafe class App : IDisposable
{
    readonly SdlWindow _window;
    readonly SdlRenderer _renderer;
    readonly Gfx _gfx;
    readonly Audio _audio;
    readonly World _world = new();
    readonly ScreenFlow _flow = new();

    bool _running = true;
    readonly float _smokeSeconds; // > 0: --smoke, scripted input, exits by itself
    float _elapsed;
    bool _smokeResized;
    int _frames;
    bool _confirmPressed; // edge-triggered: set by a key-down event, consumed by the next fixed step
    bool _restartPressed;

    // Frame pacing. With vsync the loop runs at the monitor's refresh rate (T33), so simulation uses a fixed
    // step. Over Remote Desktop, vsync presents can block ~150 ms (T15): measure, then fall back to a limiter.
    bool _vsync;
    bool _pacingDecided;
    readonly double[] _presentMs = new double[30];
    int _presentCount;

    public App(float smokeSeconds = 0)
    {
        _smokeSeconds = smokeSeconds;
        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events);
        _window = Sdl.Video.CreateWindow("BrutalGame", 1280, 720, SdlWindowFlags.Resizable);
        _renderer = Sdl.Render.CreateRenderer(_window, null!); // null = SDL picks (direct3d11 on Windows)
        _vsync = Sdl.Render.SetRenderVSync(_renderer, 1);
        _gfx = new Gfx(_renderer);
        _audio = Audio.OpenDevice(gain: smokeSeconds > 0 ? 0 : 1); // --smoke exercises audio, muted
        if (!_audio.Enabled)
            Console.WriteLine($"Audio disabled: {_audio.DisabledReason}");
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var acc = 0.0;
        while (_running)
        {
            var now = clock.Elapsed.TotalSeconds;
            acc += Math.Min(now - last, 0.1); // clamp long stalls (window drag, breakpoint)
            last = now;
            PumpEvents();
            if (_smokeSeconds > 0 && _elapsed >= _smokeSeconds)
                _running = false;
            if (_smokeSeconds > 0 && !_smokeResized && _elapsed >= _smokeSeconds / 2)
            {
                Sdl.Video.SetWindowSize(_window, 1600, 900); // exercises the resize path (text re-rasterises)
                _smokeResized = true;
            }

            var steps = 0;
            while (acc >= World.Dt && steps < 4)
            {
                FixedStep();
                acc -= World.Dt;
                steps++;
            }
            if (steps == 4)
                acc = 0; // too far behind: drop time rather than spiral

            _gfx.Frame(_flow, _world);
            var t0 = clock.Elapsed.TotalMilliseconds;
            Sdl.Render.RenderPresent(_renderer);
            PacePresent(clock.Elapsed.TotalMilliseconds - t0);
            if (!_vsync)
                LimitFrameRate(clock, now);
            _frames++;
        }
    }

    /// <summary>
    /// --smoke: runs the real window loop (vsync, events, presents) with scripted input for a few seconds, so
    /// the interactive path gets executed without a human. Exit code 0 = frames presented and gameplay reached.
    /// </summary>
    public int RunSmoke()
    {
        var scaleBefore = _gfx.TextScale;
        Run();
        Console.WriteLine($"Smoke: text scale {scaleBefore:F2} -> {_gfx.TextScale:F2} after resizing to 1600x900");
        var ok = Math.Abs(_gfx.TextScale - scaleBefore) > 0.01f && _frames > 0 && _flow.Current == Screen.Play && _world.Ticks > 0;
        Console.WriteLine(
            $"Smoke: {_frames} frames in {_elapsed:F1} s game time, vsync {_vsync}, renderer {Sdl.Render.GetRendererName(_renderer)}, audio {(_audio.Enabled ? "on (muted)" : "unavailable")}, screen {_flow.Current}, score {_world.Score}"
        );
        return ok ? 0 : 1;
    }

    void PumpEvents()
    {
        while (Sdl.Events.PollEvent(out var ev))
        {
            if (ev.type == SdlEventType.Quit)
                _running = false;
            else if (ev.type == SdlEventType.KeyDown && ev.key.repeat == 0) // repeat is a byte
            {
                switch (ev.key.key)
                {
                    case SdlKeycode.Escape:
                        _running = false;
                        break;
                    case SdlKeycode.Return or SdlKeycode.Space:
                        _confirmPressed = true;
                        break;
                    case SdlKeycode.R:
                        _restartPressed = true;
                        break;
                }
            }
            else if (ev.type == SdlEventType.MouseButtonDown)
                _confirmPressed = true;
        }
    }

    void FixedStep()
    {
        var input = _smokeSeconds > 0 ? SmokeInput() : ReadInput();
        _elapsed += World.Dt;
        _confirmPressed = false;
        _restartPressed = false;
        var before = _flow.Current;
        _flow.Step(World.Dt, input.Confirm);
        if (before == Screen.Splash && _flow.Current == Screen.Play)
            _audio.Play(Sfx.Start);
        if (_flow.Current != Screen.Play)
            return;
        _world.Step(input);
        if (_world.PickupsThisStep > 0)
            _audio.Play(Sfx.Pickup);
        if (_world.RestartedThisStep)
            _audio.Play(Sfx.Restart);
    }

    InputState SmokeInput() =>
        _elapsed switch
        {
            < 1.2f => default,
            < 1.25f => new InputState(float2.Zero, Confirm: true, Restart: false), // skip the splash (allowed after 1 s)
            _ => new InputState(new float2(MathF.Cos(_elapsed), MathF.Sin(_elapsed * 1.3f)), false, false),
        };

    InputState ReadInput()
    {
        byte* keys = Sdl.PInvoke.GetKeyboardState(null); // raw: no per-frame allocation
        var move = float2.Zero;
        if (keys[(int)SdlScancode.Left] != 0 || keys[(int)SdlScancode.A] != 0)
            move.X -= 1;
        if (keys[(int)SdlScancode.Right] != 0 || keys[(int)SdlScancode.D] != 0)
            move.X += 1;
        if (keys[(int)SdlScancode.Up] != 0 || keys[(int)SdlScancode.W] != 0)
            move.Y -= 1;
        if (keys[(int)SdlScancode.Down] != 0 || keys[(int)SdlScancode.S] != 0)
            move.Y += 1;
        return new InputState(move, _confirmPressed, _restartPressed);
    }

    void PacePresent(double ms)
    {
        if (_pacingDecided || !_vsync)
            return;
        _presentMs[_presentCount++ % _presentMs.Length] = ms;
        if (_presentCount < 60)
            return;
        _pacingDecided = true;
        if (_presentMs.Average() > 25.0)
        {
            Sdl.Render.SetRenderVSync(_renderer, 0);
            _vsync = false;
        }
    }

    static void LimitFrameRate(Stopwatch clock, double frameStart)
    {
        const double target = 1.0 / 60.0;
        while (clock.Elapsed.TotalSeconds - frameStart < target - 0.002)
            Thread.Sleep(1);
        while (clock.Elapsed.TotalSeconds - frameStart < target)
            Thread.SpinWait(50);
    }

    public void Dispose()
    {
        _audio.Dispose();
        _gfx.Dispose();
        Sdl.Render.DestroyRenderer(_renderer);
        Sdl.Video.DestroyWindow(_window);
        Sdl.Init.Quit();
    }
}
