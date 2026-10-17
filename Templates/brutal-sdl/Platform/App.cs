using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// Window, main loop, input, pause and frame pacing for interactive play. Main thread only (T16).
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
    bool _paused;

    // Input comes only from SDL events: held keys are tracked from KeyDown/KeyUp, presses are edge-triggered and
    // consumed by the next fixed step. Because nothing polls the OS keyboard directly, --smoke can drive the real
    // input path by pushing synthetic events into SDL's queue.
    readonly HashSet<SdlScancode> _held = [];
    bool _confirmPressed;
    bool _restartPressed;

    // Frame pacing. With vsync the loop runs at the monitor's refresh rate (T33), so simulation uses a fixed
    // step. Over Remote Desktop, vsync presents can block ~150 ms (T15): measure, then fall back to a limiter.
    bool _vsync;
    bool _pacingDecided;
    readonly double[] _presentMs = new double[30];
    int _presentCount;

    // --smoke state.
    readonly float _smokeSeconds; // > 0: scripted run that exits by itself
    float _elapsed; // seconds of fixed steps run (counts while paused too)
    int _frames;
    int _smokeStep;
    readonly List<string> _smokeFailures = [];
    float _smokeMark;
    long _smokeTicks;

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
            if (_smokeSeconds > 0)
                SmokeScript();
            PumpEvents();

            var steps = 0;
            while (acc >= World.Dt && steps < 4)
            {
                FixedStep();
                acc -= World.Dt;
                steps++;
            }
            if (steps == 4)
                acc = 0; // too far behind: drop time rather than spiral

            _gfx.Frame(_flow, _world, _paused);
            var t0 = clock.Elapsed.TotalMilliseconds;
            Sdl.Render.RenderPresent(_renderer);
            PacePresent(clock.Elapsed.TotalMilliseconds - t0);
            if (!_vsync)
                LimitFrameRate(clock, now);
            _frames++;
        }
    }

    void PumpEvents()
    {
        while (Sdl.Events.PollEvent(out var ev))
        {
            switch (ev.type)
            {
                case SdlEventType.Quit:
                    _running = false;
                    break;
                case SdlEventType.KeyDown:
                    _held.Add(ev.key.scancode);
                    if (ev.key.repeat == 0) // repeat is a byte
                        OnKeyPressed(ev.key.key);
                    break;
                case SdlEventType.KeyUp:
                    _held.Remove(ev.key.scancode);
                    break;
                case SdlEventType.MouseButtonDown:
                    _confirmPressed = true;
                    break;
                case SdlEventType.WindowFocusLost:
                    // Alt-Tab: stop the game and forget held keys, whose KeyUp goes to the other window.
                    _held.Clear();
                    if (_flow.Current == Screen.Play)
                        _paused = true;
                    break;
            }
        }
    }

    void OnKeyPressed(SdlKeycode key)
    {
        switch (key)
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
            case SdlKeycode.P when _flow.Current == Screen.Play:
                _paused = !_paused;
                break;
        }
    }

    void FixedStep()
    {
        var input = ReadInput();
        _elapsed += World.Dt;
        _confirmPressed = false;
        _restartPressed = false;
        if (_paused)
            return;
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

    InputState ReadInput()
    {
        var move = float2.Zero;
        if (_held.Contains(SdlScancode.Left) || _held.Contains(SdlScancode.A))
            move.X -= 1;
        if (_held.Contains(SdlScancode.Right) || _held.Contains(SdlScancode.D))
            move.X += 1;
        if (_held.Contains(SdlScancode.Up) || _held.Contains(SdlScancode.W))
            move.Y -= 1;
        if (_held.Contains(SdlScancode.Down) || _held.Contains(SdlScancode.S))
            move.Y += 1;
        return new InputState(move, _confirmPressed, _restartPressed);
    }

    // ---- --smoke ----------------------------------------------------------------------------------------------

    /// <summary>
    /// --smoke: runs the real window loop (vsync, events, presents, audio muted) for a few seconds and drives it
    /// with synthetic SDL events, so input, pause, focus loss and resize run exactly as they do for a player.
    /// Exit code 0 = every scripted check passed.
    /// </summary>
    public int RunSmoke()
    {
        var scaleBefore = _gfx.TextScale;
        Run();
        if (Math.Abs(_gfx.TextScale - scaleBefore) < 0.01f)
            _smokeFailures.Add($"text scale didn't change on resize ({scaleBefore:F2})");
        if (_smokeStep < SmokeSteps)
            _smokeFailures.Add($"script ran {_smokeStep}/{SmokeSteps} steps; use --smoke 4 or longer");
        Console.WriteLine(
            $"Smoke: {_frames} frames in {_elapsed:F1} s game time, vsync {_vsync}, renderer {Sdl.Render.GetRendererName(_renderer)}, audio {(_audio.Enabled ? "on (muted)" : "unavailable")}, text scale {scaleBefore:F2} -> {_gfx.TextScale:F2}"
        );
        foreach (var f in _smokeFailures)
            Console.WriteLine($"Smoke FAIL: {f}");
        Console.WriteLine(_smokeFailures.Count == 0 ? "Smoke: all scripted checks passed." : $"Smoke: {_smokeFailures.Count} check(s) failed.");
        return _smokeFailures.Count;
    }

    const int SmokeSteps = 9;

    /// <summary>Scripted by game time (T33), one step per entry, each waiting for its start time.</summary>
    void SmokeScript()
    {
        (float At, Action Do)[] script =
        [
            (1.2f, () => Key(SdlKeycode.Return, SdlScancode.Return)), // skip the splash (allowed after 1 s)
            (1.4f, () => { _smokeMark = _world.Player.X; Push(SdlEventType.KeyDown, SdlKeycode.Right, SdlScancode.Right); }),
            (1.9f, () => { Push(SdlEventType.KeyUp, SdlKeycode.Right, SdlScancode.Right); Expect(_world.Player.X > _smokeMark + 50, $"holding Right moved the player ({_smokeMark:F0} -> {_world.Player.X:F0})"); }),
            (2.0f, () => { Sdl.Video.SetWindowSize(_window, 1600, 900); }), // resize: text re-rasterises
            (2.2f, () => { Key(SdlKeycode.P, SdlScancode.P); }),
            (2.3f, () => { Expect(_paused, "P paused the game"); _smokeTicks = _world.Ticks; }),
            (2.7f, () => { Expect(_world.Ticks == _smokeTicks, "the world doesn't step while paused"); Key(SdlKeycode.P, SdlScancode.P); }),
            (2.9f, () => { Expect(!_paused && _world.Ticks > _smokeTicks, "P resumed the game"); Push(SdlEventType.WindowFocusLost, default, default); }),
            (3.1f, () => { Expect(_paused, "losing focus paused the game"); Key(SdlKeycode.P, SdlScancode.P); _running = _elapsed < _smokeSeconds; }),
        ];
        if (_smokeStep < script.Length && _elapsed >= script[_smokeStep].At)
            script[_smokeStep++].Do();
        if (_elapsed >= _smokeSeconds)
            _running = false;
    }

    void Expect(bool ok, string what)
    {
        Console.WriteLine($"Smoke {(ok ? "PASS" : "FAIL")}: {what}");
        if (!ok)
            _smokeFailures.Add(what);
    }

    void Key(SdlKeycode key, SdlScancode scancode)
    {
        Push(SdlEventType.KeyDown, key, scancode);
        Push(SdlEventType.KeyUp, key, scancode);
    }

    static void Push(SdlEventType type, SdlKeycode key, SdlScancode scancode)
    {
        var ev = new SdlEvent();
        if (type == SdlEventType.WindowFocusLost)
            ev.window.type = type;
        else
        {
            ev.key.type = type;
            ev.key.key = key;
            ev.key.scancode = scancode;
            ev.key.down = (byte)(type == SdlEventType.KeyDown ? 1 : 0);
        }
        Sdl.Events.PushEvent(&ev);
    }

    // ---- frame pacing -----------------------------------------------------------------------------------------

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
