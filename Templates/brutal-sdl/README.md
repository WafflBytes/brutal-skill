# BrutalGame

A small 2D game built with [BRUTAL](https://github.com/ahwootech/BRUTAL), a C# game development framework by Ahwoo Limited. Move the square with the arrow keys or WASD and collect the dots. Text uses a TrueType font (SDL_ttf) and the game has sound effects (SDL_mixer); with no audio device it runs silently.

## Run

Requires the .NET 10 SDK on Windows x64.

```text
dotnet run -c Release
```

| Option | What it does |
|---|---|
| (none) | Play. The BRUTAL logo shows first; press Enter, Space or click to skip it after 1 s. P pauses; so does switching to another window |
| `--selftest` | Headless simulation checks. Exit code 0 = pass |
| `--snapshot <dir>` | Plays a scripted session off-screen and saves PNGs (no window needed). Exit code 0 = pass |
| `--smoke [seconds]` | Runs the real window loop for 4+ s, driven by synthetic SDL key and focus events (move, pause, resume, focus loss, resize), then exits. Exit code 0 = every check passed |
| `--crash-test` | Raises an error on purpose to show the crash dialog and write the error log |

## Publish

```text
dotnet publish -c Release -o publish
```

This produces a single self-contained `BrutalGame.exe` (font and sounds are embedded) plus the files beside it: `README.txt` (for players: controls, credits, a SmartScreen note), `NOTICE.txt`, `OFL-AtkinsonHyperlegible.txt` and `licenses/` (BRUTAL package notices and the .NET runtime's licence). Ship all of them, for example zipped together.

In the published game, an unhandled error shows a message box and is logged to `%LOCALAPPDATA%\BrutalGame\error.log`.

## Credits and licences

- **BRUTAL** game development framework is Copyright (c) 2024 Ahwoo Limited, used under the BRUTAL License v1.0 (`NOTICE.txt`).
- **Atkinson Hyperlegible** font, Copyright 2020 Braille Institute of America, Inc., licensed under the SIL Open Font License 1.1 (`OFL-AtkinsonHyperlegible.txt`).
- **Third-party libraries** inside the BRUTAL packages (SDL3, SDL_ttf with FreeType and HarfBuzz, SDL_mixer, stb): notices in `licenses/`, copied automatically from each referenced package at build time. Portions of this software are copyright © 2023 The FreeType Project (https://freetype.org). All rights reserved. This software is based in part on the work of the FreeType Team.
- **Sound effects** (`Assets/Sounds/*.wav`) were AI-generated for the `brutal-sdl` template, which is released under the MIT licence, and may be used freely.

If you replace the font, ship the new font's licence instead and update the `.csproj` and this list.
