# Dear ImGui in an SDL3 game

Read this to put Dear ImGui into an SDL3 game: on an SDL window with OpenGL (7.35) or on the plain SDL renderer (7.36). For ImGui on GLFW, the ImGui packages and the shipped backends, read `glfw-imgui.md` (its traps T22 and T47 apply here too).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

**ImGui names:** `ImGui.Begin(ImString name, ref bool open, flags)`. Pointer types become `*Ptr` structs (`ImDrawDataPtr`).

## Recipes

### 7.35 Dear ImGui on an SDL3 window + OpenGL 4.1 [RUN]
`OpenGlBackend` has no GLFW dependency: it needs a current GL 4.1+ core context with GLAD loaded, and uploads the font atlas itself. `IPlatformBackend` is only `NewFrame()` + `Dispose()`, so a small hand-written glue is enough. Copy the full class (`SdlImGuiGlue`, ~150 lines with the key table) from `Sandbox/RecipeCheck/Recipes.ImGuiSdl.cs`. Window and context as in `opengl.md` 7.17.
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
- Text: `TextInput` -> `AddInputCharacter` per rune of the UTF-8 `ev.text.text`. SDL3 sends text events only between `Sdl.Keyboard.StartTextInput(window)` and `StopTextInput` [SRC]: start it while `io.WantTextInput` is true.
- Focus: `WindowFocusGained/Lost` -> `AddFocusEvent`.
- Clipboard: in the constructor set `ImGui.GetPlatformIO().Platform_GetClipboardTextFn` / `Platform_SetClipboardTextFn` to `Marshal.GetFunctionPointerForDelegate(...)` of `ImGuiPlatformIO.GetClipboardTextFnDelegate` / `SetClipboardTextFnDelegate` fields (keep the delegates rooted, T35). Get: `Sdl.PInvoke.GetClipboardText()` returns memory SDL allocated; hand it to ImGui and `Sdl.PInvoke.Free` it on the next call and in `Dispose`. Set: `Sdl.PInvoke.SetClipboardText(Marshal.PtrToStringUTF8((nint)text))`. Reset both to 0 in `Dispose`. On Windows ImGui already uses the OS clipboard; this matters on Linux and macOS.
- Cursor shapes: set `io.BackendFlags |= HasMouseCursors`; in `NewFrame` read `ImGui.GetMouseCursor()` (the shape the last frame asked for) and, only when it changed, `Sdl.Mouse.SetCursor(cursor)` + `ShowCursor()`, or `HideCursor()` for `None` / `io.MouseDrawCursor`. Create the cursors lazily with `Sdl.Mouse.CreateSystemCursor` (needs the video subsystem; throws `SdlException`): `TextInput` -> `Text`, `ResizeAll` -> `Move`, `ResizeNS/EW/NESW/NWSE` -> `NsResize`/`EwResize`/`NeswResize`/`NwseResize`, `Hand` -> `Pointer`, `Wait`, `Progress`, `NotAllowed`, the rest `Default`. `DestroyCursor` them in `Dispose`. Skip it all when `io.ConfigFlags` has `NoMouseCursorChange`.
- Gamepad navigation: `GamepadAdded` -> `io.BackendFlags |= HasGamepad` (clear it on `GamepadRemoved` when `!Sdl.Gamepad.HasGamepad()`); the game sets `io.ConfigFlags |= NavEnableGamepad` and opens the pad (`OpenGamepad`; SDL sends button events only for open pads). `GamepadButtonDown/Up` -> `AddKeyEvent`: `South` -> `GamepadFaceDown` (activate), `East` -> `FaceRight` (cancel), `West` -> `FaceLeft`, `North` -> `FaceUp`, D-pad -> `GamepadDpad*`, shoulders -> `L1`/`R1`, stick clicks -> `L3`/`R3`, `Start`, `Back`. `GamepadAxisMotion` -> `AddKeyAnalogEvent` for both directions of a stick axis (dead zone 8000 of 32767, held above 0.1) and for the triggers (`L2`/`R2`, 0..32767).
- Game input: after the glue, skip your own handling when `io.WantCaptureMouse` (mouse events) or `io.WantCaptureKeyboard` (key and text events).
- Keep `ViewportsEnable` off (the default): this glue creates no extra windows.
- **Not run:** a visible window with real input, the clipboard off Windows, high-DPI, an IME.
- **BRUTAL's `OpenGlBackend` never calls draw callbacks (T47):** Use 7.36's pattern if you need callbacks.
- In tests, call `ProcessEvent` directly for synthetic text: a pushed text event's `byte*` isn't owned by SDL.

### 7.36 Dear ImGui renderer on `SDL_RenderGeometry` (SDL games without OpenGL) [RUN]
BRUTAL's ImGui renderers are OpenGL and Vulkan only, but ImGui's output maps onto `Sdl.Render.RenderGeometryRaw`. Copy `SdlRenderGeometryBackend : IRendererBackend` (~120 lines) from `Sandbox/RecipeCheck/Recipes.ImGuiSdlRender.cs`; input uses 7.35's `SdlImGuiGlue`.
- Constructor: set `io.BackendFlags |= RendererHasVtxOffset | RendererHasTextures`. Without the second flag ImGui 1.92 expects you to build the font atlas yourself.
- Textures: for every `drawData.Textures[i]` whose `Status != OK` (`using Brutal;` for `IsNotNull`): `WantCreate` -> `Sdl.Render.CreateTexture(r, SdlPixelFormat.Abgr8888, SdlTextureAccess.Static, w, h)` (Abgr8888 is RGBA byte order), `UpdateTexture(t, null, tex.GetPixels(), width * 4)`, `SetTextureBlendMode(t, 1)`, then `tex.SetTexID(new ImTextureID(t.Pointer.Value))` and `tex.SetStatus(OK)`; `WantUpdates` -> `UpdateTexture` per `tex.Updates[i]` rectangle, with pixels from `tex.GetPixelsAt(x, y)` and the **pitch of the whole atlas** (`Width * 4`); `WantDestroy` with `UnusedFrames > 0` -> destroy, `SetTexID(ImTextureID.Invalid)`, `SetStatus(Destroyed)`. Glyphs are baked on demand, so expect `WantUpdates` on later frames.
- Draw: per `ImDrawList`, convert `ImDrawVert.col` (packed ABGR, R in the low byte) to an `SdlFColor[]` (SDL3 vertex colours are float RGBA). Per `ImDrawCmd`: `SetRenderClipRect` from `ClipRect` (minus `DisplayPos`, times `FramebufferScale`), then `RenderGeometryRaw(renderer, texture, &vtx[cmd.VtxOffset].pos, 20, colors + VtxOffset, 16, &vtx[VtxOffset].uv, 20, vtxCount - VtxOffset, idx + cmd.IdxOffset, cmd.ElemCount, 2)`. Indices are relative to `VtxOffset`, so shifting the vertex base pointers replaces a per-index offset. Pass the atlas texture for every command: ImGui's solid shapes sample a white pixel inside it. After the loop `SetRenderClipRect(r, null)`.
- **Your own textures (`ImGui.Image`):** every `ImTextureID` this backend hands out is the `SDL_Texture` pointer, so the draw loop just converts back (`SdlTexture texture = (void*)cmd.GetTexID().Value;`) and any texture made on the same renderer works: `ImGui.Image(new ImTextureRef { _TexID = new ImTextureID(myTexture.Pointer.Value) }, size)`. You keep ownership; destroy it after the last frame that shows it.
- **Draw callbacks:** when `cmd.UserCallback != 0` draw nothing for that command; if it is `-8` (`ImDrawCallback_ResetRenderState`, a marker, never call it) reset the clip rectangle, otherwise call it: `((delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)cmd.UserCallback)(list.Ptr, (ImDrawCmd*)list.CmdBuffer.Data + i)`. Add one with `drawList.AddCallback(callback, userData)`: ImGui copies the unmanaged `userData` (read it as `*(T*)cmd->UserCallbackData`) but keeps only the delegate's function pointer, so keep the `ImDrawCallback` rooted. Inside, plain SDL calls draw into the frame, clipped by the previous command's clip rectangle.
- A software renderer on a surface only writes the surface when its queue is flushed: call `Sdl.Render.FlushRenderer` (or `RenderPresent` / `RenderReadPixels`) before reading pixels. Otherwise the frame is black with every call returning true.
- Headless (no window), set `io.DisplaySize`, `DisplayFramebufferScale` and `DeltaTime` yourself and use the glue only for `ProcessEvent`.
- **Not run:** a hardware `SDL_Renderer` (only the software one), high-DPI.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T49 | A hand-written glue that sends only `LeftCtrl`/`RightCtrl` etc. gets no shortcuts: ImGui reads modifiers from `ImGuiKey.Mod_*` [RUN] | Send `io.AddKeyEvent(ImGuiKey.Mod_Ctrl, ...)` and the other three from the SDL event's `mod` bits before each key event, as BRUTAL's GLFW backend does |
