# BrutalGame

A 2D game built on the BRUTAL C# framework (SDL3 renderer, SDL_ttf text, SDL_mixer sound, Stb, Numerics), started from the `brutal-sdl` template.

**Use the `brutal` skill for every BRUTAL API.** BRUTAL isn't in model training data, and plausible API names are often wrong. Confirm names in the skill or in BRUTAL source before calling them, and run every new native call at least once.

## Layout

- `Sim/`: the game itself. Pure C#, deterministic, fixed 1/60 s step. **No SDL types here** (skill rule R6), so it runs in the self-test.
  - `World.cs`: game state and rules. `ScreenFlow.cs`: splash -> play. `Input.cs`: per-step input. `Rng.cs`: deterministic RNG.
- `Program.cs`: command-line modes, a start-up check for the VC++ runtime that BRUTAL's native DLLs need (skill trap T41), and the crash handler for play mode (dialog + `%LOCALAPPDATA%\BrutalGame\error.log`; the published exe has no console). `Platform/NativeDialog.cs` shows those dialogs with Win32, not SDL, so they work even when SDL can't load.
- `Platform/`: everything that touches SDL. `App.cs`: window, events, fixed-step loop, pause (P, and automatically on focus loss), frame pacing, plays sounds from `World` events. Input comes only from SDL events (held keys tracked from KeyDown/KeyUp), so `--smoke` drives the real input path with synthetic events; add new keys in `OnKeyPressed`/`ReadInput` and a scripted check in `SmokeScript`. `Gfx.cs`: drawing. `TextRenderer.cs`: TrueType labels that stay sharp at any window size. `Audio.cs`: SDL_mixer sound effects; silent when there's no audio device. `Resources.cs`: embedded assets. `SnapshotRun.cs`: off-screen scripted run. `Screenshot.cs`: PNG capture.
- `SelfTest.cs`: headless checks. Add one for every rule you add to `Sim/`.
- `Assets/README.txt`: ships beside the exe for players (controls, SmartScreen note, credits including the FreeType credit). Update it when controls or credits change.
- `Assets/`: `logo.png`, `Fonts/` (Atkinson Hyperlegible, SIL OFL) and `Sounds/*.wav`, all embedded in the exe, plus `NOTICE.txt` and the font's `OFL.txt` (copied beside it).
  - To add a sound: put the WAV/OGG/FLAC/MP3 in `Assets/Sounds/`, add a value to `Sfx` (the file name is the lower-case value), and play it from an event in `App.FixedStep`. The self-test checks every `Sfx` decodes and is audible.
  - Text: create a `TextRenderer.Label` once per piece of text (in `Gfx`), then call `Draw` every frame.

## Verify before calling work done

```text
dotnet build -c Release                                  # analyzer warnings are real; fix them
dotnet run -c Release -- --selftest                      # headless checks; exit code 0 = pass
dotnet run -c Release -- --snapshot snapshots            # off-screen PNGs; look at them
dotnet run -c Release -- --smoke 4                       # real window, synthetic input events, 5 scripted checks
dotnet publish -c Release -o publish                     # single-file exe; run it from an empty folder
```

Drive scripts and timers by game time, never frame count: with vsync the loop runs at the monitor's refresh rate.

## Licence (keep this working)

BRUTAL License v1.0 requires, in every distributed copy: the credits notice, the BRUTAL logo before gameplay, and `NOTICE.txt` beside the exe. The splash in `Platform/Gfx.cs` + `Sim/ScreenFlow.cs` does the first two (the self-test checks its timing), and the `.csproj` copies `NOTICE.txt`. Restyle them if you like, but don't remove them. Details are in the skill's `references/shipping.md`.

Other licences the build already handles: the font's SIL OFL text ships as `OFL-AtkinsonHyperlegible.txt`, the `IncludeBrutalThirdPartyNotices` target copies every referenced BRUTAL package's third-party notices (SDL, FreeType, HarfBuzz, stb, ...) to `licenses/`, and `IncludeDotnetRuntimeNotices` adds the bundled .NET runtime's MIT licence and notices on publish. Keep the README's credits list current when you add fonts, sounds or packages.

## When you learn something about BRUTAL

If you find a new trap, a wrong API name, or verify a call the skill doesn't cover, record it in the **brutal skill** (follow "Keeping this skill current" in its `SKILL.md`), not only here. That way every future project benefits.
