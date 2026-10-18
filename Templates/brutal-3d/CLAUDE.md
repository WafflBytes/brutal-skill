# BrutalGame3D

A 3D ball-rolling coin hunt built on the BRUTAL C# framework (SDL3 window, OpenGL 4.1 core via GLAD, Jolt physics, SDL_ttf text, SDL_mixer sound, Stb, Numerics), started from the `brutal-3d` template. Replace the level and rules in `Sim/` with your game; keep `Sim/` free of SDL and GL types.

**Use the `brutal` skill for every BRUTAL API.** BRUTAL isn't in model training data, and plausible API names are often wrong. Confirm names in the skill or in BRUTAL source before calling them, and run every new native call at least once.

## What to read (saves tokens)

The Layout list below says what every file does; you don't need to open a file to know that. To build your game, read only:
- `Sim/World.cs`, `Sim/Level.cs` and `Sim/Input.cs`: you replace these. `Sim/Bot.cs` only if the self-test's autopilot must learn your level; `Sim/Physics.cs` only to add shape kinds.
- `Platform/Renderer.cs` (drawing and HUD) and, for new keys or sounds, the input and `FixedStep` parts of `Platform/App.cs` (search for them; both files are large).
- `SelfTest.cs`, to add a check per rule.

Leave the rest unopened unless you change that area: `Program.cs`, `Platform/GlWindow.cs`, `GlUtil.cs`, `GlText.cs`, `Mat4.cs`, `Audio.cs`, `SnapshotRun.cs`, `NativeDialog.cs`, `Resources.cs`, the `.csproj` (publish and licence targets), `Assets/Fonts/OFL.txt`, `Assets/NOTICE.txt` and `README.md`. They already work and `check-template` verified them. Likewise skip the skill's `shipping.md` and `verification.md` unless you change publishing or the verify modes.

## Layout

- `Sim/`: the game itself. Pure C# plus Jolt (headless), fixed 1/60 s step. **No SDL or GL types here** (skill rule R6), so it runs in the self-test.
  - `Level.cs`: static boxes (ramps are rotated boxes: Jolt mesh shapes don't work, skill T36), coins, the autopilot route, CPU ray casts for coin placement and the shadow.
  - `Physics.cs`: `JoltRuntime` (process-wide init; `Shutdown()` runs at exit in `Program.cs`) and `PhysicsScene` (one Jolt system; teardown order per T18).
  - `World.cs`: rules, timer, chase camera, per-step events for sounds. `Bot.cs`: autopilot used by the self-test and `--snapshot`. `ScreenFlow.cs`: splash then play.
- `Program.cs`: command-line modes, VC++ runtime check (T41), crash handler, Jolt shutdown.
- `Platform/`: `App.cs` (window, events, fixed-step loop with render interpolation, keyboard/gamepad input, pause, pacing, sounds, `--smoke` script), `GlWindow.cs` (SDL GL context + GLAD), `Renderer.cs` (scene, HUD, shaders), `GlUtil.cs` (meshes, shaders, textures), `GlText.cs` (SDL_ttf -> GL textures), `Mat4.cs` (column-major GL maths), `Audio.cs`, `SnapshotRun.cs` (hidden window + framebuffer -> PNG), `NativeDialog.cs`, `Resources.cs`.
- `SelfTest.cs`: headless checks. Add one for every rule you add to `Sim/`.
- `Assets/`: `logo.png`, font, `Sounds/*.wav` (embedded), `NOTICE.txt`, `README.txt` for players.
  - To add a sound: put the WAV in `Assets/Sounds/`, add a value to `Sfx` (file name = lower-case value), play it from an event in `App.FixedStep`.

## Verify before calling work done

```text
dotnet build -c Release                                  # analyzer warnings are real; fix them
dotnet run -c Release -- --selftest                      # headless checks; exit code 0 = pass
dotnet run -c Release -- --snapshot snapshots            # off-screen PNGs; look at them
dotnet run -c Release -- --smoke 5                       # real window, synthetic input events, 9 scripted checks
dotnet publish -c Release -o ../dist                     # single-file exe; run it from an empty folder
```

Drive scripts and timers by game time, never frame count: with vsync the loop runs at the monitor's refresh rate.

## Licence (keep this working)

BRUTAL License v1.0 requires, in every distributed copy: the credits notice, the BRUTAL logo before gameplay, and `NOTICE.txt` beside the exe. The splash in `Platform/Renderer.cs` + `Sim/ScreenFlow.cs` does the first two (the self-test checks its timing), and the `.csproj` copies `NOTICE.txt`. Restyle them if you like, but don't remove them. Details are in the skill's `references/shipping.md`.

Other licences the build already handles: the font's SIL OFL text ships as `OFL-AtkinsonHyperlegible.txt`, the `IncludeBrutalThirdPartyNotices` target copies every referenced BRUTAL package's third-party notices (SDL, FreeType, HarfBuzz, stb, ...) to `licenses/`, and `IncludeDotnetRuntimeNotices` adds the bundled .NET runtime's MIT licence and notices on publish. Keep the README's credits list current when you add fonts, sounds or packages.

## When you learn something about BRUTAL

If you find a new trap, a wrong API name, or verify a call the skill doesn't cover, record it in the **brutal skill** (follow its `references/maintaining.md`), not only here. That way every future project benefits.
