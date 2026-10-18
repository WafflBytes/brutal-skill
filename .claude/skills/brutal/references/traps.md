# Trap and bug index

One line per known trap (`T#`) and confirmed framework bug (`B#`). The full entry, with its fix, is in the reference file named in the last column. Traps that apply to every library (T8, T10, T12, T13, T14, T16, T17) are in full in `SKILL.md`.

Scan this before calling an API you haven't used yet. Debugging? Start from the symptom table. Adding an ID: `maintaining.md` (next free IDs: T53, B11).

## By symptom

| Symptom | Look at |
|---|---|
| `EntryPointNotFoundException` | T6, T36, T46; search `export-gaps-detail.txt` for the entry point |
| "Unable to load DLL ... or one of its dependencies", often only on another PC | T41 (VC++ runtime), T5 (Vulkan loader), T9 (GLFW), T21 (FMOD) |
| BRUTALCORE0009/0010 build error, 0011 warning | T13, T14 |
| A type or member from a README or from memory doesn't exist | T7, T8, T11, T12, T27; then the lookup protocol in `SKILL.md` |
| A call "succeeds" (null, false, no exception) but nothing happens | T1, T10, T38, T42, T47, T48, T52 |
| `VkResultError`, or Vulkan structs that look ignored | T4, T2, T3 |
| Game or scripted test runs 2-3x too fast | T33 |
| About 6 FPS over Remote Desktop | T15 |
| Crash, `AccessViolationException` or corruption, often at shutdown | T17, T18, T16, B1 |
| Jolt shape creation throws `JoltException` | T36 |
| A Jolt motor drives a body through its limits | T51 |
| No sound, or OpenAL throws when opening the device | T45, T40, T38 |
| An image or texture loads in one format but not another | T37, T32, B10 |
| `dotnet publish` fails with NETSDK1152 | T50 |
| The published exe writes no crash dumps | T52 |
| The process dies from an exception in a job | B5 |
| ImGui shortcuts or draw callbacks don't work | T49, T47 |
| Numerics results differ from System.Numerics, or a quaternion is wrong | T19, T43 |
| Unexpected allocations in a hot loop | T31, B8 |

## All IDs

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
| T29 | Monitor `Add*` throw on missing directories / second logger / bad name | core-libs |
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
| T41 | Nearly every BRUTAL native DLL needs the VC++ runtime, which Windows doesn't include | shipping |
| T42 | Box2D contact, hit and sensor events are all off by default; sensors need the flag on both shapes | physics |
| T43 | `floatQuat.CreateFromRotationMatrix` silently returns a non-unit quaternion for a mirrored (left-handed) basis | core-libs |
| T44 | RakNet only notices a vanished peer when a reliable message can't be delivered; set the same `SetTimeoutTime` on both ends | misc |
| T45 | `Alc.OpenDevice()` throws when there is no active default playback device; `ALSOFT_DRIVERS=null` must be set before the first OpenAL call | audio |
| T46 | Jolt constraints and characters work, but the casts, motor-settings getters, body locks, default filters and every character contact listener are unexported: create constraints from body IDs, down-cast with `Unsafe.BitCast`, use `XxxFilter.Create()` | physics |
| T47 | BRUTAL's ImGui `OpenGlBackend` and `VulkanBackend` silently never call `AddCallback` draw callbacks | glfw-imgui |
| T48 | SDL3 drops gamepad input while windows exist but none is focused (hidden test window, background game); set `HintJoystickAllowBackgroundEvents` | sdl |
| T49 | A hand-written ImGui platform glue must send `ImGuiKey.Mod_Ctrl/Shift/Alt/Super`; left/right modifier keys alone give no shortcuts | glfw-imgui |
| T50 | Two packages ship the same notice file name (ImGui + ImGui.Backends); a naive licence-copy target makes `dotnet publish` fail with NETSDK1152 | shipping |
| T51 | A hinge motor's torque is unlimited by default and drives an arm through its limits; cap it with `GetMotorSettings().SetTorqueLimit` | physics |
| T52 | A single-file publish bundles `Brutal.Monitor.Subprocess.exe`, so Monitor silently writes no crash dumps; mark it `ExcludeFromSingleFile` | shipping |
| B1 | `PtrOwner.Reset(new)` leaks the old memory | core-libs |
| B2 | `NativeList.Insert(ByteSize, byte)` never grows the size | core-libs |
| B3 | Explicit `INativeList.Capacity` gets/sets the size | core-libs |
| B4 | `RandomExtensions.Next*Unit3` clusters at the poles | core-libs |
| B5 | An exception in `IJob.Run` kills the process | core-libs |
| B6 | `LString.Format<TWriter>` copies the ref-struct writer | core-libs |
| B7 | ImGui Vulkan backend's out-of-date checks never run | glfw-imgui |
| B8 | `NativeList<T>.GetEnumerator` allocates | core-libs |
| B9 | `Mem` doesn't track memory, despite the conventions doc | core-libs |
| B10 | `TextureLoader.LoadFromMemory` never transcodes Basis KTX2 (throws, leaks the handle) | assets |
