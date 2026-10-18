// Recipe 7.35: Dear ImGui on an SDL3 window with an OpenGL 4.1 core context. BRUTAL ships only a GLFW platform
// backend (T22), so SdlImGuiGlue below is a hand-written IPlatformBackend; the shipped OpenGlBackend renders.
// Needs a display (a hidden window is enough); skipped with --no-window.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.SdlApi;

/// <summary>Feeds SDL3 events and per-frame window state to Dear ImGui. Call ProcessEvent for every SDL event, NewFrame once per frame.
/// Also wires the clipboard, mouse cursor shapes and gamepad navigation (the game opens the pad; SDL only sends button events for open pads).</summary>
sealed unsafe class SdlImGuiGlue : IPlatformBackend
{
    readonly SdlWindow _window;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;
    // ImGui keeps only the function pointers: the delegates must stay rooted (T35)
    readonly ImGuiPlatformIO.GetClipboardTextFnDelegate _getClipboard;
    readonly ImGuiPlatformIO.SetClipboardTextFnDelegate _setClipboard;
    byte* _clipboardText; // SDL_GetClipboardText allocates; ImGui reads it until the next call, so free the previous one then
    readonly SdlCursor[] _cursors = new SdlCursor[(int)ImGuiMouseCursor.COUNT]; // created on first use: needs the video subsystem
    ImGuiMouseCursor _lastCursor = ImGuiMouseCursor.COUNT;

    public int ClipboardReads, ClipboardWrites;

    public SdlImGuiGlue(SdlWindow window)
    {
        _window = window;
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        _getClipboard = GetClipboardText;
        _setClipboard = SetClipboardText;
        var platformIo = ImGui.GetPlatformIO();
        platformIo.Platform_GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_getClipboard);
        platformIo.Platform_SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_setClipboard);
    }

    public void NewFrame()
    {
        var io = ImGui.GetIO();
        Sdl.Video.GetWindowSize(_window, out var w, out var h);
        Sdl.Video.GetWindowSizeInPixels(_window, out var pw, out var ph);
        io.DisplaySize = new float2(w, h);
        io.DisplayFramebufferScale = w > 0 && h > 0 ? new float2((float)pw / w, (float)ph / h) : float2.One;
        var now = _clock.Elapsed.TotalSeconds;
        io.DeltaTime = _last > 0 ? (float)Math.Max(now - _last, 1e-5) : 1f / 60f;
        _last = now;
        UpdateMouseCursor();
    }

    byte* GetClipboardText(ImGuiContextPtr ctx)
    {
        ClipboardReads++;
        if (_clipboardText != null)
            Sdl.PInvoke.Free(_clipboardText);
        _clipboardText = Sdl.PInvoke.GetClipboardText(); // "" (not null) when empty
        return _clipboardText;
    }

    void SetClipboardText(ImGuiContextPtr ctx, byte* text)
    {
        ClipboardWrites++;
        Sdl.PInvoke.SetClipboardText(Marshal.PtrToStringUTF8((nint)text) ?? "");
    }

    // ImGui computes the wanted cursor during a frame; apply it at the start of the next one. Only on change: SetCursor every frame flickers on some systems.
    void UpdateMouseCursor()
    {
        var io = ImGui.GetIO();
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
            return;
        var wanted = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
        if (wanted == _lastCursor)
            return;
        _lastCursor = wanted;
        if (wanted == ImGuiMouseCursor.None)
        {
            Sdl.Mouse.HideCursor();
            return;
        }
        Sdl.Mouse.SetCursor(CursorFor(wanted));
        Sdl.Mouse.ShowCursor();
    }

    public SdlCursor CursorFor(ImGuiMouseCursor c)
    {
        ref var slot = ref _cursors[(int)c];
        if (slot.IsNull())
            slot = Sdl.Mouse.CreateSystemCursor(c switch
            {
                ImGuiMouseCursor.TextInput => SdlSystemCursor.Text,
                ImGuiMouseCursor.ResizeAll => SdlSystemCursor.Move,
                ImGuiMouseCursor.ResizeNS => SdlSystemCursor.NsResize,
                ImGuiMouseCursor.ResizeEW => SdlSystemCursor.EwResize,
                ImGuiMouseCursor.ResizeNESW => SdlSystemCursor.NeswResize,
                ImGuiMouseCursor.ResizeNWSE => SdlSystemCursor.NwseResize,
                ImGuiMouseCursor.Hand => SdlSystemCursor.Pointer,
                ImGuiMouseCursor.Wait => SdlSystemCursor.Wait,
                ImGuiMouseCursor.Progress => SdlSystemCursor.Progress,
                ImGuiMouseCursor.NotAllowed => SdlSystemCursor.NotAllowed,
                _ => SdlSystemCursor.Default,
            });
        return slot;
    }

    static ImGuiKey MapGamepadButton(SdlGamepadButton b) => b switch
    {
        SdlGamepadButton.Start => ImGuiKey.GamepadStart,
        SdlGamepadButton.Back => ImGuiKey.GamepadBack,
        SdlGamepadButton.West => ImGuiKey.GamepadFaceLeft, // Xbox X, PS Square
        SdlGamepadButton.East => ImGuiKey.GamepadFaceRight, // Xbox B, PS Circle
        SdlGamepadButton.North => ImGuiKey.GamepadFaceUp, // Xbox Y, PS Triangle
        SdlGamepadButton.South => ImGuiKey.GamepadFaceDown, // Xbox A, PS Cross: activate
        SdlGamepadButton.DpadLeft => ImGuiKey.GamepadDpadLeft,
        SdlGamepadButton.DpadRight => ImGuiKey.GamepadDpadRight,
        SdlGamepadButton.DpadUp => ImGuiKey.GamepadDpadUp,
        SdlGamepadButton.DpadDown => ImGuiKey.GamepadDpadDown,
        SdlGamepadButton.LeftShoulder => ImGuiKey.GamepadL1,
        SdlGamepadButton.RightShoulder => ImGuiKey.GamepadR1,
        SdlGamepadButton.LeftStick => ImGuiKey.GamepadL3,
        SdlGamepadButton.RightStick => ImGuiKey.GamepadR3,
        _ => ImGuiKey.None,
    };

    // SDL axes: sticks -32768..32767 (up is negative Y), triggers 0..32767. Each stick axis feeds two ImGui keys.
    static void AddGamepadAxis(ImGuiIOPtr io, SdlGamepadAxis axis, short value)
    {
        const float dead = 8000;
        void Analog(ImGuiKey key, float v0, float v1)
        {
            var v = Math.Clamp((value - v0) / (v1 - v0), 0f, 1f);
            io.AddKeyAnalogEvent(key, v > 0.1f, v);
        }
        switch (axis)
        {
            case SdlGamepadAxis.Leftx: Analog(ImGuiKey.GamepadLStickLeft, -dead, -32768); Analog(ImGuiKey.GamepadLStickRight, dead, 32767); break;
            case SdlGamepadAxis.Lefty: Analog(ImGuiKey.GamepadLStickUp, -dead, -32768); Analog(ImGuiKey.GamepadLStickDown, dead, 32767); break;
            case SdlGamepadAxis.Rightx: Analog(ImGuiKey.GamepadRStickLeft, -dead, -32768); Analog(ImGuiKey.GamepadRStickRight, dead, 32767); break;
            case SdlGamepadAxis.Righty: Analog(ImGuiKey.GamepadRStickUp, -dead, -32768); Analog(ImGuiKey.GamepadRStickDown, dead, 32767); break;
            case SdlGamepadAxis.LeftTrigger: Analog(ImGuiKey.GamepadL2, 0, 32767); break;
            case SdlGamepadAxis.RightTrigger: Analog(ImGuiKey.GamepadR2, 0, 32767); break;
        }
    }

    public unsafe void ProcessEvent(in SdlEvent ev)
    {
        var io = ImGui.GetIO();
        switch (ev.type)
        {
            case SdlEventType.MouseMotion:
                io.AddMousePosEvent(ev.motion.x, ev.motion.y);
                break;
            case SdlEventType.MouseButtonDown or SdlEventType.MouseButtonUp:
                // SDL: 1 left, 2 middle, 3 right, 4 X1, 5 X2. ImGui: 0 left, 1 right, 2 middle, 3, 4.
                int button = ev.button.button switch { 1 => 0, 3 => 1, 2 => 2, 4 => 3, 5 => 4, _ => -1 };
                if (button >= 0)
                {
                    io.AddMousePosEvent(ev.button.x, ev.button.y);
                    io.AddMouseButtonEvent(button, ev.type == SdlEventType.MouseButtonDown);
                }
                break;
            case SdlEventType.MouseWheel:
                io.AddMouseWheelEvent(ev.wheel.x, ev.wheel.y);
                break;
            case SdlEventType.TextInput:
                // ev.text.text is a NUL-terminated UTF-8 string
                foreach (var rune in (Marshal.PtrToStringUTF8((nint)ev.text.text) ?? "").EnumerateRunes())
                    io.AddInputCharacter((uint)rune.Value);
                break;
            case SdlEventType.KeyDown or SdlEventType.KeyUp:
                // Shortcuts (Ctrl+C/V/A/Z) read ImGuiKey.Mod_*: ImGui does not derive them from LeftCtrl etc., so send SDL's mod bits first
                var mod = (uint)ev.key.mod;
                io.AddKeyEvent(ImGuiKey.Mod_Ctrl, (mod & Sdl.Constants.KmodCtrl) != 0);
                io.AddKeyEvent(ImGuiKey.Mod_Shift, (mod & Sdl.Constants.KmodShift) != 0);
                io.AddKeyEvent(ImGuiKey.Mod_Alt, (mod & Sdl.Constants.KmodAlt) != 0);
                io.AddKeyEvent(ImGuiKey.Mod_Super, (mod & Sdl.Constants.KmodGui) != 0);
                io.AddKeyEvent(MapKey(ev.key.scancode), ev.type == SdlEventType.KeyDown);
                break;
            case SdlEventType.WindowFocusGained:
                io.AddFocusEvent(true);
                break;
            case SdlEventType.WindowFocusLost:
                io.AddFocusEvent(false);
                break;
            case SdlEventType.WindowMouseLeave:
                io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                break;
            case SdlEventType.GamepadAdded:
                io.BackendFlags |= ImGuiBackendFlags.HasGamepad; // ImGui navigates with a pad only with this and ConfigFlags.NavEnableGamepad
                break;
            case SdlEventType.GamepadRemoved:
                if (!Sdl.Gamepad.HasGamepad())
                    io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
                break;
            case SdlEventType.GamepadButtonDown or SdlEventType.GamepadButtonUp:
                var key = MapGamepadButton((SdlGamepadButton)ev.gbutton.button);
                if (key != ImGuiKey.None)
                    io.AddKeyEvent(key, ev.type == SdlEventType.GamepadButtonDown);
                break;
            case SdlEventType.GamepadAxisMotion:
                AddGamepadAxis(io, (SdlGamepadAxis)ev.gaxis.axis, ev.gaxis.value);
                break;
        }
    }

    public void Dispose()
    {
        var platformIo = ImGui.GetPlatformIO();
        platformIo.Platform_GetClipboardTextFn = 0;
        platformIo.Platform_SetClipboardTextFn = 0;
        if (_clipboardText != null)
            Sdl.PInvoke.Free(_clipboardText);
        _clipboardText = null;
        foreach (var c in _cursors)
            if (!c.IsNull())
                Sdl.Mouse.DestroyCursor(c);
        Array.Clear(_cursors);
    }

    // By scancode (physical key): shortcuts follow the key position, not the layout (on AZERTY, Ctrl+Q is the A key).
    public static ImGuiKey MapKey(SdlScancode s)
    {
        if (s >= SdlScancode.A && s <= SdlScancode.Z)
            return ImGuiKey.A + (int)(s - SdlScancode.A);
        if (s >= SdlScancode.N1 && s <= SdlScancode.N9)
            return ImGuiKey._1 + (int)(s - SdlScancode.N1);
        if (s >= SdlScancode.F1 && s <= SdlScancode.F12)
            return ImGuiKey.F1 + (int)(s - SdlScancode.F1);
        if (s >= SdlScancode.Kp1 && s <= SdlScancode.Kp9)
            return ImGuiKey.Keypad1 + (int)(s - SdlScancode.Kp1);
        return s switch
        {
            SdlScancode.N0 => ImGuiKey._0,
            SdlScancode.Kp0 => ImGuiKey.Keypad0,
            SdlScancode.Tab => ImGuiKey.Tab,
            SdlScancode.Left => ImGuiKey.LeftArrow,
            SdlScancode.Right => ImGuiKey.RightArrow,
            SdlScancode.Up => ImGuiKey.UpArrow,
            SdlScancode.Down => ImGuiKey.DownArrow,
            SdlScancode.Pageup => ImGuiKey.PageUp,
            SdlScancode.Pagedown => ImGuiKey.PageDown,
            SdlScancode.Home => ImGuiKey.Home,
            SdlScancode.End => ImGuiKey.End,
            SdlScancode.Insert => ImGuiKey.Insert,
            SdlScancode.Delete => ImGuiKey.Delete,
            SdlScancode.Backspace => ImGuiKey.Backspace,
            SdlScancode.Space => ImGuiKey.Space,
            SdlScancode.Return => ImGuiKey.Enter,
            SdlScancode.Escape => ImGuiKey.Escape,
            SdlScancode.Apostrophe => ImGuiKey.Apostrophe,
            SdlScancode.Comma => ImGuiKey.Comma,
            SdlScancode.Minus => ImGuiKey.Minus,
            SdlScancode.Period => ImGuiKey.Period,
            SdlScancode.Slash => ImGuiKey.Slash,
            SdlScancode.Semicolon => ImGuiKey.Semicolon,
            SdlScancode.Equals => ImGuiKey.Equal,
            SdlScancode.Leftbracket => ImGuiKey.LeftBracket,
            SdlScancode.Backslash => ImGuiKey.Backslash,
            SdlScancode.Rightbracket => ImGuiKey.RightBracket,
            SdlScancode.Grave => ImGuiKey.GraveAccent,
            SdlScancode.Capslock => ImGuiKey.CapsLock,
            SdlScancode.Scrolllock => ImGuiKey.ScrollLock,
            SdlScancode.Numlockclear => ImGuiKey.NumLock,
            SdlScancode.Printscreen => ImGuiKey.PrintScreen,
            SdlScancode.Pause => ImGuiKey.Pause,
            SdlScancode.KpPeriod => ImGuiKey.KeypadDecimal,
            SdlScancode.KpDivide => ImGuiKey.KeypadDivide,
            SdlScancode.KpMultiply => ImGuiKey.KeypadMultiply,
            SdlScancode.KpMinus => ImGuiKey.KeypadSubtract,
            SdlScancode.KpPlus => ImGuiKey.KeypadAdd,
            SdlScancode.KpEnter => ImGuiKey.KeypadEnter,
            SdlScancode.KpEquals => ImGuiKey.KeypadEqual,
            SdlScancode.Nonusbackslash => ImGuiKey.Oem102,
            SdlScancode.Lctrl => ImGuiKey.LeftCtrl,
            SdlScancode.Lshift => ImGuiKey.LeftShift,
            SdlScancode.Lalt => ImGuiKey.LeftAlt,
            SdlScancode.Lgui => ImGuiKey.LeftSuper,
            SdlScancode.Rctrl => ImGuiKey.RightCtrl,
            SdlScancode.Rshift => ImGuiKey.RightShift,
            SdlScancode.Ralt => ImGuiKey.RightAlt,
            SdlScancode.Rgui => ImGuiKey.RightSuper,
            SdlScancode.Menu or SdlScancode.Application => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
    }
}

static unsafe partial class Recipes
{
    static GlGetProcAddressFunc? s_sdlGlLoader; // must stay rooted (T35)

    public static void ImGuiSdlRecipe()
    {
        // Key table spot checks (no window needed)
        Check(SdlImGuiGlue.MapKey(SdlScancode.A) == ImGuiKey.A && SdlImGuiGlue.MapKey(SdlScancode.Z) == ImGuiKey.Z, "A..Z");
        Check(SdlImGuiGlue.MapKey(SdlScancode.N1) == ImGuiKey._1 && SdlImGuiGlue.MapKey(SdlScancode.N0) == ImGuiKey._0, "digits");
        Check(SdlImGuiGlue.MapKey(SdlScancode.F5) == ImGuiKey.F5, "F5");
        Check(SdlImGuiGlue.MapKey(SdlScancode.Return) == ImGuiKey.Enter, "Return -> Enter");
        Check(SdlImGuiGlue.MapKey(SdlScancode.Lctrl) == ImGuiKey.LeftCtrl, "Lctrl");
        Check(SdlImGuiGlue.MapKey(SdlScancode.Kp3) == ImGuiKey.Keypad3, "Kp3");
        Check(SdlImGuiGlue.MapKey(SdlScancode.Unknown) == ImGuiKey.None, "Unknown -> None");

        // SDL drops gamepad presses while windows exist but none has focus; this window is hidden (a real, focused game window doesn't need it)
        Sdl.PInvoke.SetHint(Sdl.Constants.HintJoystickAllowBackgroundEvents, "1");
        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events | SdlInitFlags.Gamepad);
        SdlWindow window = default!;
        var made = false;
        nint context = 0;
        var textMemory = nint.Zero;
        try
        {
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextMajorVersion, 4);
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextMinorVersion, 1);
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextProfileMask, Sdl.Constants.GlContextProfileCore);
            Sdl.Video.GLSetAttribute(SdlGLAttr.Doublebuffer, 1);
            window = Sdl.Video.CreateWindow("RecipeCheck ImGui SDL", 640, 400, SdlWindowFlags.Opengl | SdlWindowFlags.Hidden);
            made = true;
            context = Sdl.Video.GLCreateContext(window);
            Check(context != 0, "SDL_GL_CreateContext (4.1 core)");
            Sdl.Video.GLMakeCurrent(window, context);
            s_sdlGlLoader = name => (nint)Sdl.PInvoke.GLGetProcAddress(Marshal.PtrToStringUTF8((nint)name)!);
            Check(Glad.Initialize(s_sdlGlLoader), "Glad.Initialize");

            ImGui.CreateContext();
            var io = ImGui.GetIO();
            io.IniFilename = default;
            ImGui.StyleColorsDark();
            var builtInClipboard = ImGui.GetPlatformIO().Platform_GetClipboardTextFn != 0; // ImGui's own Win32 clipboard, before the glue replaces it
            var platform = new SdlImGuiGlue(window);
            var renderer = new OpenGlBackend();
            try
            {
                Sdl.Video.GetWindowSizeInPixels(window, out var fbW, out var fbH);
                var pixels = new byte[fbW * fbH * 4];
                var clicked = 0;
                var volume = 0.0f;
                var showGrid = true;
                var input = new ImInputString(32);
                float2 buttonC = default, sliderMin = default, sliderMax = default, checkC = default, inputC = default;
                bool captureMouseOverUi = false, captureMouseElsewhere = true, wantText = false, wantKeysWhileTyping = false, wantKeysAfter = true;
                var vertices = 0;
                var frame = 0;
                ImDrawCallback? pendingCallback = null;
                var navFocus = "";

                void Push(SdlEvent e) => Sdl.Events.PushEvent(&e);
                void Move(float2 p)
                {
                    var e = new SdlEvent();
                    e.motion.type = SdlEventType.MouseMotion;
                    e.motion.x = p.X;
                    e.motion.y = p.Y;
                    Push(e);
                }
                void Button(float2 p, bool down)
                {
                    var e = new SdlEvent();
                    e.button.type = down ? SdlEventType.MouseButtonDown : SdlEventType.MouseButtonUp;
                    e.button.button = 1;
                    e.button.down = (byte)(down ? 1 : 0);
                    e.button.x = p.X;
                    e.button.y = p.Y;
                    Push(e);
                }
                ushort mods = 0; // what SDL would put in ev.key.mod
                void Key(SdlScancode sc, bool down)
                {
                    if (sc == SdlScancode.Lctrl)
                        mods = (ushort)(down ? mods | Sdl.Constants.KmodLctrl : mods & ~Sdl.Constants.KmodLctrl);
                    var e = new SdlEvent();
                    e.key.type = down ? SdlEventType.KeyDown : SdlEventType.KeyUp;
                    e.key.scancode = sc;
                    e.key.mod = mods;
                    e.key.down = (byte)(down ? 1 : 0);
                    Push(e);
                }
                void Click(float2 p) { Move(p); Frame(); Button(p, true); Frame(); Button(p, false); Frame(); }

                // One frame: drain SDL's queue into the glue, build the UI, render.
                void Frame()
                {
                    while (Sdl.Events.PollEvent(out var ev))
                        platform.ProcessEvent(in ev);
                    renderer.NewFrame();
                    platform.NewFrame();
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(new float2(10, 10));
                    ImGui.SetNextWindowSize(new float2(300, 200));
                    ImGui.Begin("Debug"u8);
                    ImGui.Text($"Frame {frame}");
                    if (ImGui.Button("Click me"u8, new float2(120, 30)))
                        clicked++;
                    navFocus = ImGui.IsItemFocused() ? "button" : "";
                    buttonC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    ImGui.SliderFloat("Volume"u8, ref volume, 0, 1);
                    if (ImGui.IsItemFocused()) navFocus = "slider";
                    sliderMin = ImGui.GetItemRectMin();
                    sliderMax = ImGui.GetItemRectMax();
                    ImGui.Checkbox("Show grid"u8, ref showGrid);
                    if (ImGui.IsItemFocused()) navFocus = "checkbox";
                    checkC = ImGui.GetItemRectMin() + new float2(8, 8);
                    ImGui.InputText("Name"u8, input);
                    if (ImGui.IsItemFocused()) navFocus = "input";
                    inputC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    if (pendingCallback != null)
                        ImGui.GetWindowDrawList().AddCallback(pendingCallback, 42);
                    ImGui.End();
                    ImGui.Render();

                    OpenGl.Native.glViewport(0, 0, fbW, fbH);
                    OpenGl.Native.glClearColor(0, 0, 0, 1);
                    OpenGl.Native.glClear(OpenGl.GL_COLOR_BUFFER_BIT);
                    var drawData = ImGui.GetDrawData();
                    vertices = drawData.TotalVtxCount;
                    renderer.RenderDrawData(drawData);
                    Sdl.Video.GLSwapWindow(window);
                    frame++;
                }

                Frame();
                Frame(); // the window needs a frame or two before item rectangles are final
                Move(buttonC);
                Frame();
                captureMouseOverUi = io.WantCaptureMouse;
                Click(buttonC);
                Click(checkC);
                // slider drag from the left quarter to the right quarter
                var y = (sliderMin.Y + sliderMax.Y) / 2;
                var from = new float2(sliderMin.X + (sliderMax.X - sliderMin.X) * 0.25f, y);
                var to = new float2(sliderMin.X + (sliderMax.X - sliderMin.X) * 0.75f, y);
                Move(from); Frame(); Button(from, true); Frame(); Move(to); Frame(); Button(to, false); Frame();
                // type into the text field: click it, then text and a Backspace
                Click(inputC);
                var text = new SdlEvent();
                text.text.type = SdlEventType.TextInput;
                textMemory = Marshal.StringToCoTaskMemUTF8("hi");
                text.text.text = (byte*)textMemory;
                platform.ProcessEvent(in text); // direct: SDL doesn't keep the pointer of a pushed text event alive for us
                Frame();
                wantText = io.WantTextInput;
                wantKeysWhileTyping = io.WantCaptureKeyboard;
                Key(SdlScancode.Backspace, true); Frame(); Key(SdlScancode.Backspace, false); Frame();
                var typed = System.Text.Encoding.UTF8.GetString(input.Buffer, 0, input.Length);
                // click on empty space: ImGui hands the mouse and keyboard back to the game
                var empty = new float2(560, 360);
                Move(empty);
                Frame();
                captureMouseElsewhere = io.WantCaptureMouse;
                Click(empty);
                Frame();
                wantKeysAfter = io.WantCaptureKeyboard;

                // pixels of the last frame
                fixed (byte* p = pixels)
                    OpenGl.Native.glReadPixels(0, 0, fbW, fbH, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, p);
                var lit = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 30 || pixels[i + 1] > 30 || pixels[i + 2] > 30)
                        lit++;
                Console.WriteLine($"     ImGui on SDL: {vertices} vertices, {lit} lit pixels, clicked {clicked}x, volume {volume:F2}, grid {showGrid}, typed \"{typed}\"");
                var shotDir = Environment.GetEnvironmentVariable("RECIPECHECK_SHOTS");
                if (!string.IsNullOrEmpty(shotDir))
                {
                    var flipped = new byte[pixels.Length];
                    for (var row = 0; row < fbH; row++)
                        Array.Copy(pixels, (fbH - 1 - row) * fbW * 4, flipped, row * fbW * 4, fbW * 4);
                    Directory.CreateDirectory(shotDir);
                    Brutal.StbApi.Stb.WritePng(Path.Combine(shotDir, "7.35-imgui-sdl.png"), fbW, fbH, 4, flipped, fbW * 4);
                }

                Check(vertices > 100 && lit > 2000, "the OpenGL backend drew the window in an SDL context");
                Check(captureMouseOverUi, "WantCaptureMouse is true over the window");
                Check(clicked == 1, "synthetic SDL click made Button() return true once");
                Check(!showGrid, "synthetic SDL click toggled the checkbox");
                Check(volume > 0.3f, $"dragging the slider changed the value (volume {volume:F2})");
                Check(wantText && wantKeysWhileTyping, "text field active: WantTextInput and WantCaptureKeyboard");
                Check(typed == "h", $"text \"hi\" then Backspace left \"h\" (got \"{typed}\")");
                Check(!captureMouseElsewhere, "WantCaptureMouse is false over empty space");
                Check(!wantKeysAfter, "after clicking away WantCaptureKeyboard is false");

                // Mouse cursor shape: hover the text field, the next NewFrame switches SDL's cursor
                Move(inputC); Frame(); Frame();
                var textCursor = Sdl.Mouse.GetCursor().Pointer == platform.CursorFor(ImGuiMouseCursor.TextInput).Pointer;
                Move(empty); Frame(); Frame();
                var arrowCursor = Sdl.Mouse.GetCursor().Pointer == platform.CursorFor(ImGuiMouseCursor.Arrow).Pointer;
                Check(textCursor, "over the text field SDL's cursor is the I-beam (SDL_SYSTEM_CURSOR_TEXT)");
                Check(arrowCursor, "over empty space it is back to the arrow");

                // Clipboard both ways, then Ctrl+A, Ctrl+V into the text field. The user's clipboard is restored afterwards.
                var savedPtr = Sdl.PInvoke.GetClipboardText();
                var savedClipboard = Marshal.PtrToStringUTF8((nint)savedPtr) ?? "";
                Sdl.PInvoke.Free(savedPtr);
                string toSdl, toImGui, pasted;
                try
                {
                    ImGui.SetClipboardText("from ImGui 7.35");
                    var p = Sdl.PInvoke.GetClipboardText();
                    toSdl = Marshal.PtrToStringUTF8((nint)p) ?? "";
                    Sdl.PInvoke.Free(p);
                    Sdl.PInvoke.SetClipboardText("from SDL");
                    toImGui = ImGui.GetClipboardText().ToString();
                    Click(inputC);
                    Key(SdlScancode.Lctrl, true); Key(SdlScancode.A, true); Frame(); Key(SdlScancode.A, false); Frame();
                    Key(SdlScancode.V, true); Frame(); Key(SdlScancode.V, false); Key(SdlScancode.Lctrl, false); Frame();
                    pasted = System.Text.Encoding.UTF8.GetString(input.Buffer, 0, input.Length);
                    Click(empty);
                }
                finally
                {
                    Sdl.PInvoke.SetClipboardText(savedClipboard);
                }
                Console.WriteLine($"     clipboard: ImGui built-in handler before the glue {builtInClipboard}; glue reads {platform.ClipboardReads}, writes {platform.ClipboardWrites}");
                if (OperatingSystem.IsWindows())
                    Check(builtInClipboard, "on Windows ImGui already has an OS clipboard handler (the glue's only matters elsewhere)");
                Check(platform.ClipboardWrites == 1 && platform.ClipboardReads >= 2, "the glue's clipboard functions were the ones called");
                Check(toSdl == "from ImGui 7.35", $"ImGui.SetClipboardText reached SDL's clipboard (got \"{toSdl}\")");
                Check(toImGui == "from SDL", $"ImGui.GetClipboardText read SDL's clipboard (got \"{toImGui}\")");
                Check(pasted == "from SDL", $"Ctrl+A, Ctrl+V pasted into InputText (got \"{pasted}\")");

                // Gamepad navigation with a virtual pad (as in 7.24): D-pad moves, South (Xbox A) activates
                var padName = "RecipeCheck ImGui Pad\0"u8;
                uint padId;
                fixed (byte* pName = padName)
                {
                    var desc = new SdlVirtualJoystickDesc
                    {
                        version = (uint)sizeof(SdlVirtualJoystickDesc),
                        type = (ushort)SdlJoystickType.Gamepad,
                        naxes = (ushort)SdlGamepadAxis.Count,
                        nbuttons = 15,
                        name = pName,
                    };
                    padId = Sdl.Joystick.AttachVirtualJoystick(&desc);
                }
                Check(padId != 0, "virtual pad attached");
                var pad = Sdl.Gamepad.OpenGamepad(padId); // SDL sends button events only for open pads
                try
                {
                    var joy = Sdl.Gamepad.GetGamepadJoystick(pad);
                    io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
                    Frame(); // GamepadAdded reaches the glue
                    var hasGamepad = (io.BackendFlags & ImGuiBackendFlags.HasGamepad) != 0;
                    void PadButton(SdlGamepadButton b)
                    {
                        Sdl.Joystick.SetJoystickVirtualButton(joy, (int)b, true); Frame(); Frame();
                        Sdl.Joystick.SetJoystickVirtualButton(joy, (int)b, false); Frame(); Frame();
                    }
                    Click(new float2(60, 18)); // the title bar: focuses the window without pressing anything
                    var before = clicked;
                    var navStart = navFocus; // ImGui restores the window's last nav item: the text field clicked above
                    PadButton(SdlGamepadButton.DpadUp);
                    var navAfterUp = navFocus;
                    var gridBefore = showGrid;
                    PadButton(SdlGamepadButton.South);
                    var gridToggled = showGrid != gridBefore;
                    PadButton(SdlGamepadButton.DpadUp);
                    PadButton(SdlGamepadButton.DpadUp);
                    var navAtButton = navFocus;
                    PadButton(SdlGamepadButton.South);
                    var padClicks = clicked - before;
                    Sdl.Joystick.SetJoystickVirtualAxis(joy, (int)SdlGamepadAxis.Lefty, -32768); Frame();
                    var stickUp = ImGui.IsKeyDown(ImGuiKey.GamepadLStickUp);
                    Sdl.Joystick.SetJoystickVirtualAxis(joy, (int)SdlGamepadAxis.Lefty, 0); Frame();
                    var stickReleased = !ImGui.IsKeyDown(ImGuiKey.GamepadLStickUp);
                    Console.WriteLine($"     gamepad nav: HasGamepad {hasGamepad}, focus {navStart} -> up {navAfterUp} -> up, up {navAtButton}, checkbox toggled {gridToggled}, button {padClicks}x");
                    Check(hasGamepad, "GamepadAdded set ImGuiBackendFlags.HasGamepad");
                    Check(navStart == "input" && navAfterUp == "checkbox", $"D-pad up moved nav focus from the text field to the checkbox ({navStart} -> {navAfterUp})");
                    Check(gridToggled, "South (Xbox A) toggled the focused checkbox");
                    Check(navAtButton == "button" && padClicks == 1, $"two more D-pad ups reached the button and South pressed it once ({navAtButton}, {padClicks}x)");
                    Check(stickUp && stickReleased, "left stick up past the dead zone holds GamepadLStickUp; centred releases it");
                }
                finally
                {
                    Sdl.Gamepad.CloseGamepad(pad);
                    Sdl.Joystick.DetachVirtualJoystick(padId);
                }

                // T47: BRUTAL's OpenGlBackend never calls ImDrawList.AddCallback callbacks (its loop has no UserCallback branch)
                var callbackCalls = 0;
                ImDrawCallback callback = (list, cmd) => callbackCalls++;
                pendingCallback = callback;
                Frame();
                pendingCallback = null;
                GC.KeepAlive(callback);
                Check(callbackCalls == 0, "gap (T47): OpenGlBackend skipped the ImDrawCallback (a fix upstream fails this check)");
            }
            finally
            {
                renderer.Dispose();
                platform.Dispose();
                ImGui.DestroyContext();
            }
        }
        finally
        {
            if (textMemory != nint.Zero)
                Marshal.FreeCoTaskMem(textMemory);
            if (context != 0)
                Sdl.Video.GLDestroyContext(context);
            if (made)
                Sdl.Video.DestroyWindow(window);
            Sdl.Init.Quit();
        }
    }
}
