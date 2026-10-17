using System.Diagnostics;
using Brutal.Numerics;
using Brutal.SdlApi;
using BrutalGame3D.Sim;

namespace BrutalGame3D.Platform;

/// <summary>
/// Window, main loop, input (keyboard + gamepad), pause, frame pacing and sounds for interactive play. Main thread
/// only (T16). Owns every native handle and frees them in reverse order.
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
    bool _restartPressed;
    SdlGamepad _pad;
    bool _hasPad;
    float _lastBump = -1;

    // Frame pacing. With vsync the loop runs at the monitor's refresh rate (T33), so simulation uses a fixed step
    // and rendering interpolates. Over Remote Desktop vsync can block (T15): measure, then fall back to a limiter.
    bool _vsync;
    bool _pacingDecided;
    readonly double[] _presentMs = new double[30];
    int _presentCount;

    // --smoke state.
    readonly float _smokeSeconds;
    float _elapsed;
    int _frames;
    int _smokeStep;
    readonly List<string> _smokeFailures = [];
    float3 _smokeMark;
    float _smokeYaw;
    long _smokeTicks;

    public App(float smokeSeconds = 0)
    {
        _smokeSeconds = smokeSeconds;
        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events | SdlInitFlags.Gamepad);
        _gl = new GlWindow("BrutalGame3D", 1280, 720, hidden: false);
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
            var view = View.Lerp(_prevView, _currView, alpha);
            var (w, h) = _gl.PixelSize;
            _renderer.Frame(_flow, _world, view, _paused, w, h);
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
                case SdlEventType.GamepadAdded when !_hasPad:
                    try
                    {
                        _pad = Sdl.Gamepad.OpenGamepad(ev.gdevice.which);
                        _hasPad = true;
                    }
                    catch (SdlException) { } // unusable pad: keyboard still works
                    break;
                case SdlEventType.GamepadRemoved when _hasPad:
                    Sdl.Gamepad.CloseGamepad(_pad);
                    _hasPad = false;
                    break;
                case SdlEventType.GamepadButtonDown:
                    switch ((SdlGamepadButton)ev.gbutton.button)
                    {
                        case SdlGamepadButton.South:
                            _confirmPressed = true;
                            break;
                        case SdlGamepadButton.Back:
                            _restartPressed = true;
                            break;
                        case SdlGamepadButton.Start when _flow.Current == Screen.Play:
                            _paused = !_paused;
                            break;
                    }
                    break;
                case SdlEventType.WindowFocusGained when _smokeSeconds > 0:
                    Console.WriteLine($"     [{_elapsed:F2}s] focus gained");
                    break;
                // --smoke: a real focus change (another app on the desktop) would toggle pause behind the
                // script's back, so only the script's own synthetic event (windowID 0) counts there.
                case SdlEventType.WindowFocusLost when _smokeSeconds > 0 && ev.window.windowID != 0:
                    Console.WriteLine($"     [{_elapsed:F2}s] real focus loss ignored in --smoke");
                    break;
                case SdlEventType.WindowFocusLost:
                    if (_smokeSeconds > 0)
                        Console.WriteLine($"     [{_elapsed:F2}s] focus lost");
                    // Alt-Tab: stop the game and forget held keys, whose KeyUp goes to the other window.
                    _held.Clear();
                    if (_flow.Current == Screen.Play && _world.State == GameState.Playing)
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
                if (_smokeSeconds > 0)
                    Console.WriteLine($"     [{_elapsed:F2}s] P -> paused {_paused}");
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
        if (before == Screen.Splash)
            input = input with { Confirm = false }; // the key that skipped the splash isn't also a game input
        _prevView = _currView;
        _world.Step(input);
        _currView = View.Of(_world);
        if (_world.RestartedThisStep)
        {
            _prevView = _currView; // no interpolation across a restart
            _audio.Play(Sfx.Restart);
        }
        if (_world.CoinsThisStep > 0)
            _audio.Play(Sfx.Coin);
        if (_world.BumpThisStep > 0 && _world.Time - _lastBump > 0.12f)
        {
            _audio.PlayBump(_world.BumpThisStep);
            _lastBump = _world.Time;
        }
        if (_world.FellThisStep)
            _prevView = _currView;
        if (_world.TickThisStep)
            _audio.Play(Sfx.Tick);
        if (_world.FinishedThisStep)
            _audio.Play(_world.State == GameState.Won ? Sfx.Win : Sfx.TimeUp);
    }

    InputState ReadInput()
    {
        var move = float2.Zero;
        var turn = 0f;
        if (_held.Contains(SdlScancode.Left) || _held.Contains(SdlScancode.A))
            move.X -= 1;
        if (_held.Contains(SdlScancode.Right) || _held.Contains(SdlScancode.D))
            move.X += 1;
        if (_held.Contains(SdlScancode.Up) || _held.Contains(SdlScancode.W))
            move.Y += 1;
        if (_held.Contains(SdlScancode.Down) || _held.Contains(SdlScancode.S))
            move.Y -= 1;
        if (_held.Contains(SdlScancode.Q))
            turn -= 1;
        if (_held.Contains(SdlScancode.E))
            turn += 1;
        if (_hasPad)
        {
            static float Axis(short v) => MathF.Abs(v) < 7000 ? 0 : v / 32767f; // dead zone
            move += new float2(Axis(Sdl.Gamepad.GetGamepadAxis(_pad, SdlGamepadAxis.Leftx)), -Axis(Sdl.Gamepad.GetGamepadAxis(_pad, SdlGamepadAxis.Lefty)));
            turn += Axis(Sdl.Gamepad.GetGamepadAxis(_pad, SdlGamepadAxis.Rightx));
        }
        return new InputState(move, Math.Clamp(turn, -1, 1), _confirmPressed, _restartPressed);
    }

    // ---- --smoke ----------------------------------------------------------------------------------------------

    /// <summary>
    /// --smoke: runs the real window loop (vsync, GL, events, swaps, audio muted) for a few seconds and drives it
    /// with synthetic SDL events, so input, camera turn, pause, focus loss and resize run as they do for a player.
    /// Exit code 0 = every scripted check passed.
    /// </summary>
    public int RunSmoke()
    {
        var scaleBefore = _renderer.TextScale;
        Run();
        if (_smokeStep < SmokeSteps)
            _smokeFailures.Add($"script ran {_smokeStep}/{SmokeSteps} steps; use --smoke 5 or longer");
        Console.WriteLine(
            $"Smoke: {_frames} frames in {_elapsed:F1} s game time, vsync {_vsync}, MSAA {_gl.Multisampled}, audio {(_audio.Enabled ? "on (muted)" : "unavailable")}, gamepad {_hasPad}, text scale {scaleBefore:F2} -> {_renderer.TextScale:F2}"
        );
        foreach (var f in _smokeFailures)
            Console.WriteLine($"Smoke FAIL: {f}");
        Console.WriteLine(_smokeFailures.Count == 0 ? "Smoke: all scripted checks passed." : $"Smoke: {_smokeFailures.Count} check(s) failed.");
        return _smokeFailures.Count;
    }

    const int SmokeSteps = 11;

    /// <summary>Scripted by game time (T33), one step per entry, each waiting for its start time.</summary>
    void SmokeScript()
    {
        (float At, Action Do)[] script =
        [
            (1.2f, () => Key(SdlKeycode.Return, SdlScancode.Return)), // skip the splash (allowed after 1 s)
            (1.5f, () => { _smokeMark = _world.BallPosition; Push(SdlEventType.KeyDown, SdlKeycode.W, SdlScancode.W); }),
            (2.5f, () => { Push(SdlEventType.KeyUp, SdlKeycode.W, SdlScancode.W); Expect(_world.BallPosition.Z < _smokeMark.Z - 1.5f, $"holding W rolled the ball forward (z {_smokeMark.Z:F1} -> {_world.BallPosition.Z:F1})"); }),
            (2.6f, () => { _smokeYaw = _world.CameraYaw; Push(SdlEventType.KeyDown, SdlKeycode.E, SdlScancode.E); }),
            (3.0f, () => { Push(SdlEventType.KeyUp, SdlKeycode.E, SdlScancode.E); Expect(_world.CameraYaw > _smokeYaw + 0.5f, $"holding E turned the camera ({_smokeYaw:F2} -> {_world.CameraYaw:F2})"); }),
            (3.1f, () => Sdl.Video.SetWindowSize(_gl.Window, 1600, 900)),
            (3.4f, () => { Expect(_renderer.TextScale > 1.1f, $"text re-rasterised for the bigger window (scale {_renderer.TextScale:F2})"); Key(SdlKeycode.P, SdlScancode.P); }),
            (3.5f, () => { Expect(_paused, "P paused the game"); _smokeTicks = _world.Ticks; }),
            (3.9f, () => { Expect(_world.Ticks == _smokeTicks, "the world doesn't step while paused"); Key(SdlKeycode.P, SdlScancode.P); }),
            (4.1f, () => { Expect(!_paused && _world.Ticks > _smokeTicks, "P resumed the game"); Push(SdlEventType.WindowFocusLost, default, default); }),
            (4.3f, () => { Expect(_paused, "losing focus paused the game"); Key(SdlKeycode.P, SdlScancode.P); _running = _elapsed < _smokeSeconds; }),
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
        _world.Dispose();
        if (_hasPad)
            Sdl.Gamepad.CloseGamepad(_pad);
        _gl.Dispose();
        Sdl.Init.Quit();
    }
}
