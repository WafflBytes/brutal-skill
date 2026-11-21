# BrutalGame

A game on the BRUTAL C# framework, started from the `brutal-game` skeleton with a GLFW window and Vulkan 1.3 (`--renderer vulkan`). The plumbing works; the game is a placeholder cube you move around a floor. Replace it with yours. Players need a Vulkan driver, which every current GPU driver installs.

**Use the `brutal` skill for every BRUTAL API.** BRUTAL isn't in model training data, and plausible API names are often wrong. Confirm names in the skill or in BRUTAL source before calling them, and run every new native call at least once. For Vulkan details read only the sections of the skill's `vulkan.md` you need (search it for `^#`).

## What to change

- `Sim/World.cs` and `Sim/Input.cs`: your game state, rules and input. Pure C#, deterministic, fixed 1/60 s step. **No GLFW or Vulkan types in `Sim/`** (skill rule R6), so it runs in the self-test. For 3D physics add `Ahwoo.BRUTAL.Jolt` and follow the skill's `jolt.md`.
- `Platform/Vk/Renderer.cs`: `RecordScene` (camera, meshes, push constants) and `RecordHud`; `View` is what a frame needs from the world, interpolated between fixed steps. Shaders are GLSL strings at the end, compiled with ShaderC at start-up. New text or images: `AddTexture(name, VkText.Render(text, size))` once in the constructor, then `Text(...)` or `Quad(...)` per frame. Keep `RecordSplash` and the pause overlay.
- `Platform/Vk/App.cs`: new keys in `OnKey`/`ReadInput`, sounds in `FixedStep`, and a scripted check in `SmokeScript` when you add input (it posts real key messages to the window).
- `SelfTest.cs`: one check per rule you add. `Platform/Vk/SnapshotRun.cs`: the scripted shots, if your game needs other inputs to show its screens.
- Sounds: put the file in `Assets/Sounds/`, add an `Sfx` value (file name = lower-case value) in `Platform/Audio.cs`, play it from `App.FixedStep`.
- `Assets/README.txt`: ships beside the exe for players. Update the description, controls and credits.

Leave the rest unopened unless you change that area. These files already work, and `check-template` verified them: `Program.cs` (modes, VC++ runtime check, crash dialog), `Sim/ScreenFlow.cs` (licence splash timing), `Platform/NativeDialog.cs`, `Resources.cs`, `Vk/VkGpu.cs` (instance, device, buffers, texture upload, ShaderC), `Vk/VkText.cs` (SDL_ttf text and the splash image), the swapchain part of `Vk/App.cs` (skill recipe 7.13: resize, minimise, out-of-date), the `.csproj` (publish and licence targets), `Assets/NOTICE.txt` and `Assets/Fonts/OFL.txt`. Likewise skip the skill's `shipping.md` and `verification.md` unless you change publishing or the verify modes. Set `BRUTAL_VK_VALIDATION=1` to turn on the validation layers (needs the Vulkan SDK).

## Verify before calling work done

```text
dotnet build -c Release                                  # analyzer warnings are real; fix them
dotnet run -c Release -- --selftest                      # headless checks; exit code 0 = pass
dotnet run -c Release -- --snapshot snapshots            # off-screen Vulkan render -> PNGs (GPU, no window); look at them
dotnet run -c Release -- --smoke 4                       # real window, posted key messages, scripted checks
dotnet publish -c Release -o publish                     # single-file exe; run it from an empty folder
```

Match the effort to the game: one self-test check per rule and one look at the snapshots is the bar. Drive scripts and timers by game time, never frame count: the swapchain presents at the monitor's refresh rate (T33). For an online game, remove pause-on-focus-loss in the `OnFocus` handler (the other player keeps playing).

## Licence (keep this working)

BRUTAL License v1.0 requires, in every distributed copy: the credits notice, the BRUTAL logo before gameplay, and `NOTICE.txt` beside the exe. `Renderer.RecordSplash` + `VkText.Splash` + `Sim/ScreenFlow.cs` do the first two (the self-test checks the timing), and the `.csproj` copies `NOTICE.txt`. Restyle them if you like, but don't remove them. The build also ships the font's OFL licence, every referenced BRUTAL package's third-party notices (`licenses/`) and the .NET runtime's notices. Keep `Assets/README.txt`'s credits current when you add fonts, sounds or packages.

## When you learn something about BRUTAL

If you find a new trap or a wrong API name, or you verify a call the skill doesn't cover, tell the user so it can be added to the brutal skill.
