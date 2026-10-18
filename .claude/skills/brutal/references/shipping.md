# Shipping: single-file publish and the BRUTAL licence

Read this before publishing or distributing anything built on BRUTAL.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

### 7.14 Single-file exe [RUN]
```xml
<PropertyGroup Condition="'$(_IsPublishing)' == 'true'">
  <SelfContained>true</SelfContained><PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract> <!-- BRUTAL resolvers work with this -->
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <OutputType>WinExe</OutputType><DebugType>none</DebugType>
</PropertyGroup>
<ItemGroup><None Include="Assets\NOTICE.txt" Link="NOTICE.txt" CopyToOutputDirectory="PreserveNewest" /></ItemGroup> <!-- licence 2.3 -->
```
- Build with `dotnet publish -c Release -o ../Game-win-x64`. An SDL + Stb game is about 38 MB.
- Using `Brutal.Monitor`? Keep its watchdog exe out of the bundle (T52) [RUN]:
  ```xml
  <Target Name="KeepMonitorSubprocessOutOfBundle" AfterTargets="ComputeResolvedFilesToPublishList">
    <ItemGroup>
      <ResolvedFileToPublish Update="@(ResolvedFileToPublish)" Condition="'%(Filename)' == 'Brutal.Monitor.Subprocess'" ExcludeFromSingleFile="true" />
    </ItemGroup>
  </Target>
  ```
- Test the output from an empty folder.

## Licence checklist (BRUTAL License v1.0)

A distributed product **must**:
1. Show the credits notice (2.1): `BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.`
2. Show the BRUTAL logo **before gameplay** (2.2).
   - Files: `BRUTAL/docs/branding/LOGO_BRUTAL_WHT.png` / `_BLK.png`; official assets at ahwoo.com/brutal-branding.
   - A 2.5 s fade, skippable after 1 s, is fine.
3. Ship `NOTICE.txt` beside it (2.3); copy `BRUTAL/NOTICE.txt`.
4. Keep each shipped package's `LICENSES/` notices; watch OpenAL (T30) and FMOD (T21). Most native packages (Sdl, Sdl.Ttf, Sdl.Mixer, Stb, …) carry `LICENSES/BRUTAL-*-THIRD-PARTY-NOTICES.txt`, but **nothing copies them to your output by default**. This target [RUN] copies them for every referenced `Ahwoo.BRUTAL.*` package into `licenses/`, for both build and publish, one copy per file name (T50):
   ```xml
   <Target Name="IncludeBrutalThirdPartyNotices" BeforeTargets="AssignTargetPaths" Condition="'$(NuGetPackageRoot)' != ''">
     <ItemGroup>
       <_BrutalPackage Include="@(PackageReference)" Condition="$([System.String]::Copy('%(Identity)').StartsWith('Ahwoo.BRUTAL.'))" />
       <_BrutalNotice Include="$(NuGetPackageRoot)$([System.String]::Copy('%(_BrutalPackage.Identity)').ToLowerInvariant())/%(_BrutalPackage.Version)/LICENSES/*.txt" />
       <!-- Some packages ship the same notice (ImGui and ImGui.Backends): copy each name once, or publish fails (NETSDK1152, T50). -->
       <_BrutalNoticeName Include="@(_BrutalNotice->'%(Filename)%(Extension)')" Source="%(_BrutalNotice.FullPath)" />
     </ItemGroup>
     <RemoveDuplicates Inputs="@(_BrutalNoticeName)">
       <Output TaskParameter="Filtered" ItemName="_BrutalNoticeOnce" />
     </RemoveDuplicates>
     <ItemGroup>
       <None Include="@(_BrutalNoticeOnce->'%(Source)')" Link="licenses/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
     </ItemGroup>
   </Target>
   ```
5. **Sdl.Ttf links FreeType**, whose licence (FTL) also asks for a credit in your documentation: "Portions of this software are copyright © 2023 The FreeType Project (https://freetype.org). All rights reserved." The year matches FreeType 2.13.2 in Sdl.Ttf 2026.10.2; take it from the package's notice file if the version changes.
6. **The .NET runtime is bundled** by a self-contained publish, and it's MIT-licensed with its own third-party notices. Ship them too [RUN]:
   ```xml
   <Target Name="IncludeDotnetRuntimeNotices" BeforeTargets="GetCopyToPublishDirectoryItems" Condition="'$(SelfContained)' == 'true'">
     <ItemGroup>
       <_DotnetRuntimePack Include="@(ResolvedRuntimePack->WithMetadataValue('FrameworkName', 'Microsoft.NETCore.App'))" />
       <ResolvedFileToPublish Include="%(_DotnetRuntimePack.PackageDirectory)\LICENSE.TXT" RelativePath="licenses\DOTNET-RUNTIME-LICENSE.txt" CopyToPublishDirectory="PreserveNewest" ExcludeFromSingleFile="true" />
       <ResolvedFileToPublish Include="%(_DotnetRuntimePack.PackageDirectory)\THIRD-PARTY-NOTICES.TXT" RelativePath="licenses\DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt" CopyToPublishDirectory="PreserveNewest" ExcludeFromSingleFile="true" />
     </ItemGroup>
   </Target>
   ```
   In a `dotnet new` template, don't put conditions like `'%(X.Y)' == 'literal'` in a `Condition` attribute: the template engine evaluates `Condition` attributes when it instantiates the project and silently deletes elements it judges false [RUN]. Use item functions such as `WithMetadataValue` instead.
7. **Fonts and other assets** carry their own licences. Sdl.Ttf ships no font (T39). An SIL OFL font may be embedded and redistributed if its `OFL.txt` ships with the product. The `brutal-sdl` template does all of this: it embeds Atkinson Hyperlegible, ships `OFL-AtkinsonHyperlegible.txt` and `licenses/`, and credits everything in its README.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T50 | Two BRUTAL packages can ship a notice with the same file name (ImGui + ImGui.Backends; the two RenderDoc packages). A naive licence-copy target builds fine, but `dotnet publish` fails with **NETSDK1152** [RUN] | Use the target above, which drops duplicate names |
| T52 | With the single-file settings above, `IncludeNativeLibrariesForSelfExtract` also bundles `Brutal.Monitor.Subprocess.exe` (it ships as a native asset). `Monitor` looks for it only beside the app, so `SubprocessRunning` is false and **no crash dumps are written**, with no exception [RUN] | Add the `KeepMonitorSubprocessOutOfBundle` target above. Check `SubprocessRunning` at startup |
| T41 | Nearly every BRUTAL native DLL imports the Microsoft VC++ runtime: `VCRUNTIME140.dll` (SDL3, SDL_image, SDL_mixer, stb, Box2D, GLFW, GLAD, …), plus `VCRUNTIME140_1.dll` (SDL_ttf, ImGui, Jolt, …) and `MSVCP140.dll` (Jolt, RakNet, ShaderC, VMA, Ktx, Gli, Slang). Windows doesn't include them and a self-contained publish doesn't bundle them, so the game works on dev machines and fails for a friend with "Unable to load DLL 'SDL3' or one of its dependencies" [RUN: import tables] | Check at start-up and point the player to `vc_redist.x64.exe` (template `Platform/NativeDialog.cs`). App-local DLLs depend on your Visual Studio licence. List it in the player README |

## Making a build players can use

- **Check for the VC++ runtime at start-up (T41).** Before the first SDL call, `NativeLibrary.TryLoad("vcruntime140.dll", ...)` and `vcruntime140_1.dll`; if either fails, show a dialog with the official download link (`https://aka.ms/vs/17/release/vc_redist.x64.exe`) and exit.
- **Crash reporting.** The published exe is `WinExe` (no console), so an unhandled exception just closes the window. Catch it in play mode, write a log under `%LOCALAPPDATA%\<Game>\error.log` (the exe's folder may be read-only), and show a **Win32** `MessageBoxW` via `[LibraryImport("user32.dll")]` [RUN]. Don't use `Sdl.PInvoke.ShowSimpleMessageBox` here: if the error is SDL failing to load, SDL's dialog fails too and the game closes silently.
- **Credits with no text renderer:** recipe 7.31 (`vulkan.md`). Repeat them in the player README.
- **The VC++ check must cover every runtime DLL your packages import (T41).** The `brutal-sdl` template checks `vcruntime140` and `vcruntime140_1`; add `msvcp140` when you add Jolt, RakNet, ShaderC, VMA, Ktx, Gli or Slang (`brutal-3d` does, for Jolt). Edit `NativeDialog.RequiredVcRuntimeDlls`.
- **A player README.txt beside the exe:** controls, credits (including the FreeType line), the VC++ runtime requirement (T41), and a SmartScreen note for unsigned exes ("More info", then "Run anyway").
- **One zip** of the publish folder is the easiest thing to send.
- **Pause on focus loss**, so Alt-Tab doesn't cost a life.
- The `brutal-sdl` template does all of the above; `Sandbox/check-template.ps1` checks the crash dialog and log, the missing-runtime dialog (`--vcruntime-test`), and every licence file on the published exe.

## Tooling note: keep files ASCII

Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI, so a read/replace/write turns `©` into `Â©`. Keep templates and generated text ASCII (`(c)`, `->`; `check-template.ps1` enforces it) and edit with a UTF-8-aware tool.

There are no royalties. For logo waivers (non-profit, or where the logo harms the product) or a commercial licence, contact brutal@ahwoo.com.
