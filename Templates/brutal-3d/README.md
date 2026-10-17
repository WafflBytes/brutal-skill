# BrutalGame3D

A small 3D game built with [BRUTAL](https://github.com/ahwootech/BRUTAL), a C# game development framework by Ahwoo Limited. Roll a ball around an arena with walls, ramps and raised platforms and collect all 13 coins before the 60-second timer runs out.

- **Physics:** Jolt (Ahwoo.BRUTAL.Jolt). The level is static boxes (floor, walls, platforms, ramps as rotated boxes); the ball is a dynamic sphere with continuous collision.
- **Rendering:** OpenGL 4.1 core in an SDL3 window (Ahwoo.BRUTAL.OpenGl + Sdl): sun + sky/ground ambient + specular lighting, fog, a blob shadow that follows the surface under the ball, 4x MSAA when available.
- **Camera:** chase camera behind and above the ball; Q/E (or the right stick) turn it, and movement is relative to it.
- **HUD:** coins and time, end-of-round and pause screens, TrueType text (SDL_ttf) uploaded as GL textures.
- **Sound:** SDL_mixer: coin, bump (volume follows the impact), countdown ticks in the last 5 s, win, time-up, start, restart. With no audio device it runs silently.
- **Input:** keyboard or gamepad.

## Run

Requires the .NET 10 SDK on Windows x64.

```text
dotnet run -c Release
```

| Option | What it does |
|---|---|
| (none) | Play. The BRUTAL logo shows first; press Enter, Space or click to skip it after 1 s. P pauses; so does switching to another window |
| `--selftest` | 15 headless checks on the real Jolt simulation (rolling, walls, ramps, a full autopilot run of every coin, timer, restart, determinism, camera, maths, font, sounds). Exit code 0 = pass |
| `--snapshot <dir>` | Autopilot plays a round; frames are rendered off-screen (hidden GL window + framebuffer) to PNGs. Exit code 0 = pass |
| `--smoke [seconds]` | Runs the real window loop for 5+ s, driven by synthetic SDL events (roll, turn camera, resize, pause, resume, focus loss), then exits. Exit code 0 = every check passed |
| `--crash-test` | Raises an error on purpose to show the crash dialog and write the error log |
| `--vcruntime-test` | Shows the dialog players get when the Microsoft VC++ runtime is missing (checked at every start) |

## Layout

- `Sim/`: the game, no SDL or GL types. `Level.cs` (boxes, coins, the autopilot route, ray casts), `Physics.cs` (Jolt setup and teardown), `World.cs` (rules, timer, camera, events for sounds), `Bot.cs` (autopilot), `ScreenFlow.cs` (logo splash).
- `Platform/`: `App.cs` (window, loop, input, pause, pacing, sounds), `GlWindow.cs`, `Renderer.cs` (scene + HUD + shaders), `GlUtil.cs` (meshes, shaders, textures), `GlText.cs`, `Mat4.cs`, `Audio.cs`, `SnapshotRun.cs`.

## Publish

```text
dotnet publish -c Release -o ../dist
```

This produces a single self-contained `BrutalGame3D.exe` (font and sounds are embedded) plus `README.txt` (for players), `NOTICE.txt`, `OFL-AtkinsonHyperlegible.txt` and `licenses/`. Ship all of them, for example zipped together.

In the published game, an unhandled error shows a message box and is logged to `%LOCALAPPDATA%\BrutalGame3D\error.log`.

## Credits and licences

- **BRUTAL** game development framework is Copyright (c) 2024 Ahwoo Limited, used under the BRUTAL License v1.0 (`NOTICE.txt`).
- **Jolt Physics**, **SDL3**, **SDL_ttf** (FreeType, HarfBuzz), **SDL_mixer**, **GLAD**, **stb**: notices in `licenses/`, copied automatically from each referenced BRUTAL package. Portions of this software are copyright (c) 2023 The FreeType Project (https://freetype.org). All rights reserved. This software is based in part on the work of the FreeType Team.
- **Atkinson Hyperlegible** font, Copyright 2020 Braille Institute of America, Inc., SIL Open Font License 1.1 (`OFL-AtkinsonHyperlegible.txt`).
- **Sound effects** (`Assets/Sounds/*.wav`) were generated for the BRUTAL starter templates, which are released under the MIT licence, and may be used freely.
