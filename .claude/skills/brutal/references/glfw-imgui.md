# GLFW, ImGui, ImPlot, RenderDoc

Read this for GLFW windows and input, Dear ImGui / ImPlot debug UI, or RenderDoc capture. ImGui's platform backend is GLFW only (T22).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Glfw | `Brutal.GlfwApi` | glfw3.dll, **RocketWerkz fork of 3.5.1** (T9) | `Null` platform | Gen+Manual | Window events are C# events |
| ImGui (+Abstractions/Backends/Extensions/FreeType), ImPlot | `Brutal.ImGuiApi*`, `Brutal.ImPlotApi` | imgui, imgui_freetype, implot | — | Gen (ImGui 1.92.2 docking, ImPlot 0.17) | T22 |
| RenderDoc (+ImGui) | `Brutal.RenderDocApi` | installed RenderDoc | — | Gen+Manual | `RenderDoc.Initialize(new(args))` attaches when run with `--RenderDoc` |

**Not yet verified:** no ImGui recipe has been run here. Start from `BRUTAL/Brutal.ImGui.Backends/` (GLFW platform + OpenGL or Vulkan renderer), and add a recipe once it works.

## Name translation

**GLFW**
- `Glfw.Init()` returns bool and doesn't throw.
- `glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API)` → `Glfw.WindowHint(GlfwWindowHint.ClientApi, 0)`.
- `glfwInitHint` → `Glfw.SetInitHint(GlfwInitHint.X, v)`.
- Callbacks are window events (`OnKey`, `OnClose`, `OnCursorPos`, `OnMouseButton`, `OnScroll`, `OnFramebufferSize`, `OnDrop`). Exceptions in them are rethrown from `Glfw.PollEvents()`.
- Raw layer: `Glfw.PInvoke.*`.

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

### RenderDoc

**RenderDoc** [DOC]: call `RenderDoc.Initialize(new(args))` first in `Main`. `RenderDocWindow.Instance.OnImGui()` adds a capture UI inside ImGui.

## Traps and bugs

| ID | Trap | Fix |
|---|---|---|
| T9 | A stock `glfw3.dll` fails; `GlfwWindow` binds fork-only functions (`glfwSetScrollDetailCallback`, trackpad) at `Glfw.Window.cs:610-612` | Use the packaged binary |
| T22 | ImGui's platform backend is **GLFW only** (no SDL). Renderers: Vulkan, or OpenGL 4.1+ core (GLSL 410) | Use GLFW for ImGui apps |

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B7 | `Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:1236,1337` | `ErrorOutOfDateKHR` checks never run (T4) | Catch `VkResultError` around the backend frame |

## Export gaps

GLFW (16 missing: macOS/X11/Wayland getters), ImPlot (201 `*_internal`), ImGui.Backends (2, harmless): see `export-gaps.md`.
