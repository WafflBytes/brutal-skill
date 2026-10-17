# Assets: images, textures, glTF, meshes

Read this for loading images (Stb), compressed textures (KTX/Basis, DDS), glTF models, mesh optimisation, or embedding and copying asset files.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Stb | `Brutal.StbApi` | stb.dll | **yes** | Hand | `Load`/`LoadFromMemory` → `StbImage8` (dispose; T1). `WritePng/Bmp/Tga/Jpg/Hdr` |
| Ktx / Gli | `Brutal.KtxApi` / `Brutal.GliApi` | Ktx.dll / gli.dll | yes | Gen / Hand | KTX1/2 + Basis (T32); DDS |
| Texture (+Abstractions) | `Brutal.TextureApi*` | — | yes | Hand | `TextureLoader.LoadFromFile` picks the decoder by extension; `TextureFormat` = `VkFormat` values |
| Gltf | `Brutal.GltfApi` | — | **yes** | Hand+Gen | Pure .NET glTF 2.0/GLB; no Draco decoding |
| MeshOptimizer | `Brutal.MeshOptimizer` | meshoptimizer.dll | yes | Gen | Thin; native docs apply |
| Render.Common/Debug/Mesh | `Brutal.Render.Mesh` | — | yes | Hand | `Mesh<TKey>`, `ProcGenMeshLibrary`. Common/Debug: tiny assemblies with no source in the repo |
| FreeType / ZLib | — | freetype+png18 / zlib | — | — | Native-only marker packages (no C# API) |

## Key source locations

| What | File:line |
|---|---|
| Stb load / write | `Brutal.Stb/Stb.cs:29,42,158` |

## Recipes

### 7.10 Image from an embedded resource [RUN]
```csharp
// csproj: <EmbeddedResource Include="Assets\logo.png" LogicalName="logo.png" />
using var stream = typeof(Program).Assembly.GetManifestResourceStream("logo.png")!;
var png = new byte[stream.Length]; stream.ReadExactly(png);
var image = Stb.LoadFromMemory(png, 4);        // 4 = RGBA
if (image.Data == null) throw new InvalidOperationException("decode failed"); // T1
// image.Width / Height / AsSpan()
image.Dispose();
```

### glTF

**glTF** [DOC]. `LightData` is nullable.
```csharp
var model = GltfUtility.LoadModel("m.glb");
var buf = model.LoadBinaryBuffer(0, "m.glb");
```

## Traps

| ID | Trap | Fix |
|---|---|---|
| T1 | `Stb.Load`/`LoadFromMemory` on a missing or bad file returns `Data == null`, 0×0, **no exception**. Fails later ("Texture dimensions can't be 0") | Check `image.Data == null` immediately |
| T20 | `<None Include="Assets\x.png" CopyToOutputDirectory=…>` lands in `bin\…\Assets\` | Add `Link="x.png"` or embed it (7.10) |
| T32 | KTX2/Basis transcodes to **BC7** by default | Request `Rgba32` when BC7 can't be sampled |
