# GLFW, ImGui, ImPlot, RenderDoc

Read this for GLFW windows and input, Dear ImGui / ImPlot debug UI, or RenderDoc capture. BRUTAL ships only a GLFW platform backend for ImGui (T22); SDL games use a small hand-written glue: `imgui-sdl.md` (7.35, and 7.36 for the SDL renderer).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Glfw | `Brutal.GlfwApi` | glfw3.dll, **RocketWerkz fork of 3.5.1** (T9) | `Null` platform | Gen+Manual | Window events are C# events |
| ImGui (+Abstractions/Backends/Extensions/FreeType), ImPlot | `Brutal.ImGuiApi*`, `Brutal.ImPlotApi` | imgui, imgui_freetype, implot | — | Gen (ImGui 1.92.2 docking, ImPlot 0.17) | T22. No recipe for ImPlot, FreeType fonts, the in-game console or `ImGuiEx`; upstream's `samples/Hello/HelloImPlot.cs`, `HelloImGuiFreeType.cs`, `HelloImGuiAbstractions.cs`, `HelloImGuiExtensions.cs` and `samples/Demo/DemoImGui.cs` run [RUN: SampleCheck] |
| RenderDoc (+ImGui) | `Brutal.RenderDocApi` | installed RenderDoc | — | Gen+Manual | `RenderDoc.Initialize(new RenderDoc.InitOptions(args))` loads it only with `--RenderDoc`; 7.42, T54 |

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
- `Glfw.VersionString` (a property wrapping `Glfw.PInvoke.GetVersionString()`) → `"3.5.1 Win32 WGL Null EGL OSMesa VisualC DLL"`; `Glfw.VulkanSupported()`.
- For CI with no display, call `Glfw.SetInitHint(GlfwInitHint.Platform, (int)GlfwPlatform.Null)` before `Init` [RUN]. Window creation and `PollEvents` work with it (`RecipeCheck -- 7.8 --no-window`).

### 7.21 Dear ImGui debug UI on GLFW + OpenGL [RUN]
Packages `Ahwoo.BRUTAL.ImGui`, `.ImGui.Backends`, `.OpenGl` and `.Glfw`. Namespaces `Brutal.ImGuiApi`, `Brutal.ImGuiApi.Backends`, `Brutal.OpenGlApi`, `Brutal.GlfwApi`. Runnable copy: `RecipeCheck -- 7.21`.
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
- **SDL games:** `imgui-sdl.md` (7.35 with OpenGL, 7.36 on the SDL renderer).

### 7.42 RenderDoc: opt-in loading [RUN without RenderDoc installed]
Runnable copy: `RecipeCheck 7.42`. A real capture wasn't run.
```csharp
var attached = RenderDoc.Initialize(new RenderDoc.InitOptions(args));      // loads only if args has --RenderDoc (any case)
if (RenderDoc.IsAvailable) RenderDoc.StartFrameCapture(default, default);  // guard EVERY call (T54)
```
- Without the switch, or with it and no RenderDoc, it returns `false` without throwing.
- It looks only in DLLs already loaded into the process (launched from RenderDoc) and in `C:\Program Files\RenderDoc\renderdoc.dll` by default; `InitOptions.SearchPaths` overrides that.
- The first attempt latches: later `LoadApi` calls return the cached result. `RemoveHooks()` is the only reset.
- `RenderDocWindow.Instance.OnImGui()` (package RenderDoc.ImGui) adds a capture UI inside ImGui [DOC].

## Traps and bugs

| ID | Trap | Fix |
|---|---|---|
| T9 | A stock `glfw3.dll` fails; `GlfwWindow` binds fork-only functions (`glfwSetScrollDetailCallback`, trackpad) at `Glfw.Window.cs:610-612` | Use the packaged binary |
| T22 | ImGui's **shipped** platform backend is GLFW only (no SDL). Renderers: Vulkan, or OpenGL 4.1+ core (GLSL 410) | Use GLFW, or the hand-written SDL glue of 7.35 (`imgui-sdl.md`) with `OpenGlBackend` |
| T47 | BRUTAL's `OpenGlBackend` and `VulkanBackend` never call `ImDrawList.AddCallback` callbacks, silently (`VulkanBackend.cs:199-215` is a TODO) [RUN: OpenGL; SRC: Vulkan] | Draw custom content outside ImGui (before or after `RenderDrawData`), or use a backend that honours callbacks (7.36, `imgui-sdl.md`) |
| T54 | **No RenderDoc call checks that RenderDoc loaded.** Without RenderDoc (every player's machine) every call throws `NullReferenceException` [RUN: 7.42] | Check `RenderDoc.IsAvailable` before each call, or put capture code behind one helper that does |

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B7 | `Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:1236,1337` | `ErrorOutOfDateKHR` checks never run (T4) | Catch `VkResultError` around the backend frame |

## Export gaps

GLFW (16 missing: macOS/X11/Wayland getters), ImPlot (201 `*_internal`; the public plotting calls in `HelloImPlot.cs` run), ImGui.Backends (2, harmless): see `export-gaps.md`.
