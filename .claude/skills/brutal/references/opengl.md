# OpenGL 4.x (GLAD) in an SDL3 window

Read this for OpenGL rendering: context setup, GLAD loading, MSAA, FBO post-processing, screenshots, matrices.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Package

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| OpenGl | `Brutal.OpenGlApi` | glad.dll | — | Gen | GL 4.6 core via `Glad.Initialize(loader)` |

## Recipe

### 7.17 OpenGL 4.1 core in an SDL3 window [RUN]
Package `Ahwoo.BRUTAL.OpenGl` 2026.10.1 (GLAD; `glad.dll` works inside a single-file publish). Verified on an AMD discrete GPU; other vendors haven't been tested.
```csharp
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.ContextMajorVersion, 4);
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.ContextMinorVersion, 1);
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.ContextProfileMask, Sdl.Constants.GlContextProfileCore);
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.DepthSize, 24);
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.Multisamplebuffers, 1);
Sdl.PInvoke.GLSetAttribute(SdlGLAttr.Multisamplesamples, 4);       // 4x MSAA on the default framebuffer
var window = Sdl.Video.CreateWindow("Game", 1280, 720, SdlWindowFlags.Opengl | SdlWindowFlags.Resizable);
var ctx = Sdl.PInvoke.GLCreateContext(window);                    // nint; 0 = failure
Sdl.PInvoke.GLMakeCurrent(window, ctx);
static GlGetProcAddressFunc? loader;                               // keep it rooted (T35)
loader = name => (nint)Sdl.PInvoke.GLGetProcAddress(Marshal.PtrToStringUTF8((nint)name)!);
Glad.Initialize(loader);                                           // bool
Sdl.PInvoke.GLSetSwapInterval(1);                                  // vsync
// GL calls: typed function-pointer fields on OpenGl.Native, constants on OpenGl (uint, convert to GlEnum)
OpenGl.Native.glClearColor(0.5f, 0.7f, 0.9f, 1f);
OpenGl.Native.glClear(OpenGl.GL_COLOR_BUFFER_BIT | OpenGl.GL_DEPTH_BUFFER_BIT);
OpenGl.Native.glEnable(GlCapability.DepthTest);
OpenGl.Native.glUniformMatrix4fv(loc, 1, false, p);                // Bool8 from bool
Sdl.PInvoke.GLSwapWindow(window);
// Screenshot: glReadPixels(0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, p) before the swap (MSAA resolves), then flip rows.
// Size: Sdl.PInvoke.GetWindowSizeInPixels(window, &w, &h). Teardown: GLDestroyContext(ctx), DestroyWindow.
```
- Handle types are record structs: `GlShader`, `GlProgram`, `GlBuffer`, `GlVertexArray`, `GlTexture` (`.Id`); `glBindTexture` takes the raw `uint`.
- Text without SDL_Renderer: render SDL's 8x8 debug font once into a surface with `Sdl.PInvoke.CreateSoftwareRenderer(Sdl.PInvoke.CreateSurface(128, 48, SdlPixelFormat.Abgr8888))` + `Sdl.Render.RenderDebugText`, then upload `surface->pixels` as a GL texture atlas.
- Post-processing [RUN]: render into an FBO with `glRenderbufferStorageMultisample` colour (`GL_RGBA8`) + depth (`GL_DEPTH_COMPONENT24`), check `glCheckFramebufferStatus == GL_FRAMEBUFFER_COMPLETE`, resolve with `glBlitFramebuffer` into a texture-backed FBO, then draw a full-screen triangle from `gl_VertexID` (empty VAO) with the effect shader. `GlFramebuffer`/`GlRenderbuffer` are record structs; `default` binds the window.
- Matrices: Write your own column-major `Mat4` (Perspective, LookAt, rotations) for GL. Numerics `float4x4` has both `CreatePerspectiveFieldOfView` and an `operator *(float4x4, float4)`; its convention wasn't verified for GL upload [SRC].

### The `brutal-3d` template [RUN]
`Templates/brutal-3d` is a working SDL3 + OpenGL 4.1 core + Jolt game. Copy its pieces rather than rewriting them:
- `Platform/GlWindow.cs`: context attributes, 4x MSAA with a fallback, `Glad.Initialize` with a rooted loader (T35).
- `Platform/GlText.cs`: SDL_ttf text rendered to a surface, uploaded as a GL texture, re-rasterised on resize.
- `Platform/Renderer.cs`, `GlUtil.cs`: shaders, meshes, the logo as a GL texture, lighting, fog, a blob shadow.
- `Platform/SnapshotRun.cs`: off-screen PNGs through a **hidden** SDL GL window and a framebuffer, read with `glReadPixels` (rows flipped). It needs a display, unlike the SDL renderer's surface-based snapshot.
- `Platform/Mat4.cs`: column-major matrices for GL upload.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T35 | `Glad.Initialize(loader)` hands GLAD `Marshal.GetFunctionPointerForDelegate(loader)` and doesn't root the delegate (`Brutal.OpenGl/Glad.cs:13`). The `glXxx` function-pointer aliases are `global using`s inside the package, so they aren't visible in your project | Keep the loader in a static field. Call `OpenGl.Native.glXxx(...)` directly; the fields are already typed |
