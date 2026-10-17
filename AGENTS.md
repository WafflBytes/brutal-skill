# AGENTS.md: BRUTAL framework projects

**Before writing or changing any code that uses BRUTAL, read `.claude/skills/brutal/SKILL.md`.** Then read the file in `.claude/skills/brutal/references/` for each library you'll touch (SDL, OpenGL, Vulkan, physics, and so on; `SKILL.md` has the table). Together they are the verified reference for this codebase: rules, library map, C→BRUTAL name translation, recipes that compiled and ran, runtime traps (T1–T44), framework bugs (B1–B9) and native export gaps. Your training data almost certainly doesn't cover BRUTAL, so don't guess API names. (It's packaged as a Claude Code skill, but it's plain Markdown that any agent can read.)

## Layout

- `.claude/skills/brutal/`: the reference (`SKILL.md` + `references/*.md`). Update it there.
- `BRUTAL/`: a clone of https://github.com/ahwootech/BRUTAL. It holds the source of every `Ahwoo.BRUTAL.*` package, with no project files. Read it to confirm APIs; don't edit it.
- `Templates/brutal-sdl/` (2D) and `Templates/brutal-3d/` (OpenGL + Jolt): starter game templates. New games start from one: `dotnet new install Templates/brutal-sdl` (or `brutal-3d`), then `dotnet new brutal-sdl -n MyGame`.
- `Sandbox/`: verification tools (`RecipeCheck`, `VulkanCompute`, `ExportAudit`, `AnalyzerDump`, `FixedProbe`; see `Sandbox/README.md`). Run all of them, plus the template check and the skill lint, with `powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1`, or one with `dotnet run -c Release --project Sandbox/<Name>`.

## Critical rules (summary; the skill has the details)

1. **Confirm every BRUTAL API in source** (`rg` in `BRUTAL/`) before using it. Package READMEs are partly stale: Jolt's README uses `EMotionType`, but the type is `MotionType`.
2. **Compiling is not working.** 774 Jolt bindings compile but throw `EntryPointNotFoundException`; for example, use `BoxShapeSettings.Create(...).CreateShape()`, not `BoxShape.Create(float3)`. Mesh, convex-hull and height-field shapes can't be created at all in Jolt 2026.10.2 (T36); use a static compound of primitives. Run new native calls, or check them with `Sandbox/ExportAudit`.
3. **BRUTALCORE0009/0010 are build errors**: a pointer from a managed array. Use `stackalloc`, native memory (`NativeList<T>.AsSpan()`, `Mem`) or `fixed`. Prefer `u8` string literals for native calls.
4. **Silent failures to guard against:**
   - `Stb.Load` returns a null image instead of throwing.
   - Vulkan `sType` is set only by `new T()`.
   - Vulkan errors (including `ErrorOutOfDateKHR` on acquire/present) throw `VkResultError`.
   - Vulkan output arrays are typed `ReadOnlySpan` but get written.
5. **Own native memory and handles:** dispose each exactly once. Handles are value types, so copies alias. Never dispose borrowed `Get*` handles or Jolt's `Factory` (use `Factory.DestroyInstance()`).
6. **Keep SDL and GLFW on the main thread.** Keep game logic free of SDL, GLFW and Vulkan types so it can be tested headless.
7. **Verify by running:** a headless self-test, a scripted snapshot run, and for publish output, a run from an empty folder.
8. **Licence:** anything distributed needs the BRUTAL credits notice, the logo before gameplay, and `NOTICE.txt` shipped beside it.
9. **Keep the reference current:** when you verify something new or find a new trap, update the skill files and run `Sandbox/lint-skill.ps1`.

Project basics: target `net10.0`, set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, and restore the `Ahwoo.BRUTAL.*` packages anonymously from nuget.org.
