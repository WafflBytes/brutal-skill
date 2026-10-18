# SDL3 (+ Image, Mixer, Ttf)

Read this for SDL windows, the 2D renderer, input (keyboard, mouse, gamepad), SDL audio streams, screenshots, or the SDL satellite libraries. For OpenGL inside an SDL window also read `opengl.md`.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Sdl | `Brutal.SdlApi` | SDL3.dll 3.4.12 | partly | Gen+Manual | Ergonomic `Sdl.Video/Render/Events/...` + raw `Sdl.PInvoke.*` (1195 fns) |
| Sdl.Image/Mixer/Ttf | `Brutal.SdlApi.Image/.Mixer/.Ttf` | SDL3_image/mixer/ttf | yes | Gen | No WEBP/AVIF/JXL/TIFF. Ttf bundles FreeType 2.13.2 + HarfBuzz. Mixer renders without a device (`MIX_Generate`) |


## Name translation

**SDL3**
- Ergonomic groups follow the SDL headers: `Sdl.Init/Video/Events/Keyboard/Mouse/Gamepad/Joystick/Log/Render/Vulkan`.
- Raw 1:1 layer: `Sdl.PInvoke.<NameWithoutPrefix>`. Audio, GPU, camera, filesystem, threads and timers are raw only.
- Enums: `SDL_EVENT_KEY_DOWN`→`SdlEventType.KeyDown`, `SDLK_ESCAPE`→`SdlKeycode.Escape`, `SDLK_1`→`SdlKeycode.Num1`, `SDL_PIXELFORMAT_ARGB8888`→`SdlPixelFormat.Argb8888`.
- Struct and union fields stay **lowercase**: `ev.type`, `ev.key.key`, `ev.key.scancode`, `ev.wheel.y`, `SdlFRect{x,y,w,h}`.
- Keycodes and scancodes that start with a digit use `Num`/`N`: `SdlKeycode.Num1`, but `SdlScancode.N1` (not `_1`).
- Synthetic events you push have `windowID == 0`, real ones carry the window's id. In a scripted test, ignore real focus events (`ev.window.windowID != 0`) so another window on the desktop can't interfere.
- All SDL structs are in one file, `BRUTAL/Brutal.Sdl/Generated/Sdl.Structs.gen.cs` (not one file per struct as for Vulkan). Common ones: `SdlVertex { position: SdlFPoint, color: SdlFColor, tex_coord: SdlFPoint }`, `SdlFPoint { x, y }`, `SdlFColor { r, g, b, a }` (floats 0..1), `SdlColor { r, g, b, a }` (bytes), `SdlKeyboardEvent { type, windowID, scancode, key, mod, down, repeat }` (`down`/`repeat` are bytes).
- Handle aliases exist: `window.Show()` is `Sdl.Video.ShowWindow(window)`.
- **Satellite libraries** have their own static classes, not `Sdl.*`: `SdlImage.*`, `SdlTtf.*`, `SdlMixer.*` (raw layer: `SdlImage.PInvoke.*` and so on). Their ergonomic names **drop the C underscores**: `IMG_Load_IO` → `SdlImage.LoadIO`, `IMG_isWEBP` → `SdlImage.IsWEBP`, `TTF_RenderText_Blended` → `SdlTtf.RenderTextBlended`, `MIX_LoadAudio` → `SdlMixer.LoadAudio`. Their creators throw `SdlException` instead of returning null.

## Key source locations

| What | File:line |
|---|---|
| SDL renderer | `Brutal.Sdl/Generated/Libraries/Sdl.Render.gen.cs` (`CreateRenderer:112`, `CreateTexture:434`, `RenderTexture:2449`) |
| SDL event pump | `Brutal.Sdl/Manual/Libraries/Sdl.Events.cs` |

## Recipes

### 7.9 SDL3 renderer + pixel buffer [RUN]
```csharp
Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events);
var window = Sdl.Video.CreateWindow("Game", 1280, 720, SdlWindowFlags.Resizable);
var renderer = Sdl.Render.CreateRenderer(window, null!);   // null = SDL picks (direct3d11 on Windows)
var vsync = Sdl.Render.SetRenderVSync(renderer, 1);        // bool; T15
Sdl.Render.SetRenderLogicalPresentation(renderer, 640, 360, SdlRendererLogicalPresentation.Letterbox);
var tex = Sdl.Render.CreateTexture(renderer, SdlPixelFormat.Argb8888, SdlTextureAccess.Streaming, 320, 180);
Sdl.Render.SetTextureScaleMode(tex, SdlScaleMode.Nearest);
var pixels = new uint[320 * 180];                           // 0xAARRGGBB
for (var running = true; running;)
{
    while (Sdl.Events.PollEvent(out var ev))
    {
        if (ev.type == SdlEventType.Quit || (ev.type == SdlEventType.KeyDown && ev.key.key == SdlKeycode.Escape)) running = false;
        // SdlEventType.MouseWheel -> ev.wheel.y
    }
    var buttons = Sdl.Mouse.GetMouseState(out var wx, out var wy);                     // window coords
    Sdl.Render.RenderCoordinatesFromWindow(renderer, wx, wy, out var lx, out var ly); // logical coords
    unsafe
    {
        fixed (uint* p = pixels) Sdl.Render.UpdateTexture(tex, null, p, 320 * sizeof(uint));
        Sdl.Render.SetRenderDrawColor(renderer, 0, 0, 0, 255);
        Sdl.Render.RenderClear(renderer);
        var dst = new SdlFRect { x = 0, y = 0, w = 640, h = 360 };
        Sdl.Render.RenderTexture(renderer, tex, null, &dst);
        Sdl.Render.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        Sdl.Render.RenderDebugText(renderer, 4, 4, "HUD");   // built-in 8x8 font
        Sdl.Render.RenderPresent(renderer);
    }
}
Sdl.Render.DestroyTexture(tex); Sdl.Render.DestroyRenderer(renderer); Sdl.Video.DestroyWindow(window); Sdl.Init.Quit();
```
- **Blending:** `SetTextureBlendMode(tex, 1)` / `SetRenderDrawBlendMode(r, 1)` (1 = `SDL_BLENDMODE_BLEND`).
- **RGBA bytes:** use `SdlPixelFormat.Abgr8888` (same as `Rgba32`).
- **Screenshot** (before `RenderPresent`):
  ```csharp
  var s = Sdl.PInvoke.RenderReadPixels(renderer, null);
  var rgba = Sdl.PInvoke.ConvertSurface(s, SdlPixelFormat.Abgr8888);
  Stb.WritePng(path, rgba->w, rgba->h, 4, new ReadOnlySpan<byte>(rgba->pixels, rgba->pitch * rgba->h), rgba->pitch);
  ```
  Then `Sdl.PInvoke.DestroySurface` both surfaces.

### 7.16 SDL3 vector drawing, keyboard state, procedural audio [RUN]
Batched coloured triangles (no texture), the 8x8 debug font scaled up, polled keyboard state, and a push-mode audio stream fed from the main thread.
```csharp
Sdl.Render.SetRenderDrawBlendMode(renderer, 1);       // also the blend mode for untextured RenderGeometry
var verts = new SdlVertex[n];                          // position (SdlFPoint), color (SdlFColor, 0..1 floats), tex_coord
fixed (SdlVertex* p = verts)
    Sdl.Render.RenderGeometry(renderer, default, p, count, null, 0); // default(SdlTexture) = no texture; null indices = plain triangle list
Sdl.Render.SetRenderDrawColor(renderer, 255, 255, 255, 255);        // debug text uses the draw colour
Sdl.Render.SetRenderScale(renderer, 3, 3);                           // 24 px glyphs; composes with logical presentation
Sdl.Render.RenderDebugText(renderer, x / 3, y / 3, "SPEED");
Sdl.Render.SetRenderScale(renderer, 1, 1);

byte* keys = Sdl.PInvoke.GetKeyboardState(null);   // raw: no allocation (the ergonomic Sdl.Keyboard.GetKeyboardState copies to a new bool[])
var throttle = keys[(int)SdlScancode.W] != 0;

if (Sdl.Init.InitSubSystem(SdlInitFlags.Audio))     // separate from the main Init, so a missing device can't block startup
{
    var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 1, freq = 44100 };
    var stream = Sdl.PInvoke.OpenAudioDeviceStream(0xFFFFFFFFu, &spec, null, null); // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, no callback
    if (!stream.IsNull()) Sdl.PInvoke.ResumeAudioStreamDevice(stream);               // device streams start paused
    // each frame: top the queue up to ~70 ms
    var queued = Sdl.PInvoke.GetAudioStreamQueued(stream) / sizeof(float);
    fixed (float* p = samples) Sdl.PInvoke.PutAudioStreamData(stream, p, want * sizeof(float));
    Sdl.PInvoke.DestroyAudioStream(stream);        // also closes the device it opened
}
```
- `SdlEvent` unions used: `ev.gdevice.which` (GamepadAdded/Removed), `ev.gbutton.button` (byte, cast to `SdlGamepadButton`), `ev.key.repeat` (a `byte`, not a `bool`: test `ev.key.repeat == 0`) [RUN].
- `Sdl.Gamepad.OpenGamepad(id)` throws `SdlException` on failure; `GetGamepadAxis` returns `short` (triggers 0..32767). Already-connected pads arrive as `GamepadAdded` events right after init.
- Rumble: `Sdl.Gamepad.RumbleGamepad(pad, low, high, ms)` (ushort strengths); re-send each frame with a ~120 ms duration so it stops by itself. `RumbleGamepadTriggers` returns `false` when the connection has no trigger motors (T34).

- **Synthetic input for tests** [RUN]: `var ev = new SdlEvent(); ev.key.type = SdlEventType.KeyDown; ev.key.key = SdlKeycode.Right; ev.key.scancode = SdlScancode.Right; ev.key.down = 1; Sdl.Events.PushEvent(&ev);` (same for `KeyUp`, and `ev.window.type = SdlEventType.WindowFocusLost`). Pushed events go through `PollEvent` like real ones but do **not** change `GetKeyboardState`, so track held keys from KeyDown/KeyUp events if a test needs to drive movement. The template's `--smoke` does this.
- **Focus loss:** on `SdlEventType.WindowFocusLost`, clear held keys (their KeyUp goes to the other window) and pause.
- **Dear ImGui in an SDL game:** no shipped backend, but a small event glue plus `OpenGlBackend` (OpenGL window, 7.35) or a `SDL_RenderGeometry` renderer (plain SDL renderer, 7.36) works; both are [RUN] in `glfw-imgui.md`.

### 7.18 SDL_image: load PNG/JPG from memory or file, save, textures [RUN]
Namespace `Brutal.SdlApi.Image`. No SDL video init needed. Codecs: PNG, JPG, BMP, GIF, QOI, TGA and other dependency-free formats; **not** WEBP, AVIF, JXL or TIFF (T37).
```csharp
SdlSurface* img;
fixed (byte* p = pngBytes)                                    // or embedded-resource bytes
    img = SdlImage.LoadIO(Sdl.PInvoke.IOFromConstMem(p, (nuint)pngBytes.Length), closeio: true); // IMG_Load_IO
var rgba = Sdl.PInvoke.ConvertSurface(img, SdlPixelFormat.Abgr8888); // loaded format varies; convert for RGBA bytes
var fromFile = SdlImage.Load("Assets/hero.png");             // throws SdlException on failure (not T1's silent null)
SdlImage.SavePNG(fromFile, "out.png"); SdlImage.SaveJPG(fromFile, "out.jpg", 90);
var tex = SdlImage.LoadTexture(renderer, "Assets/hero.png"); // straight to an SdlTexture
// Free: Sdl.PInvoke.DestroySurface(surface) for each surface, Sdl.Render.DestroyTexture(tex).
```

### 7.19 SDL_ttf: TrueType text [RUN]
Namespace `Brutal.SdlApi.Ttf`. FreeType + HarfBuzz are built in. No font ships with the package (T39).
```csharp
SdlTtf.Init();                                                 // throws SdlException on failure
var font = SdlTtf.OpenFont("Assets/MyFont.ttf", 24);           // throws if missing
int w, h; SdlTtf.GetStringSize(font, "Hello", 0, &w, &h);     // length 0 = whole string

// In a game loop: the renderer text engine (cached glyph atlas), one TtfText per label.
var engine = SdlTtf.CreateRendererTextEngine(renderer);
var text = SdlTtf.CreateText(engine, font, "Score 0", 0);
SdlTtf.SetTextColor(text, 255, 220, 80, 255);
SdlTtf.SetTextString(text, $"Score {score}", 0);              // update in place each frame
SdlTtf.DrawRendererText(text, x, y);

// Font from embedded bytes [RUN]: SDL_ttf reads the font lazily for as long as it's open, so copy the bytes to
// native memory that outlives CloseFont (a `fixed` block that ends would leave it reading freed memory) [SRC].
var io = Sdl.PInvoke.IOFromConstMem(nativeFontBytes, (nuint)length);
var embedded = SdlTtf.OpenFontIO(io, closeio: true, 24);

// Sharp text at any window size [RUN]: open the font at size × (output height / logical height), draw with
// SetRenderScale(1/scale) so the logical-presentation scale cancels out, and on resize call
// SdlTtf.SetFontSize(font, newSize). That updates existing TtfText objects, but it clears the font's glyph cache,
// so keep one TtfFont per size rather than switching sizes per draw. See the template's Platform/TextRenderer.cs.

// One-off text to a surface (TTF_RenderText_Blended): e.g. to bake into a texture or a GL atlas.
var s = SdlTtf.RenderTextBlended(font, "Hello", 0, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
// Teardown order: DestroyText -> DestroyRendererTextEngine -> CloseFont -> (renderer) -> SdlTtf.Quit
```
- Also verified on an SDL software renderer (`Sdl.Render.CreateSoftwareRenderer(surface)`), so text appears in headless `--snapshot` runs.

### 7.20 SDL_mixer: sounds and music [RUN]
Namespace `Brutal.SdlApi.Mixer`. SDL_mixer **3** object model (`MixMixer`/`MixTrack`/`MixAudio`), not the SDL2 `Mix_PlayChannel` API. Decodes WAV, OGG, FLAC and MP3 with no extra DLLs.
```csharp
SdlMixer.Init();
// In a game: a mixer on the default playback device. Needs no SDL_Init(Audio). Throws if there's no device.
var mixer = SdlMixer.CreateMixerDevice(0xFFFFFFFFu, null);  // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, device's format
// Headless/tests: an offline mixer you pull audio from with Generate:
//   var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 2, freq = 48000 };
//   var offline = SdlMixer.CreateMixer(&spec);

var sfx = SdlMixer.LoadAudio(mixer, "Assets/jump.wav", predecode: true); // resampled to the mixer format
fixed (byte* p = wavBytes)                            // embedded asset [RUN]: predecode reads it all now,
    sfx = SdlMixer.LoadAudioIO(mixer, Sdl.PInvoke.IOFromConstMem(p, (nuint)wavBytes.Length), predecode: true, closeio: true); // so a fixed block is enough
SdlMixer.PlayAudio(mixer, sfx);                       // fire-and-forget one-shot
var music = SdlMixer.CreateTrack(mixer);              // a track = one controllable voice
SdlMixer.SetTrackAudio(music, SdlMixer.LoadAudio(mixer, "Assets/theme.ogg", predecode: false));
SdlMixer.PlayTrack(music, 0);                         // 0 = no options
SdlMixer.SetTrackLoops(music, -1);                    // -1 = loop forever; must come AFTER PlayTrack (T40)
SdlMixer.SetTrackGain(music, 0.5f);
bool playing = SdlMixer.TrackPlaying(music);

// Raw PCM (procedural audio): LoadRawAudio copies the data, so the source buffer can be freed right away.
var raw = SdlMixer.LoadRawAudio(mixer, pcmPtr, byteCount, &monoF32Spec);

fixed (float* p = buf) SdlMixer.Generate(offline, p, buf.Length * sizeof(float)); // offline mixer only (T38)
// GetAudioDuration(audio) is in the audio's source sample frames, not ms.
// Teardown: DestroyTrack, DestroyAudio, DestroyMixer, SdlMixer.Quit.
```
- A working setup is in the `brutal-sdl` template: `Platform/Audio.cs` (device mixer, falls back to silence when `CreateMixerDevice` throws, an offline mixer for `--selftest`), `Platform/TextRenderer.cs` and `Platform/Resources.cs`.
- Callbacks run on SDL_mixer's audio thread. Exceptions thrown in them are parked and rethrown by the next ergonomic mixer call; during long device playback call `SdlMixer.ThrowPendingCallbackExceptions()` once per frame [SRC].

### 7.24 Gamepads, with a virtual pad for tests [RUN]
```csharp
Sdl.Init.Initialize(SdlInitFlags.Gamepad | SdlInitFlags.Events);   // Gamepad implies Joystick
// Pads arrive as events, including ones already plugged in at start-up:
//   SdlEventType.GamepadAdded   -> var pad = Sdl.Gamepad.OpenGamepad(ev.gdevice.which);   // throws SdlException
//   SdlEventType.GamepadRemoved -> Sdl.Gamepad.CloseGamepad(pad)
short lx = Sdl.Gamepad.GetGamepadAxis(pad, SdlGamepadAxis.Leftx);          // -32768..32767; triggers are 0..32767
bool a = Sdl.Gamepad.GetGamepadButton(pad, SdlGamepadButton.South);         // South = A on Xbox, Cross on PlayStation
// Events: SdlEventType.GamepadButtonDown with ev.gbutton.which and .button (a byte; cast to SdlGamepadButton)
Sdl.Gamepad.RumbleGamepad(pad, low, high, 120);                             // ushort strengths, duration in ms
```
**Testing without hardware:** a virtual gamepad goes through the same API and events.
```csharp
var desc = new SdlVirtualJoystickDesc {
    version = (uint)sizeof(SdlVirtualJoystickDesc),     // SDL_INIT_INTERFACE: must be the struct size
    type = (ushort)SdlJoystickType.Gamepad,             // gets a standard mapping automatically
    naxes = (ushort)SdlGamepadAxis.Count, nbuttons = 15, name = pName,
    Rumble = &OnRumble };                               // [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] static byte OnRumble(void*, ushort, ushort)
var id = Sdl.Joystick.AttachVirtualJoystick(&desc);
var joy = Sdl.Gamepad.GetGamepadJoystick(pad);          // borrowed: don't close it (T17)
Sdl.Joystick.SetJoystickVirtualAxis(joy, (int)SdlGamepadAxis.Leftx, -32768);
Sdl.Joystick.SetJoystickVirtualButton(joy, (int)SdlGamepadButton.South, true);
Sdl.Gamepad.UpdateGamepads();                           // or PollEvent; then read the pad as above
Sdl.Joystick.DetachVirtualJoystick(id);                 // -> GamepadRemoved
```
Verified: hot-plug add and remove events, axes, buttons, a button event, and `RumbleGamepad` reaching the device's callback.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T11 | Raw-layer-only functions: `Glfw.PInvoke.GetVersionString`, `Sdl.PInvoke.RenderReadPixels/ConvertSurface/DestroySurface`, SDL `Get*s(int*)` enumerations, SDL audio/GPU/camera/filesystem | Search `Sdl.PInvoke` / `Glfw.PInvoke` |
| T15 | **Vsync over Remote Desktop:** SDL D3D11 `RenderPresent` blocks ~150–170 ms, so the game runs at ~6 FPS | Average present time over 30 frames; if over 25 ms, `SetRenderVSync(r, 0)` and use your own 60 FPS limiter |
| T33 | With vsync on, the loop runs at the **monitor's** refresh rate (often 120–240 Hz), not 60 Hz. Scripts, timers or physics counted in frames run 2-3x too fast, and early inputs land before screens accept them | Fixed-step accumulator for simulation; drive scripted tests by game time or state, never by frame count |
| T37 | SDL_image has no WEBP, AVIF, JXL or TIFF codec. Detection is compiled out too: `SdlImage.IsWEBP` returns false even for a real WEBP, and loading throws `SdlException: Unsupported image format`, so the failure shows up only when that asset loads | Convert such assets to PNG, JPG or QOI at build time; load every asset once in `--selftest` |
| T38 | `SdlMixer.Generate` always fills the whole buffer but returns only the bytes of real audio before trailing silence; it can come back slightly short even while a track is mid-clip | Treat any value ≥ 0 as success. Use `TrackPlaying` to know when a clip ended |
| T39 | No font ships with Sdl.Ttf, and system fonts (Arial, Segoe UI) generally can't be redistributed | Bundle a font whose licence allows redistribution (e.g. SIL Open Font License) and ship its licence file. Tests can use `BRUTAL_TEST_FONT` or a system font |
| T40 | `SdlMixer.PlayTrack(track, 0)` resets the track's loop count from its options (default 0), so `SetTrackLoops` called **before** it is silently lost and music plays once | Call `SetTrackLoops(track, -1)` **after** `PlayTrack` (or pass loops in the play options) |
| T34 | Observed with an Xbox Series X controller: `RumbleGamepadTriggers` returns `false` (no impulse triggers), even with a focused window; `RumbleGamepad` works. Trigger motors depend on the connection path (Bluetooth / XInput lack them) | Check the bool once and fall back to the body motors |
