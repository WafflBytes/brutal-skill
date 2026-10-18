# Audio: OpenAL, FMOD, SDL audio

Read this when choosing or wiring up audio. The simplest verified path is an SDL push-mode audio stream (`sdl.md`, 7.16).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| OpenAl | `Brutal.OpenAlApi` | OpenAL32.dll (Soft) | with `ALSOFT_DRIVERS=null` | Gen + OO facade | 7.32, T30, T45 |
| Fmod | `Brutal.FmodApi[.Studio]` | **none** (T21) | — | Gen | |

Also: SDL audio streams (raw `Sdl.PInvoke.*`, verified in `sdl.md` 7.16) and **Sdl.Mixer** (`sdl.md` 7.20, verified): WAV/OGG/FLAC/MP3 decoding, tracks, looping, gain, and an offline mixer for tests. For most games Sdl.Mixer is the easiest choice.

## OpenAL

### 7.32 OpenAL: device, buffer, source state machine [RUN]
Runnable copy: `RecipeCheck` recipe 7.32 (`Sandbox/RecipeCheck/Recipes.OpenAl.cs`). The package is `Ahwoo.BRUTAL.OpenAl` (note the spelling), namespace `Brutal.OpenAlApi`. **Verified:** every check passed on a real default playback device (WASAPI, `Gain = 0` so it is silent) and, separately, on OpenAL Soft's own `null` backend (what the recipe falls back to when there is no device, T45).
```csharp
using Brutal.OpenAlApi;
using var device = Alc.OpenDevice();          // default device; throws AlcException when there is none (T45)
using var context = device.CreateContext();
context.MakeCurrent();                        // required before any Al.* call
var buffer = Al.GenBuffer();
buffer.SetData(AlFormat.Mono16, MemoryMarshal.AsBytes(pcm16.AsSpan()), 44100);  // raw bytes, not shorts
Al.ThrowIfError();                            // errors latch; poll with Al.GetError() / Al.ThrowIfError()
var source = Al.GenSource();
source.Buffer = buffer;
source.Gain = 0.8f;                           // also Pitch, Looping, SourceRelative (true = 2D, no distance attenuation)
source.Play();                                // source.State is Playing at once; Stopped when a non-looping buffer ends
// ... source.SampleOffset advances; Looping = true keeps it Playing; Stop() => Stopped
source.Delete(); buffer.Delete();             // plain names: delete each exactly once, sources before their buffers
Context.MakeCurrent(null);                    // release before the context is disposed
```
- **Pool sources for effects:** `Stop()`, assign a different `Buffer`, `Play()` again [RUN]. A buffer can't be deleted while a source still has it.
- **Errors never throw** except for device/context lifetime calls: using a deleted source sets `AlError.InvalidName`; `Al.GetError()` returns it and clears it [RUN].
- **Fall back to silence** when `OpenDevice` throws: a game must still run on machines with no audio output (the eval's baseline 3D game wrapped the whole audio class in try/catch).
- **Licence (T30):** `OpenAL32.dll` lands beside the exe (dynamic linking, as required); the package ships `LICENSES/` (COPYING, BSD-3, pffft, fmt, gsl and `BRUTAL-OPENAL-THIRD-PARTY-NOTICES.txt`), which the licence-copy target in `shipping.md` copies to `licenses/` [SRC for OpenAL specifically]. Mention OpenAL Soft in the player README credits.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T21 | FMOD ships **no native binaries**. Needs FMOD Engine **2.03.08** (`fmod.dll`, `fmodstudio.dll`) beside the exe; `SetDllImportResolver` unsupported. Missing DLLs → `DllNotFound`/`TypeInitialization` at the first call | Supply them via `<Content … CopyToOutputDirectory>`; follow the FMOD EULA |
| T30 | OpenAL Soft is under the GNU **Library** GPL v2 (not LGPL 2.1) | Link dynamically (as shipped) and ship its `LICENSES/` |
| T45 | `Alc.OpenDevice()` throws `AlcException: alcOpenDevice failed` when Windows has no active default playback endpoint (OpenAL Soft's log shows WASAPI `0x80070490`), and `Alc.GetAllDeviceSpecifiers()` is empty. `ALSOFT_DRIVERS` (for example `null`) is read **once when the library first loads**, so `Environment.SetEnvironmentVariable` after any OpenAL call is too late. `ALSOFT_LOGLEVEL=3` prints the backend log | Catch the throw and run silent; for tests set `ALSOFT_DRIVERS=null` in the **process environment before the first OpenAL call** (recipe 7.32 re-runs itself in a child process to do that) |
