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

/// <summary>Feeds SDL3 events and per-frame window state to Dear ImGui. Call ProcessEvent for every SDL event, NewFrame once per frame.</summary>
sealed class SdlImGuiGlue : IPlatformBackend
{
    readonly SdlWindow _window;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;

    public SdlImGuiGlue(SdlWindow window) => _window = window;

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
        }
    }

    public void Dispose() { }

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

        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events);
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
                void Key(SdlScancode sc, bool down)
                {
                    var e = new SdlEvent();
                    e.key.type = down ? SdlEventType.KeyDown : SdlEventType.KeyUp;
                    e.key.scancode = sc;
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
                    buttonC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    ImGui.SliderFloat("Volume"u8, ref volume, 0, 1);
                    sliderMin = ImGui.GetItemRectMin();
                    sliderMax = ImGui.GetItemRectMax();
                    ImGui.Checkbox("Show grid"u8, ref showGrid);
                    checkC = ImGui.GetItemRectMin() + new float2(8, 8);
                    ImGui.InputText("Name"u8, input);
                    inputC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
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
