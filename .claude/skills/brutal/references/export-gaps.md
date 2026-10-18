# Native export gaps (declared in C#, missing from the win-x64 DLL)

Read this before calling any binding you haven't seen run, especially Jolt, Box2D maths, ImPlot or Slang.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

Found by `Sandbox/ExportAudit`, which compares each package's P/Invoke imports with its native PE exports. Calls to these compile, then throw `EntryPointNotFoundException`. **To check one function, search `export-gaps-detail.txt`** (a hit means missing); don't run the audit for that, since restoring it downloads all 50 packages. Maintainers regenerate the list after upgrading (`maintaining.md`): `dotnet run -c Release --project Sandbox/ExportAudit -- --detail` (lines look like `missing: jolt jolt!BoxShape_Create_3`). It follows the Windows renames done by package resolvers (`glfw`→`glfw3`, `openal`→`OpenAL32`, `shaderc`→`shaderc_shared`, `vulkan`→System32 `vulkan-1`). It doesn't audit function pointers loaded at runtime (GLAD, Vulkan device functions).

| Package | Imports | Missing | What |
|---|---|---|---|
| **Jolt** | 3246 | **774** | Below |
| ImPlot | 571 | 201 | `*_internal` functions |
| Box2D | 484 | 64 | Inline math helpers (`b2Add`, `b2Dot`, `b2Cross`, `b2Length`, `b2Normalize`, `b2Lerp`, `b2MakeRot`, `b2Rot_*`, `b2TransformPoint`, `b2AABB_*`, `b2Min/Max/Clamp`…). Use Numerics |
| GLFW | 154 | 16 | macOS/X11/Wayland native getters (expected on Windows) |
| Slang | 279 | 2 | `slang_getEmbeddedCoreModule`, `spReflection_GetSession` |
| ImGui.Backends | 11 | 2 | `glfwGetCocoaWindow` (macOS) and `user32!DefWindowProc`: `[LibraryImport]` is exact-spelling, and user32 only exports `DefWindowProcA/W`. Declared in `GlfwBackend.WindowsNative.cs:53` but never called, so harmless |
| FMOD | 575 | 575 | No native binaries shipped (T21); expected |
| All others | — | 0 | SDL family, ImGui, ImGui.FreeType, ShaderC, VMA, stb, Ktx, Gli, MeshOptimizer, NFD, NotCurses, RakNet, OpenAL, GLAD, Vulkan loader |

The full Jolt gap list (shapes, `Shape`, `BodyInterface`, `PhysicsSystem`, helpers) is in `physics.md`.

**Exported isn't always working:** some exported Jolt calls bind to the wrong native overload and silently drop their arguments (T36). An empty ExportAudit row proves the symbol exists, not that it works; still run each new call once (R2).

**Upstream status:** the Jolt gaps are reported as [ahwootech/BRUTAL#13](https://github.com/ahwootech/BRUTAL/issues/13) (independent repro: 3246 declared, 774 missing). RocketWerkz replied on 2026-10-15 that all gaps are fixed internally; as of 2026-10-17 nuget.org still has only Jolt 2026.10.2. When a newer version appears, bump it in `Sandbox/ExportAudit` and `Sandbox/RecipeCheck` and re-run `Sandbox/verify.ps1`.
