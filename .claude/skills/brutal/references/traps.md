# Trap and bug index

One line per known trap (`T#`) and confirmed framework bug (`B#`). The full entry, with its fix, is in the reference file named in the last column. Traps that apply to every library (T8, T10, T12, T13, T14, T16, T17) are in full in `SKILL.md`.

Scan this before calling an API you haven't used yet. New IDs: add the full entry to the library's reference file and a row here, then update "next free IDs" in `SKILL.md` and run `Sandbox/lint-skill.ps1`.

| ID | Summary | File |
|---|---|---|
| T1 | `Stb.Load` on a missing/bad file returns `Data == null`, no exception | assets |
| T2 | Vulkan `sType` is set only by `new T()`, not `default`/`stackalloc`/arrays | vulkan |
| T3 | Vulkan output spans typed `ReadOnlySpan` are still written | vulkan |
| T4 | Every failing `VkResult` throws `VkResultError`, including `ErrorOutOfDateKHR` | vulkan |
| T5 | Vulkan loader is System32 `vulkan-1.dll` only; touching `Vulkan.*` loads it | vulkan |
| T6 | Declared but not exported (e.g. `BoxShape.Create`) → `EntryPointNotFoundException` | physics |
| T7 | Jolt README uses `EMotionType`/`EActivation`, which don't exist | physics |
| T9 | Stock `glfw3.dll` fails; use the packaged fork | glfw-imgui |
| T11 | Some functions exist only in the raw `Sdl.PInvoke` / `Glfw.PInvoke` layer | sdl |
| T15 | Vsync over Remote Desktop: `RenderPresent` blocks ~160 ms | sdl |
| T18 | Jolt teardown out of order is a use-after-free | physics |
| T19 | Numerics ≠ System.Numerics (`Normalize(zero)` throws, masks, no `half`) | core-libs |
| T20 | `<None Include="Assets\x">` lands in `bin\…\Assets\` | assets |
| T21 | FMOD ships no native binaries | audio |
| T22 | ImGui's shipped platform backend is GLFW only (SDL glue: 7.35) | glfw-imgui |
| T23 | `Box2D.cs` loads `box2dd` under `#if DEBUG` | physics |
| T24 | `Platform` throws outside Windows/Linux/macOS | core-libs |
| T25 | Jolt is "highly experimental"; API changes between versions | physics |
| T26 | `LogCategory.Make` isn't thread-safe | core-libs |
| T27 | `NativeList<T>.Count` is an `ElementCount`; use `.Value` | core-libs |
| T28 | Slang: SPIR-V or source text only; no CPU/LLVM targets | vulkan |
| T29 | Monitor `Add*` throw on missing directories / second logger | core-libs |
| T30 | OpenAL Soft is under the GNU Library GPL v2 | audio |
| T31 | `foreach` over `NativeList<T>` allocates | core-libs |
| T32 | KTX2/Basis transcodes to BC7 by default | assets |
| T33 | With vsync the loop runs at the monitor's refresh rate (often 120–240 Hz), not 60 Hz | sdl |
| T34 | `RumbleGamepadTriggers` returns false on many connections | sdl |
| T35 | `Glad.Initialize` doesn't root the loader delegate; `glXxx` aliases are package-internal | opengl |
| T36 | Jolt mesh, convex-hull and height-field shapes can't be created (data dropped; issue #13) | physics |
| T37 | SDL_image has no WEBP/AVIF/JXL/TIFF; `IsWEBP` is false and loading throws | sdl |
| T38 | `SdlMixer.Generate` returns real-audio bytes, not bytes written | sdl |
| T39 | No font ships with Sdl.Ttf; bundle a redistributable one | sdl |
| T40 | `SdlMixer.PlayTrack` resets the loop count; set loops after it | sdl |
| T44 | RakNet only notices a vanished peer when a reliable message can't be delivered; set the same `SetTimeoutTime` on both ends | misc |
| T45 | `Alc.OpenDevice()` throws when there is no active default playback device; `ALSOFT_DRIVERS=null` must be set before the first OpenAL call | audio |
| T46 | Jolt constraints and characters work, but the casts, motor-settings getters, body locks, default filters and every character contact listener are unexported: create constraints from body IDs, down-cast with `Unsafe.BitCast`, use `XxxFilter.Create()` | physics |
| T43 | `floatQuat.CreateFromRotationMatrix` silently returns a non-unit quaternion for a mirrored (left-handed) basis | core-libs |
| T42 | Box2D contact, hit and sensor events are all off by default; sensors need the flag on both shapes | physics |
| T41 | Nearly every BRUTAL native DLL needs the VC++ runtime, which Windows doesn't include | shipping |
| B1 | `PtrOwner.Reset(new)` leaks the old memory | core-libs |
| B2 | `NativeList.Insert(ByteSize, byte)` never grows the size | core-libs |
| B3 | Explicit `INativeList.Capacity` gets/sets the size | core-libs |
| B4 | `RandomExtensions.Next*Unit3` clusters at the poles | core-libs |
| B5 | An exception in `IJob.Run` kills the process | core-libs |
| B6 | `LString.Format<TWriter>` copies the ref-struct writer | core-libs |
| B7 | ImGui Vulkan backend's out-of-date checks never run | glfw-imgui |
| B8 | `NativeList<T>.GetEnumerator` allocates | core-libs |
| B9 | `Mem` doesn't track memory, despite the conventions doc | core-libs |
