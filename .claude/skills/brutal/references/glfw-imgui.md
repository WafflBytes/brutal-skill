# GLFW, ImGui, ImPlot, RenderDoc

Read this for GLFW windows and input, Dear ImGui / ImPlot debug UI, or RenderDoc capture. BRUTAL ships only a GLFW platform backend for ImGui (T22); SDL games use a small hand-written glue (7.35, and 7.36 for the SDL renderer).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Glfw | `Brutal.GlfwApi` | glfw3.dll, **RocketWerkz fork of 3.5.1** (T9) | `Null` platform | Gen+Manual | Window events are C# events |
| ImGui (+Abstractions/Backends/Extensions/FreeType), ImPlot | `Brutal.ImGuiApi*`, `Brutal.ImPlotApi` | imgui, imgui_freetype, implot | — | Gen (ImGui 1.92.2 docking, ImPlot 0.17) | T22 |
| RenderDoc (+ImGui) | `Brutal.RenderDocApi` | installed RenderDoc | — | Gen+Manual | `RenderDoc.Initialize(new(args))` attaches when run with `--RenderDoc` |


## Name translation

**GLFW**
- `Glfw.Init()` returns bool and doesn't throw.
- `glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API)` → `Glfw.WindowHint(GlfwWindowHint.ClientApi, 0)`.
- `glfwInitHint` → `Glfw.SetInitHint(GlfwInitHint.X, v)`.
- Callbacks are window events (`OnKey`, `OnClose`, `OnCursorPos`, `OnMouseButton`, `OnScroll`, `OnFramebufferSize`, `OnDrop`). Exceptions in them are rethrown from `Glfw.PollEvents()`.
- Raw layer: `Glfw.PInvoke.*`.
- Ergonomic wrappers live in both `Manual/` and `Generated/Libraries/` (for example `Glfw.GetRequiredInstanceExtensions` and `Glfw.WaitEventsTimeout` are generated). Search both before concluding a function only exists in the raw layer.

**ImGui:** `ImGui.Begin(ImString name, ref bool open, flags)`. Pointer types become `*Ptr` structs (`ImDrawDataPtr`).

## Key source locations

| What | File:line |
|---|---|
| GLFW init, create window | `Brutal.Glfw/Manual/Libraries/Glfw.Initialization.cs:186`, `Glfw.Window.cs:241` |
| ImGui backends | `Brutal.ImGui.Backends/PlatformBackends/GlfwBackend.cs:66`, `RendererBackends/OpenGlBackend.cs:20`, `VulkanBackend.cs:21` |

## Recipes

### 7.8 GLFW [RUN]
```csharp
if (!Glfw.Init()) throw new Exception("GLFW init failed");
Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);  // no API (Vulkan or input-only)
Glfw.WindowHint(GlfwWindowHint.Visible, 0);    // optional: hidden, for tests
var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Game", Size = new int2(1280, 720) });
while (!window.ShouldClose) Glfw.PollEvents();
window.Dispose(); Glfw.Terminate();
```
- `Glfw.PInvoke.GetVersionString()` → `"3.5.1 Win32 WGL Null EGL OSMesa VisualC DLL"`; `Glfw.VulkanSupported()`.
- For CI with no display, call `Glfw.SetInitHint(GlfwInitHint.Platform, (int)GlfwPlatform.Null)` before `Init` [RUN]. Window creation and `PollEvents` work with it (`RecipeCheck -- 7.8 --no-window`).

### 7.21 Dear ImGui debug UI on GLFW + OpenGL [RUN]
Packages `Ahwoo.BRUTAL.ImGui`, `.ImGui.Backends`, `.OpenGl` and `.Glfw`. Namespaces `Brutal.ImGuiApi`, `Brutal.ImGuiApi.Backends`, `Brutal.OpenGlApi`, `Brutal.GlfwApi`. Verified with a hidden window, 4.1 core context, both BRUTAL backends, widgets, and a synthetic click. `RecipeCheck -- 7.21`; set `RECIPECHECK_SHOTS=<dir>` to save the frame as a PNG.
```csharp
static GlGetProcAddressFunc? s_glLoader;                        // keep rooted: GLAD keeps the pointer (T35)

Glfw.Init();
Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 4);        // the OpenGL backend needs 4.1+ core (T22)
Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 1);
Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Tool", Size = new int2(1280, 720) });
Glfw.PInvoke.MakeContextCurrent(window);
Glfw.PInvoke.SwapInterval(1);
s_glLoader = name => Glfw.PInvoke.GetProcAddress(name);        // GlGetProcAddressFunc takes byte*
Glad.Initialize(s_glLoader);

ImGui.CreateContext();
var io = ImGui.GetIO();
io.IniFilename = default;                                       // otherwise imgui.ini is written to the working directory
ImGui.StyleColorsDark();
var platform = new GlfwBackend(window, GlfwBackend.ClientApi.OpenGL); // hooks the window's input events
var renderer = new OpenGlBackend();                             // needs the GL context current

while (!window.ShouldClose)
{
    Glfw.PollEvents();
    renderer.NewFrame(); platform.NewFrame(); ImGui.NewFrame(); // this order
    ImGui.Begin("Debug"u8);                                     // u8 labels (T14); $"..." interpolation also works
    ImGui.Text($"FPS {io.Framerate:F0}");
    if (ImGui.Button("Reset"u8, new float2(120, 30))) { /* ... */ }
    ImGui.SliderFloat("Volume"u8, ref volume, 0, 1);
    ImGui.Checkbox("Show grid"u8, ref showGrid);
    ImGui.End();
    ImGui.Render();
    var fb = window.FramebufferSize;
    OpenGl.Native.glViewport(0, 0, fb.X, fb.Y);
    OpenGl.Native.glClear(OpenGl.GL_COLOR_BUFFER_BIT);         // draw your game here, then the UI on top
    renderer.RenderDrawData(ImGui.GetDrawData());
    Glfw.PInvoke.SwapBuffers(window);
}
renderer.Dispose(); platform.Dispose(); ImGui.DestroyContext(); window.Dispose(); Glfw.Terminate(); // this order
```
- `ImGui.Begin/Button/Checkbox/SliderFloat` take `ref` for in/out values and return `bool`. Pointer types are `*Ptr` structs (`ImGuiIOPtr`, `ImDrawDataPtr`), and their fields are `ref` properties.
- **Synthetic input for tests** [RUN]: call `io.AddMousePosEvent(x, y)` / `io.AddMouseButtonEvent(0, down)` **after** `platform.NewFrame()` and before `ImGui.NewFrame()`, so the backend's own update can't overwrite it. A click takes two frames: down, then up.
- **SDL games:** BRUTAL has no SDL platform backend (T22), but a hand-written one works: see 7.35. With no OpenGL at all use the `SDL_RenderGeometry` renderer of 7.36. A separate GLFW tool window or SDL_ttf debug text (`sdl.md` 7.19) are the alternatives.

### 7.35 Dear ImGui on an SDL3 window + OpenGL 4.1 [RUN]
`IPlatformBackend` is only `NewFrame()` + `Dispose()`, and `OpenGlBackend` (the renderer) has no GLFW dependency: it needs a current GL 4.1+ core context with GLAD loaded, and it uploads ImGui 1.92's font atlas itself. So an SDL game can use ImGui with a small hand-written platform glue. The full class (`SdlImGuiGlue`, about 150 lines including the key table) is in `Sandbox/RecipeCheck/Recipes.ImGuiSdl.cs`; `RecipeCheck -- 7.35` runs it in a hidden SDL window (`RECIPECHECK_SHOTS=<dir>` saves the frame). The window and context are created exactly as in `opengl.md` 7.17 (`SdlWindowFlags.Opengl`, `GLSetAttribute` 4.1 core, `GLCreateContext`, `Glad.Initialize`).
```csharp
var platform = new SdlImGuiGlue(window);      // yours
var renderer = new OpenGlBackend();           // BRUTAL's
// each frame:
while (Sdl.Events.PollEvent(out var ev)) { platform.ProcessEvent(in ev); /* then your own handling */ }
renderer.NewFrame(); platform.NewFrame(); ImGui.NewFrame();
/* widgets */
ImGui.Render(); renderer.RenderDrawData(ImGui.GetDrawData()); Sdl.Video.GLSwapWindow(window);
```
What the glue does (all [RUN]):
- `NewFrame`: `io.DisplaySize` = `GetWindowSize`, `io.DisplayFramebufferScale` = pixel size / window size, `io.DeltaTime` from a stopwatch (must be > 0).
- Mouse: `MouseMotion` -> `AddMousePosEvent(x, y)`; `MouseButtonDown/Up` -> `AddMouseButtonEvent`, **SDL buttons 1/2/3 are left/middle/right but ImGui's 0/1/2 are left/right/middle** (swap 2 and 3); `MouseWheel` -> `AddMouseWheelEvent(x, y)`; `WindowMouseLeave` -> `AddMousePosEvent(-float.MaxValue, -float.MaxValue)`.
- Keys: `AddKeyEvent(MapKey(ev.key.scancode), down)`. The table maps `SdlScancode` to `ImGuiKey` (`A`..`Z` and `F1`..`F12` are contiguous in both enums, `N1`..`N9` -> `_1`..`_9`, `N0` -> `_0`, `Return` -> `Enter`, `Pageup` -> `PageUp`, `Equals` -> `Equal`, `Grave` -> `GraveAccent`, `Lctrl`/`Lshift`/`Lalt`/`Lgui` -> `LeftCtrl`...`LeftSuper`, `Kp*`/`KpPlus`... -> `Keypad*`). **Modifiers must be sent separately (T49):** before each key event, `io.AddKeyEvent(ImGuiKey.Mod_Ctrl, (ev.key.mod & Sdl.Constants.KmodCtrl) != 0)`, and the same for `Mod_Shift` (`KmodShift`), `Mod_Alt` (`KmodAlt`), `Mod_Super` (`KmodGui`). Without them Ctrl+A/C/V/Z do nothing. Scancode-based means shortcuts follow key position, not layout.
- Text: `TextInput` -> `AddInputCharacter` per rune of the UTF-8 `ev.text.text`. SDL3 sends text events only while text input is started on the window (`Sdl.Keyboard.StartTextInput(window)` / `StopTextInput`) [SRC]; the recipe feeds the text event directly, so whether real key presses produce `TextInput` without that call was not run. Start it while `io.WantTextInput` is true.
- Focus: `WindowFocusGained/Lost` -> `AddFocusEvent`.
- Clipboard: in the constructor set `ImGui.GetPlatformIO().Platform_GetClipboardTextFn` / `Platform_SetClipboardTextFn` to `Marshal.GetFunctionPointerForDelegate(...)` of `ImGuiPlatformIO.GetClipboardTextFnDelegate` / `SetClipboardTextFnDelegate` fields (keep the delegates rooted, T35). Get: `Sdl.PInvoke.GetClipboardText()` returns memory SDL allocated; hand it to ImGui and `Sdl.PInvoke.Free` it on the next call and in `Dispose`. Set: `Sdl.PInvoke.SetClipboardText(Marshal.PtrToStringUTF8((nint)text))`. Reset both to 0 in `Dispose`. **On Windows ImGui already uses the OS clipboard without this** (its Win32 default is non-null before the glue sets anything); the glue's version matters on Linux and macOS, where ImGui's fallback is a clipboard inside the process.
- Cursor shapes: set `io.BackendFlags |= HasMouseCursors`; in `NewFrame` read `ImGui.GetMouseCursor()` (the shape the last frame asked for) and, only when it changed, `Sdl.Mouse.SetCursor(cursor)` + `ShowCursor()`, or `HideCursor()` for `None` / `io.MouseDrawCursor`. Create the cursors lazily with `Sdl.Mouse.CreateSystemCursor` (needs the video subsystem; throws `SdlException`): `TextInput` -> `Text`, `ResizeAll` -> `Move`, `ResizeNS/EW/NESW/NWSE` -> `NsResize`/`EwResize`/`NeswResize`/`NwseResize`, `Hand` -> `Pointer`, `Wait`, `Progress`, `NotAllowed`, the rest `Default`. `DestroyCursor` them in `Dispose`. Skip it all when `io.ConfigFlags` has `NoMouseCursorChange`.
- Gamepad navigation: `GamepadAdded` -> `io.BackendFlags |= HasGamepad` (clear it on `GamepadRemoved` when `!Sdl.Gamepad.HasGamepad()`); the game sets `io.ConfigFlags |= NavEnableGamepad` and opens the pad (`OpenGamepad`; SDL sends button events only for open pads). `GamepadButtonDown/Up` -> `AddKeyEvent`: `South` -> `GamepadFaceDown` (activate), `East` -> `FaceRight` (cancel), `West` -> `FaceLeft`, `North` -> `FaceUp`, D-pad -> `GamepadDpad*`, shoulders -> `L1`/`R1`, stick clicks -> `L3`/`R3`, `Start`, `Back`. `GamepadAxisMotion` -> `AddKeyAnalogEvent` for both directions of a stick axis (dead zone 8000 of 32767, held above 0.1) and for the triggers (`L2`/`R2`, 0..32767).
- Game input: after the glue, skip your own handling when `io.WantCaptureMouse` (mouse events) or `io.WantCaptureKeyboard` (key and text events). Verified: true over a window and in an active text field, false over empty space and after clicking away.
Checked by running synthetic SDL events (pushed with `Sdl.Events.PushEvent`) through the glue: a click made `Button` return true once, a click toggled a checkbox, a drag moved a slider, `"hi"` + Backspace left `"h"` in an `InputText`, and the framebuffer read back with 12,994 lit pixels (the frame also looked right). Also run: hovering the `InputText` made SDL's current cursor the I-beam and empty space the arrow; text went from `ImGui.SetClipboardText` to SDL's clipboard and back, and Ctrl+A, Ctrl+V pasted it into the field (the recipe restores your clipboard); with a virtual pad (7.24) D-pad up moved nav focus from the text field (ImGui restores a window's last nav item) to the checkbox, South toggled it, two more ups reached the button and South pressed it once, and the left stick past the dead zone held `GamepadLStickUp`. Each of the clipboard, cursor, modifier and gamepad checks was seen to fail with its code removed. The recipe sets `Sdl.Constants.HintJoystickAllowBackgroundEvents` to `"1"` because its window is hidden (T48). `OpenGlBackend`'s constructor sets `RendererHasViewports` and calls `InitMultiViewportSupport`: harmless with `ViewportsEnable` off (the default; viewports would need a platform glue that creates windows, which this one doesn't).
- **Not run:** the clipboard on Linux or macOS (where the glue's version is the one that matters), multi-viewports, high-DPI (the scale maths is the same as GLFW's but only ran at 1.0), an IME, a real gamepad, and a real (visible, focused) window with real input.
- **BRUTAL's `OpenGlBackend` never calls draw callbacks (T47):** the recipe adds one with `ImGui.GetWindowDrawList().AddCallback(cb, 42)` and asserts it is not called, so an upstream fix fails it. Use 7.36's pattern if you need callbacks.
- Text pushed with `PushEvent` carries a raw `byte*` that SDL does not own for you; the recipe calls `ProcessEvent` directly for the synthetic text event. Real SDL text events are fine.

### 7.36 Dear ImGui renderer on `SDL_RenderGeometry` (SDL games without OpenGL) [RUN]
For a game on the plain SDL renderer (the `brutal-sdl` template) there is no BRUTAL ImGui renderer (they are OpenGL and Vulkan only), but ImGui's output maps directly onto `Sdl.Render.RenderGeometryRaw`. The full class (`SdlRenderGeometryBackend : IRendererBackend`, about 120 lines) is in `Sandbox/RecipeCheck/Recipes.ImGuiSdlRender.cs`; it uses the same `SdlImGuiGlue` for input as 7.35. `RecipeCheck -- 7.36` runs it headless (software renderer on a surface; no window or GPU).
- Constructor: set `io.BackendFlags |= RendererHasVtxOffset | RendererHasTextures`. Without the second flag ImGui 1.92 expects you to build the font atlas yourself.
- Textures: for every `drawData.Textures[i]` whose `Status != OK` (`using Brutal;` for `IsNotNull`): `WantCreate` -> `Sdl.Render.CreateTexture(r, SdlPixelFormat.Abgr8888, SdlTextureAccess.Static, w, h)` (Abgr8888 is RGBA byte order), `UpdateTexture(t, null, tex.GetPixels(), width * 4)`, `SetTextureBlendMode(t, 1)`, then `tex.SetTexID(new ImTextureID(t.Pointer.Value))` and `tex.SetStatus(OK)`; `WantUpdates` -> `UpdateTexture` per `tex.Updates[i]` rectangle, with pixels from `tex.GetPixelsAt(x, y)` and the **pitch of the whole atlas** (`Width * 4`); `WantDestroy` with `UnusedFrames > 0` -> destroy, `SetTexID(ImTextureID.Invalid)`, `SetStatus(Destroyed)`. Glyphs are baked on demand, so new characters on a later frame trigger `WantUpdates`; the recipe saw 1 create and 5 updates in 6 frames.
- Draw: per `ImDrawList`, convert `ImDrawVert.col` (packed ABGR, R in the low byte) to an `SdlFColor[]` (SDL3 vertex colours are float RGBA). Per `ImDrawCmd`: `SetRenderClipRect` from `ClipRect` (minus `DisplayPos`, times `FramebufferScale`), then `RenderGeometryRaw(renderer, texture, &vtx[cmd.VtxOffset].pos, 20, colors + VtxOffset, 16, &vtx[VtxOffset].uv, 20, vtxCount - VtxOffset, idx + cmd.IdxOffset, cmd.ElemCount, 2)`. Indices are relative to `VtxOffset`, so shifting the vertex base pointers replaces a per-index offset. Pass the atlas texture for every command: ImGui's solid shapes sample a white pixel inside it. After the loop `SetRenderClipRect(r, null)`.
- **Your own textures (`ImGui.Image`):** every `ImTextureID` this backend hands out is the `SDL_Texture` pointer, so the draw loop just converts back (`SdlTexture texture = (void*)cmd.GetTexID().Value;`) and any texture made on the same renderer works: `ImGui.Image(new ImTextureRef { _TexID = new ImTextureID(myTexture.Pointer.Value) }, size)` (the recipe wraps this as `SdlRenderGeometryBackend.TextureRef`). You keep ownership; destroy it after the last frame that shows it.
- **Draw callbacks:** when `cmd.UserCallback != 0` draw nothing for that command; if it is `-8` (`ImDrawCallback_ResetRenderState`, a marker, never call it) reset the clip rectangle, otherwise call it: `((delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)cmd.UserCallback)(list.Ptr, (ImDrawCmd*)list.CmdBuffer.Data + i)`. Add one with `drawList.AddCallback(callback, userData)`: ImGui copies the unmanaged `userData` (read it as `*(T*)cmd->UserCallbackData`) but keeps only the delegate's function pointer, so keep the `ImDrawCallback` rooted. Inside, plain SDL calls draw into the frame, clipped by the previous command's clip rectangle.
- A software renderer on a surface only writes the surface when its queue is flushed: call `Sdl.Render.FlushRenderer` (or `RenderPresent` / `RenderReadPixels`) before reading pixels. Forgetting it gave a black frame with every call returning true.
- Headless there is no window for `SdlImGuiGlue.NewFrame`, so the recipe sets `io.DisplaySize`, `DisplayFramebufferScale` and `DeltaTime` itself and uses the glue only for `ProcessEvent`.
Verified by running: about 8,200 lit pixels, a synthetic click made `Button` return true once, button pixels are bluish (vertex colours and blending arrive intact), one atlas texture created and glyph updates uploaded, and a rectangle much wider than its window is cut by the clip rectangle (ImGui only culls text on the CPU, so this rectangle really tests `SetRenderClipRect`; I removed the call once and the check failed with 1,370 stray pixels). The frame was checked by eye. Also run: a 2x2 green `SdlTexture` drawn with `ImGui.Image` read back pure green (with a null ID it reads white, and that check fails), and a callback that draws a magenta square with `RenderFillRect` ran once per frame with user data 42 and its square is in the frame; a `ResetRenderState` marker in the same list was skipped safely. Removing the callback call fails the recipe.
- **Not run:** a real window renderer (hardware or GPU-backed `SDL_Renderer`: same calls, but only the software one was run), `DisplayFramebufferScale` above 1 (high-DPI), a project using it in a real game loop.

### RenderDoc

**RenderDoc** [DOC]: call `RenderDoc.Initialize(new(args))` first in `Main`. `RenderDocWindow.Instance.OnImGui()` adds a capture UI inside ImGui.

## Traps and bugs

| ID | Trap | Fix |
|---|---|---|
| T9 | A stock `glfw3.dll` fails; `GlfwWindow` binds fork-only functions (`glfwSetScrollDetailCallback`, trackpad) at `Glfw.Window.cs:610-612` | Use the packaged binary |
| T22 | ImGui's **shipped** platform backend is GLFW only (no SDL). Renderers: Vulkan, or OpenGL 4.1+ core (GLSL 410) | Use GLFW, or the hand-written SDL glue of 7.35 with `OpenGlBackend` |
| T47 | BRUTAL's `OpenGlBackend` and `VulkanBackend` never call `ImDrawList.AddCallback` callbacks: the OpenGL loop has no `UserCallback` branch, the Vulkan one is a commented-out TODO (`VulkanBackend.cs:199-215`). Nothing fails; the callback just never runs. [RUN] for OpenGL (7.35 asserts it), [SRC] for Vulkan | Draw custom content outside ImGui (before or after `RenderDrawData`), or use a backend that honours callbacks (7.36) |
| T49 | A hand-written platform glue that sends only `LeftCtrl`/`RightCtrl` etc. key events gets no shortcuts: ImGui reads Ctrl/Shift/Alt/Super from `ImGuiKey.Mod_*`, not from the left/right keys, so Ctrl+A/C/V/Z silently do nothing. [RUN] (7.35) | Send `io.AddKeyEvent(ImGuiKey.Mod_Ctrl, ...)` and the other three from the SDL event's `mod` bits before each key event, as BRUTAL's GLFW backend does |

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B7 | `Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:1236,1337` | `ErrorOutOfDateKHR` checks never run (T4) | Catch `VkResultError` around the backend frame |

## Export gaps

GLFW (16 missing: macOS/X11/Wayland getters), ImPlot (201 `*_internal`), ImGui.Backends (2, harmless): see `export-gaps.md`.
