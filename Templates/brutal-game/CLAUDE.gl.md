# BrutalGame

A game on the BRUTAL C# framework, started from the `brutal-game` skeleton with an SDL3 window and OpenGL 4.1 core (`--renderer gl`). The plumbing works; the game is a placeholder cube you move around a floor. Replace it with yours.

**Use the `brutal` skill for every BRUTAL API.** BRUTAL isn't in model training data, and plausible API names are often wrong. Confirm names in the skill or in BRUTAL source before calling them, and run every new native call at least once.

## What to change

- `Sim/World.cs` and `Sim/Input.cs`: your game state, rules and input. Pure C#, deterministic, fixed 1/60 s step. **No SDL or GL types in `Sim/`** (skill rule R6), so it runs in the self-test. For 3D physics add `Ahwoo.BRUTAL.Jolt` and follow the skill's `jolt.md` (recipes 7.6, 7.27, 7.34, 7.44). Jolt also needs `msvcp140.dll` added to `NativeDialog.RequiredVcRuntimeDlls` (T41).
- `Platform/Gl/Renderer.cs`: `DrawScene` (camera, meshes, lighting) and `DrawHud`; `View` is what a frame needs from the world, interpolated between fixed steps. Keep `DrawSplash` and the pause overlay. Text: create a `GlText.Label` once in the constructor, call `Text(...)` every frame.
- `Platform/Gl/App.cs`: new keys in `OnKeyPressed`/`ReadInput`, sounds in `FixedStep`, and a scripted check in `SmokeScript` when you add input.
- `SelfTest.cs`: one check per rule you add. `Platform/Gl/SnapshotRun.cs`: the scripted shots, if your game needs other inputs to show its screens.
- Sounds: put the file in `Assets/Sounds/`, add an `Sfx` value (file name = lower-case value) in `Platform/Audio.cs`, play it from `App.FixedStep`. The self-test checks every `Sfx` decodes and is audible.
- `Assets/README.txt`: ships beside the exe for players. Update the description, controls and credits.

Leave the rest unopened unless you change that area. These files already work, and `check-template` verified them: `Program.cs` (modes, VC++ runtime check, crash dialog), `Sim/ScreenFlow.cs` (licence splash timing), `Platform/NativeDialog.cs`, `Resources.cs`, `Gl/GlWindow.cs` (GL context + GLAD), `Gl/GlUtil.cs` (shaders, meshes, textures), `Gl/GlText.cs` (SDL_ttf to GL textures), `Gl/Mat4.cs` (column-major GL maths), the `.csproj` (publish and licence targets), `Assets/NOTICE.txt` and `Assets/Fonts/OFL.txt`. Likewise skip the skill's `shipping.md` and `verification.md` unless you change publishing or the verify modes.

## Verify before calling work done

```text
dotnet build -c Release                                  # analyzer warnings are real; fix them
dotnet run -c Release -- --selftest                      # headless checks; exit code 0 = pass
dotnet run -c Release -- --snapshot snapshots            # hidden GL window -> PNGs (needs a desktop); look at them
dotnet run -c Release -- --smoke 4                       # real window, synthetic input events, scripted checks
dotnet publish -c Release -o publish                     # single-file exe; run it from an empty folder
```

Match the effort to the game: one self-test check per rule and one look at the snapshots is the bar. Drive scripts and timers by game time, never frame count: with vsync the loop runs at the monitor's refresh rate (T33). For an online game, remove pause-on-focus-loss in `App.PumpEvents` (the other player keeps playing).

## Licence (keep this working)

BRUTAL License v1.0 requires, in every distributed copy: the credits notice, the BRUTAL logo before gameplay, and `NOTICE.txt` beside the exe. `Renderer.DrawSplash` + `Sim/ScreenFlow.cs` do the first two (the self-test checks the timing), and the `.csproj` copies `NOTICE.txt`. Restyle them if you like, but don't remove them. The build also ships the font's OFL licence, every referenced BRUTAL package's third-party notices (`licenses/`) and the .NET runtime's notices. Keep `Assets/README.txt`'s credits current when you add fonts, sounds or packages.

## When you learn something about BRUTAL

If you find a new trap or a wrong API name, or you verify a call the skill doesn't cover, tell the user so it can be added to the brutal skill.
