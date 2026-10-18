---
name: brutal
description: Verified reference for writing C# games and tools on the BRUTAL framework (Ahwoo / RocketWerkz; NuGet packages Ahwoo.BRUTAL.*, namespaces Brutal.*). Use this whenever code uses Brutal.* namespaces or Ahwoo.BRUTAL packages, or the user wants to start, build, debug, verify or ship a game on BRUTAL with its SDL3, OpenGL, Vulkan, GLFW, ImGui, Jolt, Box2D, ShaderC, Stb, OpenAL or RakNet bindings, even if they only say "my BRUTAL game" or name one of those libraries inside a BRUTAL project. BRUTAL isn't in training data and plausible API names are often wrong, so consult this before writing any BRUTAL call.
---

# BRUTAL framework

BRUTAL (Ahwoo / RocketWerkz) is a set of C# bindings and helpers for writing games on .NET 10. It's a toolkit, not an engine. Your training data doesn't cover it and its docs are thin, so plausible API names are often wrong: `EMotionType` is really `MotionType`, and `Glfw.GetVersionString()` is really the `Glfw.VersionString` property. Everything here was checked against the source or run.

**BRUTAL home** is the repository this skill ships in. Every `BRUTAL/`, `Sandbox/` and `Templates/` path in this skill is relative to it. To find it:
1. If the `BRUTAL_HOME` environment variable is set, use it.
2. Otherwise, take this skill folder's real path and go up three levels (`<home>/.claude/skills/brutal`). If the skill was installed by linking (for example `~/.claude/skills/brutal` as a junction or symlink), resolve the link first: `(Get-Item <skill folder>).Target` in PowerShell, `readlink -f` elsewhere.

What's in it:
- `BRUTAL/`: a clone of https://github.com/ahwootech/BRUTAL, the source of all 50 packages (no project files). Search it; don't edit it. If it's missing: `git clone https://github.com/ahwootech/BRUTAL BRUTAL` in BRUTAL home.
- `Templates/brutal-sdl/`: the starter game template (see "Starting a new game").
- `Sandbox/`: verification tools. `Sandbox/verify.ps1` runs them all (`references/verification.md`).
- `%NUGET%` = `%USERPROFILE%\.nuget\packages`.

**Update check (once a session):** in a git clone, run `git -C <home> ls-remote origin main`; if `git -C <home> cat-file -e <hash>^{commit}` fails, a newer skill exists: tell the user and offer `pull --ff-only`. Never pull unasked. Skip if offline.

**Verified against:** `Ahwoo.BRUTAL.*` 2026.10.0–2026.10.4 on .NET 10, with `BRUTAL/` source at commit `a25cdf8`. Windows x64 is fully verified. Linux x64 runs the headless recipes in CI and WSL2, but not Vulkan, windowed recipes or the templates; each recipe says where it ran. macOS: nothing run. Last full re-verification: 2026-10-18 (`references/verification.md`).

**Markers:** [RUN] = compiled and executed; [SRC] = checked in source, not executed; [DOC] = from a package README, unverified.

## How to use this skill

1. Follow the rules below; they prevent the failures that cost the most time.
2. **New game? Start from a template** ("Starting a new game") and read its generated `CLAUDE.md`. The template already does the publish settings, licence, splash, crash dialog and verify modes, so skip `shipping.md` and `verification.md` unless you change those.
3. Read the reference file only for each library you **add or change** (table below). Each holds that area's packages, C-to-BRUTAL names, source locations, verified recipes, traps and bugs. For a single name, search (lookup protocol) instead of reading a whole file. For one recipe, search the file for `^#` to get the section line numbers, then read its name-translation section, that recipe and its Traps table only.
4. Start from a [RUN] recipe rather than writing from memory.
5. Check `references/traps.md` before calling an API; read the full entry in the named file.
6. If something isn't covered, confirm it in source with the lookup protocol, run it once, then record it (`references/maintaining.md`).

## Rules

- **R1 Source over memory.** Confirm every API with `rg` in `BRUTAL/` (lookup protocol below) before calling it. The repo matches the published packages: entry-point counts are identical for Jolt, SDL, Box2D and GLFW.
- **R2 Compiling isn't working.** Bindings can compile and still throw `EntryPointNotFoundException`; 774 Jolt functions aren't exported (`references/export-gaps.md`). Run every new native call at least once.
- **R3 Verify by running** a headless self-test and/or a scripted `--snapshot` (`references/verification.md`). Silent failures (traps) don't show up when reading code.
- **R4** BRUTALCORE0009/0010 are **build errors**. Fix the memory source (stackalloc, native memory or `fixed`); never suppress them (`references/core-libs.md`).
- **R5 Own native memory.** Dispose or destroy everything from `Mem`, `PtrOwner<T>`, `NativeList<T>`, `Stb.Load` and Vulkan/Jolt/SDL `Create*` exactly once. Handles are value types, so copies alias (T17).
- **R6** Keep simulation free of SDL/GLFW/Vulkan types, so it tests headless and renderers stay swappable.
- **R7** Keep SDL/GLFW init, windows and event pumps on the main thread (T16).
- **R8** Use `u8` literals for strings going to native code (T14).
- **R9 Follow BRUTAL's conventions:** `var`, `readonly struct`, lowercase numerics, `ByteSize.Of<T>()`, `ElementCount`, stock exceptions only. Details in `references/core-libs.md` and `%NUGET%\ahwoo.brutal.core\<ver>\docs\brutal-conventions.md`.
- **R10 Licence.** A distributable isn't done until it shows `BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.`, shows the BRUTAL logo before gameplay (`BRUTAL/docs/branding/`), and ships `NOTICE.txt` (copy `BRUTAL/NOTICE.txt`) beside the exe. The templates do all three; full checklist in `references/shipping.md`.
- **R11 Scope.** BRUTAL has no editor, scene graph, renderer or gameplay framework, and won't get them. Build those yourself; don't search BRUTAL for them.

## Which reference file

| You're working on | Read |
|---|---|
| Pointers, `Mem`, `NativeList`, Numerics maths, logging, jobs, localization, crash monitor, any BRUTALCORE analyzer | `references/core-libs.md` |
| SDL3 window, 2D renderer, pixel buffer, input, gamepad, screenshots; images (Sdl.Image), TrueType text (Sdl.Ttf), sound and music (Sdl.Mixer) | `references/sdl.md` |
| OpenGL (GLAD) in an SDL window, FBOs, post-processing | `references/opengl.md` (plus `sdl.md`) |
| GLFW windows, Dear ImGui / ImPlot on GLFW, RenderDoc | `references/glfw-imgui.md` |
| Dear ImGui inside an SDL3 game (OpenGL or the SDL renderer) | `references/imgui-sdl.md` |
| Vulkan (instance, compute, swapchain), VMA, ShaderC, Slang | `references/vulkan.md` |
| Box2D (2D physics) | `references/box2d.md` |
| Jolt (3D physics) | `references/jolt.md` |
| Images (Stb), KTX/DDS textures, glTF, meshes, asset files | `references/assets.md` |
| OpenAL, FMOD, choosing an audio path | `references/audio.md` |
| RakNet networking, NotCurses terminal UI, native file dialogs | `references/misc.md` |
| Any binding you haven't seen run (export gaps) | `references/export-gaps.md`; to check one name, search `references/export-gaps-detail.txt` |
| Publishing a single-file exe, the licence (not needed when the template's publish is unchanged) | `references/shipping.md` |
| Self-tests, snapshots, Sandbox tools, upgrading packages (beyond the template's own checks) | `references/verification.md` |
| Every known trap and bug, one line each, plus a symptom lookup | `references/traps.md` |
| Recording a new trap, recipe or verification (only after you found one) | `references/maintaining.md` |

Common usings for recipes: `Brutal`, `Brutal.Numerics`, `Brutal.Logging`, plus the library namespace. Recipe numbers (`7.NN`) are stable IDs: to find one, search `references/` for `### 7.NN`.

## Quick facts

| | |
|---|---|
| TFM | `net10.0` only. Sources use C# 14 `extension` blocks, `allows ref struct`, `ref` fields, generic math |
| Packages | 50 × `Ahwoo.BRUTAL.<Name>` on **nuget.org**, restored anonymously. List them with `dotnet package search Ahwoo.BRUTAL --take 100` |
| Versions | 2026.10.0–2026.10.4; each package is versioned independently |
| Native RIDs | `win-x64`, `linux-x64`, `osx-arm64` (`runtimes/<rid>/native/`). win-x64 fully verified; linux-x64 headless recipes only; osx-arm64 not run |
| Namespaces | `Brutal`, `Brutal.Pointers`, `Brutal.<Lib>` or `Brutal.<Lib>Api` (`Brutal.SdlApi`, `Brutal.VulkanApi`, `Brutal.JoltApi`) |
| Help and bugs | Questions: [BRUTAL forums](https://forums.ahwoo.com/forums/brutal/). Bugs: GitHub issues; how to report is in `references/maintaining.md` |

Minimal project [RUN]. Setting `RuntimeIdentifier` puts the native DLLs beside the exe.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks><RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ahwoo.BRUTAL.Core" Version="2026.10.1" />
    <PackageReference Include="Ahwoo.BRUTAL.Logging" Version="2026.10.2" />
    <PackageReference Include="Ahwoo.BRUTAL.Numerics" Version="2026.10.1" />
  </ItemGroup>
</Project>
```

## Architecture for a new game

- **Layering:** pure-C# simulation (Numerics + Collections) → `Render(Span<uint>)` or vertex building → backend (SDL now, Vulkan later). The backend owns all native handles.
- **Timestep:** fixed 1/60 s with an accumulator; cap at about 4 steps per frame. With vsync the loop runs at the monitor's rate, not 60 Hz (T33).
- **Ownership:** one owner per native handle; dispose in reverse creation order.
- **Input:** paint along a line from the previous to the current mouse position; convert window coordinates with `RenderCoordinatesFromWindow`.
- **Renderer choice:**
  - SDL renderer: pixel/2D games; runs anywhere, including remote sessions.
  - OpenGL in an SDL window: simple 3D, verified end to end.
  - ImGui (GLFW + OpenGL/Vulkan): debug UI.
  - Vulkan + Abstractions + VMA: 3D and high performance.
  - Vulkan compute: GPU compute.
  - NotCurses: terminal.
- **Physics:** Box2D for 2D; Jolt for 3D, using only the exported subset.
- **Large worlds:** keep positions in `double3`/`double4x4` and convert with `float3.Pack(in double3, …)` for rendering. There's no floating-origin helper.

## Starting a new game

Start from a template rather than an empty project. Both are small playable games with the licence (R10), every third-party notice, a crash dialog, a VC++ runtime check, a deterministic fixed-step `Sim/`, `--selftest`, `--snapshot`, `--smoke` and single-file publish already working.

| Template | For | Stack |
|---|---|---|
| `Templates/brutal-sdl` | 2D games | SDL3 renderer, SDL_ttf, SDL_mixer, Stb |
| `Templates/brutal-3d` | 3D games | SDL3 window + OpenGL 4.1 core, Jolt physics (boxes and spheres; rotated boxes for ramps, see T36), chase camera, TrueType text and the logo as GL textures, SDL_mixer, gamepad |

```text
dotnet new list brutal                                     # already installed? then skip the installs
dotnet new install <BRUTAL home>/Templates/brutal-sdl      # once per machine, per template you use
dotnet new install <BRUTAL home>/Templates/brutal-3d
dotnet new brutal-sdl -n MyGame -o <folder>/MyGame         # or: dotnet new brutal-3d ...
```

Then replace `Sim/` with your game (keep it free of SDL and GL types) and add a self-test check for each rule. The generated `CLAUDE.md` explains the layout and the verify commands. For an online game, remove the template's pause-on-focus-loss (the other player keeps playing) and test with two processes on 127.0.0.1 (`references/misc.md`).

## Name translation (C/C++ → BRUTAL)

General rules; library-specific translation is in each reference file.

| C | BRUTAL |
|---|---|
| `uint32_t` count/index | `int` |
| `VkDeviceSize` (in structs) | `ByteSize64` |
| `VkBool32` / C bool | `Bool32` / `bool` |
| `pFoo` field | `Foo`; `ppFoo` keeps its name (`ppEnabledExtensionNames`) |
| `char x[256]` | `fixed byte X[256]` |
| `VK_SHADER_STAGE_COMPUTE_BIT` | `VkShaderStageFlags.ComputeBit` (prefix stripped, PascalCase) |
| Enum value starting with a digit | `_` prefix: `VkImageViewType._2D`, `VkSampleCountFlags._1Bit` |
| `b2Vec2`, `Vec3`, `Quat` | `float2`, `float3`, `floatQuat` |
| Opaque `Foo*` | `readonly struct Foo` with `IsNull()` |

Handle structs in the source look empty because `[InteropHandle]` generates their members (`core-libs.md`).

## Lookup protocol

Search with your Grep tool (or `rg` from BRUTAL home). Pass the pattern as written; don't route regexes through PowerShell strings, which can mangle `\b` and `\s`. Search, then read only the lines you need, never a whole `*.gen.cs` file (some are 25k+ lines).

| Question | Pattern | Path |
|---|---|---|
| Does it exist? Exact signature | `public static .*\bCreateTexture\(` | `BRUTAL/Brutal.Sdl` |
| Struct fields (Vulkan: one file per struct) | `^\s+public [^(]+;` | `BRUTAL/Brutal.Vulkan/Generated/Structs/VkWriteDescriptorSet.gen.cs` |
| SDL struct (all in one file; show ~6 lines after) | `struct SdlVertex\b` | `BRUTAL/Brutal.Sdl/Generated/Sdl.Structs.gen.cs` |
| Enum members | `^\s+\w+ = ` | `BRUTAL/Brutal.Jolt/Generated/Enums/MotionType.gen.cs` |
| Namespace | `^namespace` | `BRUTAL/Brutal.Core/Pointers/PointerExtensions.cs` |
| Exported by the native DLL? (R2) A hit = **missing** | `BoxShape_Create` | `.claude/skills/brutal/references/export-gaps-detail.txt` |

Sources, in priority order:
1. **`BRUTAL/Brutal.<Lib>/`**. `Generated/` holds the bindings; `Manual/` holds hand-written ergonomics. Generated doc comments link to the upstream C header line.
2. **Package READMEs** (not in git): `%NUGET%\ahwoo.brutal.<name>\<ver>\README.md`. Useful ones: sdl (handle and threading rules, satellite libraries), jolt (init and shutdown order), monitor (options), openal, slang, fmod. Some are stale (T7, T8).
3. **`brutal-conventions.md`** inside the Core package (summarised in `references/core-libs.md`).
4. **The compiled package**: `lib\net10.0\*.dll`, `runtimes\win-x64\native\`, `analyzers\dotnet\cs\`.

## Traps

**Before calling an API, check `references/traps.md`**: one line for every known trap (`T#`) and framework bug (`B#`), naming the reference file that holds the full entry and its fix. The ones below apply to every library, so they're here in full:

| ID | Trap | Fix |
|---|---|---|
| T8 | Package READMEs say a GitHub Packages PAT is required | Stale: `Ahwoo.BRUTAL.*` restore anonymously from nuget.org (only internal `RocketWerkz.*` packages need the feed) |
| T10 | Mixed error styles: `Glfw.Init()` returns bool; SDL `Init`/`Create*`, `SdlImage`/`SdlTtf`/`SdlMixer` loaders and Vulkan throw; `Sdl.Render.*` setters return bool; SDL `Get*` returns null handles; `Stb.Load` returns a null image (T1) | Check every return type |
| T12 | Extensions live in separate namespaces: `AsPtr` in `Brutal.Pointers.Extensions`, Vulkan helpers in `Brutal.VulkanApi.Abstractions`, ImGui helpers in `Brutal.ImGuiApi.Extensions` | Search `^namespace` in the file |
| T13 | BRUTALCORE0009/0010 errors on pointers into managed arrays | stackalloc, native memory, or `fixed` (`references/core-libs.md`) |
| T14 | BRUTALCORE0011 warns on `string` where a UTF-8 overload exists (ShaderC, Strings) | `"…"u8`; raw string literals take `u8` too |
| T16 | SDL/GLFW are main-thread only, with no guard (undefined behaviour) | Keep init, windows and pumps on the main thread |
| T17 | Handles are values: copies alias. Disposing a borrowed handle (SDL `Get*`, Jolt `GetBodyInterface()`) is a double free. Never dispose Jolt's `Factory` | Dispose only what you `Create`/`Open`, once. For Jolt use `Factory.DestroyInstance()` |

## Verification

"It compiles" proves little here (R2, R3). **Done means:**
1. Build with analyzer warnings fixed.
2. A headless `--selftest` with one check per game rule you added.
3. One `--snapshot` run, and look at the images.
4. Every new native call has run at least once.
5. If it's shared: one publish, run from an empty folder.

Match the effort to the task. Don't build extra harnesses (multi-window bots, thousands of state comparisons) unless the user asks or the feature is networked, and then reuse recipe 7.33's two-process pattern (`misc.md`) rather than writing your own. The template commands are in its `CLAUDE.md`; the Sandbox tools are in `references/verification.md`.

## Not in the repo (don't search for it)

Project, solution and build files; generators (`Brutal.*.Generate`) and T4 templates; analyzer and interop-generator source (behaviour in `references/core-libs.md`); tests, samples and per-package guides; source for `Render.Common`/`Render.Debug` and `Brutal.Monitor.Subprocess`; any C# API for FreeType or ZLib (native-only).

## Keeping this skill current

Found a new trap, a wrong name, or verified something not covered? Record it so the next session doesn't rediscover it: follow `references/maintaining.md` (read it only then).
