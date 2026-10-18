# Verification workflow, Sandbox tools, verification log

Read this when verifying work, running or extending `Sandbox/`, or after upgrading BRUTAL packages.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

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
| `RecipeCheck` | Runs 7.1–7.8, 7.10 (Stb round trip + T1), 7.11, 7.18–7.20 (SDL_image, SDL_ttf, SDL_mixer; T37–T40), 7.21 (ImGui), 7.35 (ImGui on SDL), 7.22 (Box2D events; T42), 7.23 (RakNet loopback), 7.24 (virtual gamepad) and 7.25 (glTF drawn with OpenGL; `RECIPECHECK_SHOTS=<dir>` saves the 7.21 and 7.25 frames), 7.26 (quaternion conventions; T43), 7.27 (Jolt tuning and determinism) and 7.28 (RakNet failure paths; T44), asserting each result, plus trap check `T36` (passes while the trap holds, fails when it's fixed upstream). Filter with `-- 7.6`; `--no-window`, `--no-vulkan` |
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

## Verification log

All entries are from 2026-10-15:
- **`BrutalSmoke`:** pass.
- **`RecipeCheck`:** pass after the Jolt shape fix (T6).
- **`VulkanCompute`:** pass on a GPU, 1024 values, 0 errors.
- **`ExportAudit`** (all 50 packages): results in `export-gaps.md`.
- **`AnalyzerDump`, `FixedProbe`:** results in `core-libs.md`.

2026-10-17: `Sandbox/` rebuilt as the current tool set (RecipeCheck and ExportAudit replace the earlier `BrutalSmoke`, `VulkanProbe` and `AllPackages` experiments). `verify.ps1`: all pass on Windows 11 x64, .NET SDK 10.0.402, AMD discrete + integrated GPUs.
- **`RecipeCheck`:** 10/10 pass. New: 7.10 Stb PNG write/load round trip, T1 confirmed (missing file gives `Data == null`, no exception), GLFW Null platform [RUN].
- **`VulkanCompute`:** 1024 values, 0 errors, on both a discrete and an integrated AMD GPU.
- **`ExportAudit`:** every export-gap count reproduced (Jolt 3246/774, ImPlot 571/201, Box2D 484/64, GLFW 154/16, Slang 279/2). New row: ImGui.Backends 11/2 (harmless).
- **`AnalyzerDump`:** the same 12 rules, 7 code fixes and 1 generator as `core-libs.md`. Analyzers reference Roslyn 5.0.
- **`FixedProbe`:** the accepted patterns compile and run; the error build gives exactly BRUTALCORE0009 and 0010.

2026-10-17 (later): T36 verified with RecipeCheck check `T36` after reading [ahwootech/BRUTAL#13](https://github.com/ahwootech/BRUTAL/issues/13). Mesh, convex-hull and height-field creation all throw `JoltException` on Jolt 2026.10.2; a `StaticCompoundShapeSettings` of two boxes collides (ball rests at y=0.48). RecipeCheck: 11/11 pass.

2026-10-17, starter template (`Templates/brutal-sdl`, `check-template.ps1`): all steps pass. Build: 0 warnings. `--selftest`: 6/6. `--snapshot`: 4 PNGs via an SDL software renderer on a plain surface, with no window or video init (splash and play screens checked by eye). `--smoke 3`: direct3d11 with vsync, 492 frames for 3.0 s of game time (fixed step held at 60 Hz while vsync ran at the monitor rate, T33). Publish: a 37.9 MB single-file exe plus `NOTICE.txt`; from an empty folder, `--selftest` and `--snapshot` both pass.

2026-10-17: `BRUTAL/` replaced with a real `git clone` of ahwootech/BRUTAL at `a25cdf8` (one docs-only commit after `b30520d`). The previous copy was byte-identical to `a25cdf8`. `lint-skill.ps1` now fails when `BRUTAL/` HEAD differs from the commit in `SKILL.md`.

2026-10-17: recipes 7.18 SDL_image, 7.19 SDL_ttf and 7.20 SDL_mixer added to `RecipeCheck`. All pass headless (software renderer + offline mixer), and the device mixer step plays on the default output at zero gain. Found T37 (no WEBP and no WEBP detection), T38 (`Generate` return value), T39 (no bundled font) and T40 (`PlayTrack` resets loops; `RecipeCheck` fails if a fix ships upstream).

2026-10-17: template gained TrueType text (embedded Atkinson Hyperlegible, SIL OFL) and SDL_mixer sound effects. `check-template.ps1` passes: 8/8 self-test checks (including the font rendering and every sound decoding and playing on an offline mixer); 5 snapshots, with sharp text at 720p and 1080p; `--smoke` with the device mixer muted and a live resize (text scale 2.00 → 2.50); publish at 38.8 MB with `NOTICE.txt`, `OFL-AtkinsonHyperlegible.txt` and four package notices in `licenses/` (new MSBuild target, `shipping.md`).

2026-10-17: recipe 7.21 (ImGui on GLFW + OpenGL 4.1 core) added to `RecipeCheck` and passing: 204 vertices drawn, and a synthetic click made `Button()` return true exactly once. The frame was checked by eye. Also: the trap index moved from `SKILL.md` to `references/traps.md` (SKILL.md 20.2 → 16.9 KB), and `lint-skill.ps1` now flags control characters, after one had replaced a `\b` in a lookup command.

2026-10-17, real-task eval (`brutal-skill-eval/iteration-1`, outside the repo): two agents built the same Breakout from one user prompt. Both games passed the objective grading. **With the skill:** 9.3 min, 199k tokens, 57 tool calls, 0 build or runtime errors, started from the template. **Baseline:** 20.8 min, 213k tokens, 100 tool calls, 1 build error (the `repeat` byte) and 2 runtime bugs. The baseline wasn't skill-free: subagents inherit the parent session's loaded project instructions, which still held the old single-file guide, so treat it as "skill vs old guide". For a clean baseline, run it as a fresh session in a folder without BRUTAL project instructions. Fixes applied from the eval: crash dialog and log, .NET runtime notices, player README.txt, pause on focus loss, event-driven input that `--smoke` drives with synthetic events (5 checks), SDL struct location and field names, and two template README bugs (double-encoded ©, a dangling NOTICE.md reference).

2026-10-17, clean baseline for the Breakout eval: a separate session started outside the repo, with the skill link removed and no BRUTAL instructions loaded. 12/13 objective checks (it deliberately shipped SDL3.dll beside the exe instead of single-file), about 15 min wall time, 0 build errors. It found what neither skill run nor our checks had: SDL3.dll's VC++ runtime dependency. That became T41; the template now checks at start-up with a Win32 dialog, and `check-template.ps1` tests it with `--vcruntime-test`. Three-way result (n = 1 each, so indicative only): skill 9.3 min and the richest game; old guide 20.8 min; no help 15 min. Lesson: an eval without help can still out-find the skill on a distribution problem, so keep running one.

2026-10-17, coverage round: 7.13 windowed Vulkan is now [RUN] (`Sandbox/VulkanWindow`: triangle at 640x400 and 900x560, pixels read back, 1 swapchain rebuild). On the RX 7900 XTX the driver reported the resize only as `SuboptimalKHR` (returned twice, `ErrorOutOfDateKHR` never thrown), so handling only the T4 exception isn't enough. New RecipeCheck recipes: 7.22 Box2D events (found T42: every event flag is off by default, and sensors need it on both shapes), 7.23 RakNet client/server over loopback (about 90 ms connect plus round trip), 7.24 SDL gamepad through a virtual device (hot-plug, axes, buttons, rumble callback) and 7.25 glTF loaded and drawn with OpenGL (vertex colours checked by pixel; looked at by eye). New `check-upgrades.ps1`; its `-Apply` path was tested by downgrading one reference and letting it bump back. All of these passed first time except RakNet (`Stream` vs `System.IO.Stream`, CS0104) and glTF (enum casing, `OpenGl.Native` being a static class).

2026-10-17, CI: `.github/workflows/verify.yml` added (Windows job: `verify.ps1 -NoWindow -NoVulkan -NoDialog`; Linux job: Sandbox built for `linux-x64` via `BrutalRid`). Not yet run on GitHub. First run outside Windows, in WSL2 Ubuntu 26.04 x64 (.NET SDK 10.0.402, invariant globalization because that install has no libicu): RecipeCheck `--no-window --no-vulkan` passed every recipe it ran; the device mixer in 7.20 found no audio device (`libasound.so.2` missing) and fell back as designed. AnalyzerDump (12 rules), FixedProbe (0009, 0010) and the lint under pwsh 7.6 matched Windows. VulkanCompute couldn't load the Vulkan loader. On Windows, `verify.ps1 -NoWindow -NoVulkan -NoDialog` passes and prints SKIP for what it skips.

2026-10-18, eval iteration 2 (three harder tasks, with and without the skill; `brutal-skill-eval/iteration-2/RESULTS.md`, outside the repo): a 3D Jolt roller, online Pong over RakNet, a Vulkan cube. All six built working programs. Every skill run shipped a licence-correct single exe and made 0 wrong API guesses; two of three baselines had no logo splash (not shareable) and all three shipped 200+ loose files. Fixes from it: the `brutal-3d` template (built from the verified roller run; `check-template.ps1 -Template brutal-3d` passes end to end, 40.3 MB single exe, 11 snapshots, both dialogs); recipe 7.13's minimise wait moved out of the swapchain rebuild (VulkanWindow now tests minimise + close + restore); new RecipeCheck recipes 7.26 quaternions (T43), 7.27 Jolt tuning + determinism, 7.28 RakNet failure paths (T44); 7.29 Vulkan depth/buffers/push constants/matrices from the eval code; template smoke ignores real focus events; ASCII-only templates (PowerShell 5.1 note).

When you verify something new, add a line here and change its marker to [RUN].

2026-10-18, TODO Priority 1.1: new `Sandbox/VulkanDepth` (off-screen, no window) runs recipe 7.29 end to end: depth image and depth-tested render pass, vertex/index buffers, push-constant MVP, back-face culling, pixel read-back plus a depth-off control; all 5 checks passed first time on the RX 7900 XTX (no validation layers installed). 7.29 is now plain [RUN]. Found `PhysicalDeviceExtensions.FindSupportedFormat` for the depth-format search.

2026-10-18, TODO Priority 1.2: new `Sandbox/VulkanTexture` (off-screen) runs recipe 7.30: staging upload, layout barriers, sampler, descriptor set, sampling; 7 of 7 pixel checks passed first time on the RX 7900 XTX (orientation, nearest filter, UNorm 128 reads 128, SRGB 128 reads 55). One compile surprise: CS1686 for `&setLayout` inside a local function.

2026-10-18, TODO Priority 1.3 (splash recipe only; no `brutal-vulkan` template yet): new `Sandbox/VulkanSplash` (off-screen) runs recipe 7.31: official logo via Stb plus the credits line via SDL_ttf in one texture, letterboxed quad, fade; 6 of 6 pixel checks pass on the RX 7900 XTX and the written PNG was looked at. The one failure on the first run was a bug in the check itself (the clear colour counted as lit), not in the recipe.

2026-10-18, TODO Priority 1.4: new RecipeCheck recipe 7.32 (OpenAL). It passes on OpenAL Soft's `null` backend: device, context, PCM buffer, source Initial/Playing/Stopped, play position, looping, stop, source pooling, latched `InvalidName`. This machine had no usable default output: `OpenDevice` threw and OpenAL Soft's log (`ALSOFT_LOGLEVEL=3`) showed WASAPI `0x80070490`, both in the tool shell and in the user's own terminal, although Windows lists four sound devices and the Audio service runs. That is new trap T45. `ALSOFT_DRIVERS` is read once at library load, so the recipe re-runs itself in a child process with it set (an in-process `SetEnvironmentVariable` after an enumeration call didn't work). The real-device path is not run here.

2026-10-18, OpenAL follow-up: once Windows had an active default output again, recipe 7.32 passed on the real device (WASAPI, silent at gain 0) with no change, so both paths (real device and the null-backend child process) are now [RUN].

2026-10-18, TODO Priority 1.5 (recipe + test harness, no template): new `Sandbox/NetSession` runs recipe 7.33 with real processes. All checks passed first time and on two repeat runs: join, input round trip, host/client agreement on 14 shared ticks, 1-in-3 snapshot loss (raw view stalled on 83-94 of ~234 frames), crash detected in 1515-1548 ms at a 1.5 s timeout, version reject, full host. The first version of the smoothing counted frames since the last snapshot and stalled 19 of 236 frames (a late snapshot was treated as fresh); a clock pulled forward by snapshot ticks brought that to 0, and the recipe documents the difference.

2026-10-18, TODO Priority 1.6: new RecipeCheck recipe 7.34 (Jolt constraints, compound shapes, characters) and trap T46. Every feature checked by running it: hinge (with motor), point, distance, fixed, slider, cone and swing-twist constraints, static and mutable compound shapes, CharacterVirtual (stand, walk, step with and without ExtendedUpdate, wall, jump) and the body-based Character. The recipe also asserts that the documented gaps still throw EntryPointNotFoundException, so an upstream fix fails it (like T36). Three of my first probes were wrong rather than the API: a wheel overlapping its static anchor fought the hinge motor (0.8 rad instead of 2.0), the step in the character test was only 1 m long (the character climbed it and walked off), and a constraint removed from a sleeping body leaves it asleep. Passed on 4 runs.

2026-10-18, TODO Priority 1.7a: new RecipeCheck recipe 7.35 (ImGui on an SDL3 window with the shipped `OpenGlBackend` and a hand-written `SdlImGuiGlue`). Passed on the first run and on repeats: key-table spot checks, a synthetic SDL click on a button and a checkbox, a slider drag, `"hi"` + Backspace in an `InputText`, `WantCaptureMouse` / `WantCaptureKeyboard` true over the UI and false elsewhere, and 12,994 lit pixels read back (frame checked by eye). One check was broken on purpose and failed as expected. Not run: clipboard, cursor shapes, gamepad nav, multi-viewports, high-DPI, a real focused window. `OpenGlBackend` with `ViewportsEnable` off is fine. T22 reworded: only the *shipped* platform backend is GLFW-only.
