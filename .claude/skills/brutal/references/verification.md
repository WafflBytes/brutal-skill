# Verification workflow and Sandbox tools

Read this when verifying beyond the template's own checks, running or extending `Sandbox/`, or after upgrading BRUTAL packages. The dated history of what was verified is in `VERIFICATION-LOG.md` at BRUTAL home (maintainers only; you never need it to build a game).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Verification workflow

1. **Build.** Analyzer errors and warnings (0001/0002/0004/0009–0012) are real; fix them, don't suppress.
2. **`--selftest`.** Headless invariant checks on the backend-free simulation: conservation, settling, reactions, performance budget.
3. **`--snapshot <dir>`.** Scripted input, PNGs via `RenderReadPixels` (7.9), then exit; run it with a timeout. Look at the images.
4. **Profile frame phases** (upload/draw/HUD/present) over 30 frames before blaming the simulation (T15).
5. **Run every new native call once**, or audit it (`export-gaps.md`).
6. **Run the published output** from an empty folder (7.14).
7. **Edge cases:** missing assets (T1), Vulkan resize (T4), no Vulkan runtime (T5).

`Sandbox/` tools (details in `Sandbox/README.md`). Run all of them with `powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1` (add `-NoWindow -NoVulkan` when there's no display or GPU); the exit code is the number of failures. Run one with `dotnet run -c Release --project Sandbox/<Name>`:

| Tool | What it does |
|---|---|
| `RecipeCheck` | Runs 7.1–7.8, 7.10 (Stb round trip + T1), 7.11, 7.18–7.20 (SDL_image, SDL_ttf, SDL_mixer; T37–T40), 7.21 (ImGui), 7.35 and 7.36 (ImGui on SDL), 7.22 (Box2D events; T42), 7.23 (RakNet loopback), 7.24 (virtual gamepad) and 7.25 (glTF drawn with OpenGL; `RECIPECHECK_SHOTS=<dir>` saves the 7.21 and 7.25 frames), 7.26 (quaternion conventions; T43), 7.27 (Jolt tuning and determinism) and 7.28 (RakNet failure paths; T44), asserting each result, plus trap check `T36` (passes while the trap holds, fails when it's fixed upstream). Filter with `-- 7.6`; `--no-window`, `--no-vulkan` |
| `VulkanWindow` | Runs 7.13: triangle, live resize, swapchain rebuild, pixels read back before and after. Opens a window for ~3 s; `verify.ps1` skips it with `-NoWindow` or `-NoVulkan` |
| `VulkanDepth` | Runs 7.29 off-screen (no window): depth buffer, indexed draw, push constants, matrices, culling; pixels read back, plus a depth-off control. Skipped by `-NoVulkan` |
| `VulkanTexture` | Runs 7.30 off-screen: texture upload, sampler, descriptor set, sampling; pixels read back (orientation, nearest filter, sRGB vs UNorm). Skipped by `-NoVulkan` |
| `VulkanSplash` | Runs 7.31 off-screen: logo + SDL_ttf credits line in one texture, letterboxed quad, fade; pixels read back and a PNG written for a look. Skipped by `-NoVulkan` |
| `NetSession` | Runs 7.33: spawns a host and client processes (bots) over loopback and checks join, agreement, loss smoothing, crash, version mismatch, full host. About 30 s; exit = failed checks |
| `VulkanCompute` | Runs 7.12; exit code 0 means correct results. `-- <index>` picks a GPU |
| `ExportAudit` | Export gaps (`export-gaps.md`) for all 50 packages (`--detail`, `--package <name>`). Restoring it downloads every package |
| `AnalyzerDump` | Lists every analyzer rule, code fix and generator (`core-libs.md`) |
| `FixedProbe` | `[Fixed]` cases: builds and runs by default; `-p:ProbeErrors=true` must fail with exactly 0009 and 0010 |
| `check-template.ps1` | `-Template brutal-sdl` (default) or `brutal-3d`; `verify.ps1` runs both. `dotnet new <template>` in a throwaway template hive, then an ASCII-only check on the template sources, then build with warnings as errors, `--selftest`, `--snapshot`, `--smoke` (skipped with `-NoWindow`), single-file publish (checks `NOTICE.txt`, the font licence and `licenses/`), and the published exe run from an empty folder |
| `check-upgrades.ps1` | Compares every `Ahwoo.BRUTAL.*` version in the repo with nuget.org, and the BRUTAL mirror with the verified commit. `-Apply` bumps the versions; then run `verify.ps1` |
