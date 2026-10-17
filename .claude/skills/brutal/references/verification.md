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
| `RecipeCheck` | Runs 7.1–7.8, 7.10 (Stb round trip + T1), 7.11, 7.18–7.20 (SDL_image, SDL_ttf, SDL_mixer; T37–T40) and 7.21 (ImGui; `RECIPECHECK_SHOTS=<dir>` saves its frame), asserting each result, plus trap check `T36` (passes while the trap holds, fails when it's fixed upstream). Filter with `-- 7.6`; `--no-window`, `--no-vulkan` |
| `VulkanCompute` | Runs 7.12; exit code 0 means correct results. `-- <index>` picks a GPU |
| `ExportAudit` | Export gaps (`export-gaps.md`) for all 50 packages (`--detail`, `--package <name>`). Restoring it downloads every package |
| `AnalyzerDump` | Lists every analyzer rule, code fix and generator (`core-libs.md`) |
| `FixedProbe` | `[Fixed]` cases: builds and runs by default; `-p:ProbeErrors=true` must fail with exactly 0009 and 0010 |
| `check-template.ps1` | `dotnet new brutal-sdl` in a throwaway template hive, then build with warnings as errors, `--selftest`, `--snapshot`, `--smoke` (skipped with `-NoWindow`), single-file publish (checks `NOTICE.txt`, the font licence and `licenses/`), and the published exe run from an empty folder |

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

When you verify something new, add a line here and change its marker to [RUN].
