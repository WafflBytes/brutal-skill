# Shipping: single-file publish and the BRUTAL licence

Read this before publishing or distributing anything built on BRUTAL.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

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
- Test the output from an empty folder.

## Licence checklist (BRUTAL License v1.0)

A distributed product **must**:
1. Show the credits notice (2.1): `BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.`
2. Show the BRUTAL logo **before gameplay** (2.2).
   - Files: `BRUTAL/docs/branding/LOGO_BRUTAL_WHT.png` / `_BLK.png`; official assets at ahwoo.com/brutal-branding.
   - A 2.5 s fade, skippable after 1 s, is fine.
3. Ship `NOTICE.txt` beside it (2.3); copy `BRUTAL/NOTICE.txt`.
4. Keep each shipped package's `LICENSES/` notices; watch OpenAL (T30) and FMOD (T21). Most native packages (Sdl, Sdl.Ttf, Sdl.Mixer, Stb, …) carry `LICENSES/BRUTAL-*-THIRD-PARTY-NOTICES.txt`, but **nothing copies them to your output by default**. This target [RUN] copies them for every referenced `Ahwoo.BRUTAL.*` package into `licenses/`, for both build and publish:
   ```xml
   <Target Name="IncludeBrutalThirdPartyNotices" BeforeTargets="AssignTargetPaths" Condition="'$(NuGetPackageRoot)' != ''">
     <ItemGroup>
       <_BrutalPackage Include="@(PackageReference)" Condition="$([System.String]::Copy('%(Identity)').StartsWith('Ahwoo.BRUTAL.'))" />
       <_BrutalNotice Include="$(NuGetPackageRoot)$([System.String]::Copy('%(_BrutalPackage.Identity)').ToLowerInvariant())/%(_BrutalPackage.Version)/LICENSES/*.txt" />
       <None Include="@(_BrutalNotice)" Link="licenses/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
     </ItemGroup>
   </Target>
   ```
5. **Sdl.Ttf links FreeType**, whose licence (FTL) also asks for a credit in your documentation: "Portions of this software are copyright © 2023 The FreeType Project (https://freetype.org). All rights reserved." The year matches FreeType 2.13.2 in Sdl.Ttf 2026.10.2; take it from the package's notice file if the version changes.
6. **Fonts and other assets** carry their own licences. Sdl.Ttf ships no font (T39). An SIL OFL font may be embedded and redistributed if its `OFL.txt` ships with the product. The `brutal-sdl` template does all of this: it embeds Atkinson Hyperlegible, ships `OFL-AtkinsonHyperlegible.txt` and `licenses/`, and credits everything in its README.

There are no royalties. For logo waivers (non-profit, or where the logo harms the product) or a commercial licence, contact brutal@ahwoo.com.
