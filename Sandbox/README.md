# Sandbox: verification tools for the brutal skill

The knowledge base is the Claude Code skill in `.claude/skills/brutal/` (`SKILL.md` + `references/*.md`). Each tool here reproduces part of it, so its claims can be re-checked on any machine and after every package upgrade.

## Run everything

```powershell
powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1            # full run (needs a display and a Vulkan GPU)
powershell -ExecutionPolicy Bypass -File Sandbox/verify.ps1 -NoWindow -NoVulkan -NoDialog -NoAudio   # headless / CI
```

The script prints one PASS/FAIL/SKIP line per tool, and its exit code is the number of failures. `-NoDialog` makes `check-template.ps1` skip its two message-box checks (it still checks `error.log`). `-NoAudio` makes `SampleCheck.ps1` skip the samples that play sound. Anything skipped prints SKIP, never PASS.

## CI

`.github/workflows/verify.yml` runs on every push and pull request to `main` (and by hand from the Actions tab). Both jobs clone ahwootech/BRUTAL at the commit in `SKILL.md`'s "Verified against" line, so `lint-skill.ps1` checks it.

- **windows** (`windows-latest`): `verify.ps1 -NoWindow -NoVulkan -NoDialog -NoAudio`, failing on a non-zero exit code. Runners have no GPU, and an interactive desktop isn't guaranteed. A non-blocking probe step then runs `check-template.ps1` without flags, to show whether `--smoke` and the dialogs work there.
- **linux** (`ubuntu-latest`): builds `Sandbox.slnx` for `linux-x64` (the `BrutalRid` environment variable; locally `-p:BrutalRid=linux-x64`), then runs RecipeCheck `--no-window --no-vulkan`, NetSession, the headless samples (`SampleCheck.ps1 -NoWindow -NoVulkan -NoAudio`), AnalyzerDump, FixedProbe and the skill lint under `pwsh`. Not run there: Vulkan, ExportAudit (it audits the win-x64 DLLs, which the Windows job covers) and `check-template.ps1` (the template is win-x64 only).

Nothing in CI covers a display or a GPU: run the full `verify.ps1` on a desktop before a release.

## Tools

| Tool | Covers | Checks | Pass means |
|---|---|---|---|
| `RecipeCheck` | Recipes 7.1–7.8, 7.10, 7.11, 7.18–7.28, 7.32, 7.34–7.44; trap check T30 | Runs each recipe and asserts its result (Box2D/Jolt bodies settle, SPIR-V magic, Stb PNG round trip, T1 null image, GPU listed) | Exit 0. Filter with `-- 7.6` or `-- jolt`; `--no-window` uses GLFW's Null platform; `--no-vulkan` skips 7.11 |
| `VulkanWindow` | Recipe 7.13 | GLFW window + surface, swapchain, render pass, a triangle, a live resize with swapchain rebuild; reads the presented image back before and after | Exit 0. Opens a window for ~3 s; skipped by `verify.ps1 -NoWindow` or `-NoVulkan`. `-- --no-size-check` shows which resize signal the driver sends |
| `VulkanDepth` | Recipe 7.29 | Off-screen (no window): depth image, render pass with depth, vertex/index buffers, push-constant MVP matrices, culling; reads pixels back and repeats with the depth test off as a control | Exit 0. Needs a Vulkan GPU, no display; skipped by `verify.ps1 -NoVulkan` |
| `VulkanTexture` | Recipe 7.30 | Off-screen (no window): staging-buffer upload of RGBA pixels, layout barriers, sampler, descriptor set, fragment-shader sampling; reads pixels back (orientation, nearest filter, sRGB vs UNorm) | Exit 0. Needs a Vulkan GPU, no display; skipped by `verify.ps1 -NoVulkan` |
| `VulkanSplash` | Recipe 7.31 | Off-screen (no window): the official logo (Stb) and the credits line (SDL_ttf) composed into one texture, drawn as a letterboxed quad with a fade; reads pixels back and writes `splash-640x400.png` next to the exe for a look | Exit 0. Needs a Vulkan GPU and the `BRUTAL/` clone and `Templates/brutal-3d` fonts above the tool (exit 2 if missing); skipped by `verify.ps1 -NoVulkan` |
| `NetSession` | Recipe 7.33 | Host-authoritative online game over RakNet, tested with real processes on 127.0.0.1: the tool starts itself as host and client (bots, no window), reads their logs and checks join, input to snapshot round trip, host/client agreement, smoothing under 1-in-3 snapshot loss, crash detection, version mismatch and a full host | Exit = failed checks, about 30 s. Needs loopback UDP only, so it also runs in CI |
| `VulkanCompute` | Recipe 7.12 | Squares 1024 indices in a compute shader | Exit 0, `errors = 0`. Picks the discrete GPU; pass `-- <index>` for another one |
| `ExportAudit` | `references/export-gaps.md` | P/Invoke imports of all 50 packages vs. the win-x64 native export tables (natives come from the `.Native` companion packages) | Counts match `references/export-gaps.md`. `-- --detail` adds `missing: <package> <dll>!<entry>` lines; `-- --package jolt` limits it |
| `AnalyzerDump` | `references/core-libs.md` | Loads the packaged Roslyn analyzers, code fixes and generators | Lists 12 `BRUTALCORE` rules + `InteropHandleGenerator` |
| `FixedProbe` | `[Fixed]` in `references/core-libs.md` | `Fine.cs` patterns compile and run; `Errors.cs` (with `-p:ProbeErrors=true`) must fail | Default build passes; error build fails with exactly 0009 and 0010 |

Run one tool: `dotnet run -c Release --project Sandbox/<Name>`.

`check-template.ps1` (also run by `verify.ps1`) runs `dotnet new brutal-sdl` in a throwaway template hive (nothing is installed for you), then builds with warnings as errors, runs `--selftest`, `--snapshot` and `--smoke`, publishes a single-file exe, and runs it from an empty folder. Add `-Keep` to inspect the generated project.

`SampleCheck.ps1` (also run by `verify.ps1`) copies upstream's `BRUTAL/samples/Hello` and `Demo` files to a temp folder, rewrites their `#:package` versions to the ones in `ExportAudit.csproj` (`-Pinned` keeps upstream's), builds each with `dotnet build <file>.cs` and runs it. Headless samples must exit 0 within 120 s. Windowed ones run for 6 s, get a screenshot through `PrintWindow` (it must show at least two colours; saved under `%TEMP%\brutal-samplecheck\screenshots`), then a close request, and must exit 0. `HelloFmod` must throw `DllNotFoundException` (no FMOD natives); `HelloNativeFileDialog` and `HelloNotCurses` are skipped. A new upstream sample runs as headless until it's classified in the script's `$kinds` table. `-Only HelloJolt,DemoVulkan` runs a few.

`lint-skill.ps1` (also run by `verify.ps1`) checks the skill itself: one full entry per trap/bug ID, an index row in `references/traps.md` naming the right file, every reference file linked, the "next free IDs" note in `traps.md`, the export-gap list present, and a size budget for `SKILL.md`.

## After upgrading BRUTAL packages

`check-upgrades.ps1` reports every `Ahwoo.BRUTAL.*` version in `Sandbox/` and `Templates/` against nuget.org, and how many commits the BRUTAL mirror is past the verified commit. `-Apply` bumps every version in place (UTF-8, no BOM); then follow steps 3 and 4 below. Exit code 0 = everything current, 1 = updates available or applied.

1. `git -C BRUTAL pull`, then `git -C BRUTAL log --stat <verified commit>..HEAD` to see which packages' source changed (the verified commit is in the `SKILL.md` "Verified against" line). `dotnet package search Ahwoo.BRUTAL --take 100` lists the latest package versions.
2. Bump `ExportAudit/ExportAudit.csproj` (all 50 `PackageDownload` items, plus one per `.Native` companion; a package without its `.Native` shows `NOT FOUND`) and the `PackageReference`s in the other tools. `check-upgrades.ps1 -Apply` bumps both. New `.Native` packages: `dotnet package search Ahwoo.BRUTAL --take 100`.
3. Run `verify.ps1`, then compare ExportAudit's numbers with `references/export-gaps.md` and regenerate `references/export-gaps-detail.txt` (`references/maintaining.md`). Jolt changes most often (T25).
4. Update the "Verified against" line in `SKILL.md` (package versions and the `BRUTAL/` commit) and add a line to `VERIFICATION-LOG.md`. Until you do, `lint-skill.ps1` fails with a reminder that `BRUTAL/` has moved past the verified commit.

## Notes

- `Directory.Build.props` holds the shared settings: `net10.0`, unsafe code, `win-x64` (override with `-p:BrutalRid=<rid>`).
- ExportAudit and AnalyzerDump read the NuGet cache (`%NUGET_PACKAGES%` or `%USERPROFILE%\.nuget\packages`). Restoring ExportAudit downloads every package there.
- ExportAudit follows the Windows library renames done by package resolvers (`glfw`→`glfw3`, `openal`→`OpenAL32`, `shaderc`→`shaderc_shared`, `vulkan`→`vulkan-1` in System32). FMOD always shows as not found because it ships no binaries (T21).
- These tools were rebuilt on 2026-10-17 after the originals were lost. Older guide revisions mention `BrutalSmoke`, `VulkanProbe` and `AllPackages`; RecipeCheck and ExportAudit cover what they did.
