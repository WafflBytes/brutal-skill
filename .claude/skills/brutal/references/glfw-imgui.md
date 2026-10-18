# GLFW, ImGui, ImPlot, RenderDoc

Read this for GLFW windows and input, Dear ImGui / ImPlot debug UI, or RenderDoc capture. BRUTAL ships only a GLFW platform backend for ImGui (T22); SDL games use a small hand-written glue (7.35).

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
- **SDL games:** BRUTAL has no SDL platform backend (T22), but a hand-written one works: see 7.35. A separate GLFW tool window or SDL_ttf debug text (`sdl.md` 7.19) are the alternatives.

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
- Keys: `AddKeyEvent(MapKey(ev.key.scancode), down)`. The table maps `SdlScancode` to `ImGuiKey` (`A`..`Z` and `F1`..`F12` are contiguous in both enums, `N1`..`N9` -> `_1`..`_9`, `N0` -> `_0`, `Return` -> `Enter`, `Pageup` -> `PageUp`, `Equals` -> `Equal`, `Grave` -> `GraveAccent`, `Lctrl`/`Lshift`/`Lalt`/`Lgui` -> `LeftCtrl`...`LeftSuper`, `Kp*`/`KpPlus`... -> `Keypad*`). Modifiers need no extra code: ImGui derives Ctrl/Shift/Alt from the left/right key events. Scancode-based means shortcuts follow key position, not layout.
- Text: `TextInput` -> `AddInputCharacter` per rune of the UTF-8 `ev.text.text`. SDL3 sends text events only while text input is started on the window (`Sdl.Keyboard.StartTextInput(window)` / `StopTextInput`) [SRC]; the recipe feeds the text event directly, so whether real key presses produce `TextInput` without that call was not run. Start it while `io.WantTextInput` is true.
- Focus: `WindowFocusGained/Lost` -> `AddFocusEvent`.
- Game input: after the glue, skip your own handling when `io.WantCaptureMouse` (mouse events) or `io.WantCaptureKeyboard` (key and text events). Verified: true over a window and in an active text field, false over empty space and after clicking away.
Checked by running synthetic SDL events (pushed with `Sdl.Events.PushEvent`) through the glue: a click made `Button` return true once, a click toggled a checkbox, a drag moved a slider, `"hi"` + Backspace left `"h"` in an `InputText`, and the framebuffer read back with 12,994 lit pixels (the frame also looked right). I broke one check on purpose to see it fail, then restored it. `OpenGlBackend`'s constructor sets `RendererHasViewports` and calls `InitMultiViewportSupport`: harmless with `ViewportsEnable` off (the default; viewports would need a platform glue that creates windows, which this one doesn't).
- **Not run:** clipboard (`Sdl.PInvoke.SetClipboardText` / `GetClipboardText` into `platformIo.Platform_*ClipboardTextFn`), mouse cursor shapes (`SDL_CreateSystemCursor`), gamepad navigation, multi-viewports, high-DPI (the scale maths is the same as GLFW's but only ran at 1.0), an IME, and a real (visible, focused) window with real input.
- Text pushed with `PushEvent` carries a raw `byte*` that SDL does not own for you; the recipe calls `ProcessEvent` directly for the synthetic text event. Real SDL text events are fine.

### RenderDoc

**RenderDoc** [DOC]: call `RenderDoc.Initialize(new(args))` first in `Main`. `RenderDocWindow.Instance.OnImGui()` adds a capture UI inside ImGui.

## Traps and bugs

| ID | Trap | Fix |
|---|---|---|
| T9 | A stock `glfw3.dll` fails; `GlfwWindow` binds fork-only functions (`glfwSetScrollDetailCallback`, trackpad) at `Glfw.Window.cs:610-612` | Use the packaged binary |
| T22 | ImGui's **shipped** platform backend is GLFW only (no SDL). Renderers: Vulkan, or OpenGL 4.1+ core (GLSL 410) | Use GLFW, or the hand-written SDL glue of 7.35 with `OpenGlBackend` |

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B7 | `Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:1236,1337` | `ErrorOutOfDateKHR` checks never run (T4) | Catch `VkResultError` around the backend frame |

## Export gaps

GLFW (16 missing: macOS/X11/Wayland getters), ImPlot (201 `*_internal`), ImGui.Backends (2, harmless): see `export-gaps.md`.
