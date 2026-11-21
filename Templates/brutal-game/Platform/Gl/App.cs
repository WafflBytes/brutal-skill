using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// Window, main loop, input, pause, frame pacing and sounds for interactive play. Main thread only (T16).
/// Owns every native handle and frees them in reverse order.
/// </summary>
public sealed unsafe class App : IDisposable
{
    readonly GlWindow _gl;
    readonly Renderer _renderer;
    readonly Audio _audio;
    readonly World _world = new();
    readonly ScreenFlow _flow = new();
    View _prevView;
    View _currView;

    bool _running = true;
    bool _paused;

    // Input comes only from SDL events: held keys are tracked from KeyDown/KeyUp, presses are edge-triggered and
    // consumed by the next fixed step, so --smoke can drive the real input path with synthetic events.
    readonly HashSet<SdlScancode> _held = [];
    bool _confirmPressed;

    // Frame pacing. With vsync the loop runs at the monitor's refresh rate (T33), so simulation uses a fixed step
    // and rendering interpolates. Over Remote Desktop vsync can block (T15): measure, then fall back to a limiter.
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
        _gl = new GlWindow("BrutalGame", 1280, 720, hidden: false);
        _vsync = _gl.SetVSync(true);
        _renderer = new Renderer();
        _audio = Audio.OpenDevice(gain: smokeSeconds > 0 ? 0 : 1); // --smoke exercises audio, muted
        if (!_audio.Enabled)
            Console.WriteLine($"Audio disabled: {_audio.DisabledReason}");
        _prevView = _currView = View.Of(_world);
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

            var alpha = _paused ? 1f : (float)(acc / World.Dt);
            var (w, h) = _gl.PixelSize;
            _renderer.Frame(_flow, View.Lerp(_prevView, _currView, alpha), _paused, w, h);
            var t0 = clock.Elapsed.TotalMilliseconds;
            _gl.Swap();
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
                    // --smoke drives pause with its own synthetic event (windowID 0). Ignore real focus changes then:
                    // another window on the desktop would otherwise pause the game behind the script's back.
                    if (_smokeSeconds > 0 && ev.window.windowID != 0)
                        break;
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
        if (_paused)
            return;
        var before = _flow.Current;
        _flow.Step(World.Dt, input.Confirm);
        if (before == Screen.Splash && _flow.Current == Screen.Play)
            _audio.Play(Sfx.Start);
        if (_flow.Current != Screen.Play)
            return;
        if (before == Screen.Splash)
            input = input with { Confirm = false }; // the key that skipped the splash isn't also a game input
        _prevView = _currView;
        _world.Step(input);
        _currView = View.Of(_world);
        // Play sounds here from per-step events your World reports (e.g. a PickupsThisStep counter).
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
        return new InputState(move, _confirmPressed);
    }

    // ---- --smoke ----------------------------------------------------------------------------------------------

    /// <summary>
    /// --smoke: runs the real window loop (vsync, GL, events, swaps, audio muted) for a few seconds and drives it
    /// with synthetic SDL events, so input, pause, focus loss and resize run exactly as they do for a player.
    /// Exit code 0 = every scripted check passed.
    /// </summary>
    public int RunSmoke()
    {
        var scaleBefore = _renderer.TextScale;
        Run();
        if (_smokeStep < SmokeSteps)
            _smokeFailures.Add($"script ran {_smokeStep}/{SmokeSteps} steps; use --smoke 4 or longer");
        Console.WriteLine(
            $"Smoke: {_frames} frames in {_elapsed:F1} s game time, vsync {_vsync}, MSAA {_gl.Multisampled}, audio {(_audio.Enabled ? "on (muted)" : "unavailable")}, text scale {scaleBefore:F2} -> {_renderer.TextScale:F2}"
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
            (1.4f, () => { _smokeMark = _world.Marker.X; Push(SdlEventType.KeyDown, SdlKeycode.Right, SdlScancode.Right); }),
            (1.9f, () => { Push(SdlEventType.KeyUp, SdlKeycode.Right, SdlScancode.Right); Expect(_world.Marker.X > _smokeMark + 1, $"holding Right moved the marker ({_smokeMark:F1} -> {_world.Marker.X:F1})"); }),
            (2.0f, () => Sdl.Video.SetWindowSize(_gl.Window, 1600, 900)),
            (2.2f, () => { Expect(_renderer.TextScale > 1.1f, $"text re-rasterised for the bigger window (scale {_renderer.TextScale:F2})"); Key(SdlKeycode.P, SdlScancode.P); }),
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
            _gl.SetVSync(false);
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
        _renderer.Dispose(); // needs the GL context, so before the window goes
        _gl.Dispose();
        Sdl.Init.Quit();
    }
}
