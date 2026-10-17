# Audio: OpenAL, FMOD, SDL audio

Read this when choosing or wiring up audio. The simplest verified path is an SDL push-mode audio stream (`sdl.md`, 7.16).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| OpenAl | `Brutal.OpenAlApi` | OpenAL32.dll (Soft) | no | Gen + OO facade | T30 |
| Fmod | `Brutal.FmodApi[.Studio]` | **none** (T21) | — | Gen | |

Also: SDL audio streams (raw `Sdl.PInvoke.*`, verified in `sdl.md` 7.16) and **Sdl.Mixer** (`sdl.md` 7.20, verified): WAV/OGG/FLAC/MP3 decoding, tracks, looping, gain, and an offline mixer for tests. For most games Sdl.Mixer is the easiest choice.

## OpenAL

**OpenAL** [DOC]. Most errors latch, so poll for them.
```csharp
using var dev = Alc.OpenDevice();
using var ctx = dev.CreateContext();
ctx.MakeCurrent();
var buf = Al.GenBuffer();
buf.SetData(AlFormat.Mono16, pcm, sampleRate: 44100);
var src = Al.GenSource();
src.Buffer = buf;
src.Play();
```

## Traps

| ID | Trap | Fix |
|---|---|---|
| T21 | FMOD ships **no native binaries**. Needs FMOD Engine **2.03.08** (`fmod.dll`, `fmodstudio.dll`) beside the exe; `SetDllImportResolver` unsupported. Missing DLLs → `DllNotFound`/`TypeInitialization` at the first call | Supply them via `<Content … CopyToOutputDirectory>`; follow the FMOD EULA |
| T30 | OpenAL Soft is under the GNU **Library** GPL v2 (not LGPL 2.1) | Link dynamically (as shipped) and ship its `LICENSES/` |
