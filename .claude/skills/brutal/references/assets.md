# Assets: images, textures, glTF, meshes

Read this for loading images (Stb), compressed textures (KTX/Basis, DDS), glTF models, mesh optimisation, or embedding and copying asset files.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Stb | `Brutal.StbApi` | stb.dll | **yes** | Hand | `Load`/`LoadFromMemory` → `StbImage8` (dispose; T1). `WritePng/Bmp/Tga/Jpg/Hdr` |
| Ktx / Gli | `Brutal.KtxApi` / `Brutal.GliApi` | Ktx.dll / gli.dll | yes | Gen / Hand | KTX1/2 + Basis, with the encoder (7.38, T32, B10); DDS |
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

### 7.25 glTF: load a .glb and draw it with OpenGL [RUN]
Namespace `Brutal.GltfApi` (pure .NET). The recipe writes its own tiny .glb, loads it, draws it with OpenGL 4.1 core (`opengl.md`) and checks the pixels.
```csharp
using var loader = new GltfLoader("model.glb");      // parses the JSON; the GLB BIN chunk or .bin files load lazily
var prim = loader.GltfJson.Meshes[0].Primitives[0];   // check prim.Mode == MeshPrimitive.ModeEnum.Triangles
var acc = loader.GltfJson.Accessors[prim.Attributes["POSITION"]]; // ComponentType == Accessor.ComponentTypeEnum.Float, Type == Accessor.TypeEnum.Vec3
var positions = MemoryMarshal.Cast<byte, float3>(loader.GetAccessorBufferSpan(prim.Attributes["POSITION"]));
var indices = MemoryMarshal.Cast<byte, ushort>(loader.GetAccessorBufferSpan(prim.Indices!.Value)); // or uint: check ComponentType
// Upload with glBufferData, set glVertexAttribPointer per attribute, then glDrawElements(GL_TRIANGLES, n, GL_UNSIGNED_SHORT, null).
```
- Enum names are PascalCase (`Triangles`, `Vec3`, `UnsignedShort`), not the spec's `TRIANGLES`/`VEC3`.
- `GetAccessorBufferSpan` doesn't support sparse accessors. For interleaved buffer views it returns `count * stride` bytes, so step by `bufferView.ByteStride`, not by the element size.
- Node transforms, materials and images: `loader.GltfJson.Nodes`, `.Materials`, `loader.GetOrLoadImage(i)` [SRC]. Axis helpers: `loader.FlipHandedness()`, `SwapYZAxes()`, `Scale(f)` [SRC].
- Lower level: `GltfUtility.LoadModel("m.glb")` plus `model.LoadBinaryBuffer(0, "m.glb")` [DOC]. There's no Draco decoding.

### 7.38 Compressed textures: Basis KTX2 and DDS [RUN]
Checked by `RecipeCheck 7.38` (headless). It makes a Basis KTX2 with libktx's own encoder, so no sample file is needed.
```csharp
using Brutal.KtxApi; using Brutal.TextureApi;
// Encode (optional; normally your asset pipeline does this):
var info = new KtxTextureCreateInfo { VkFormat = (uint)VkFormat.R8G8B8A8Unorm, BaseWidth = 64, BaseHeight = 64, BaseDepth = 1,
    NumDimensions = 2, NumLevels = 1, NumLayers = 1, NumFaces = 1 };
KtxTexture2.Create(in info, KtxTextureCreateStorageEnum.TextureCreateAllocStorage, out var k2);
k2.SetImageFromMemory(0, 0, 0, rgbaBytes); k2.CompressBasis(255);
k2.WriteToMemory(out var mem); using (mem) File.WriteAllBytes("tex.ktx2", mem.Span.ToArray()); k2.Destroy();
// Load: TextureLoader picks KTX (libktx), DDS/KMG (gli) or stb by extension
var tex = TextureLoader.LoadFromFile("tex.ktx2", new Brutal.TextureApi.Ktx.Loader.LoadSettings { SuperCompressionTranscodeFormat = KtxTranscodeFmt.Rgba32 });
// tex.Format (TextureFormat = VkFormat values), tex.Extent, tex.Data, tex.RowPitch(level)
TextureWriter.SaveToFile(tex, "tex.dds");                     // .ktx/.ktx2, gli's or an stb format, by extension
TextureLoader.Unload(tex);
```
- With no settings, Basis becomes `BC7_UNorm_Block` (T32): 4096 bytes for 64x64. With `Rgba32` it's `R8G8B8A8_UNorm`, and the worst channel error was 11/255 against the source.
- `TextureLoader.LoadFromMemory` can't load Basis (B10). Do it yourself: `KtxTexture2.CreateFromMemory(bytes, KtxTextureCreateFlagBits.LoadImageDataBit, out var h)`, `h.TranscodeBasis(KtxTranscodeFmt.Rgba32, KtxTranscodeFlagBits.HighQuality)`, `new Brutal.TextureApi.Ktx.KtxTexture(h)`, and `Destroy()` it when done.
- Check what your encoder produced. On the 64x64 test gradient, `CompressBasis(0)` (ETC1S at libktx's default quality) decoded one 4x4 block as the opposite corner's colour (error 241/255); `CompressBasis(255)` gave 11, and UASTC (`CompressBasisEx` with `Uastc = true`) gave 7. Seen once, not asserted.
- A DDS written with `TextureWriter` and read back kept the format and every texel.
- Not run: mipmaps, cube maps, arrays, KTX1, sRGB formats, BC7 decoding (the BC7 bytes weren't checked against the source), uploading to a GPU, `LazyLoad`.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T1 | `Stb.Load`/`LoadFromMemory` on a missing or bad file returns `Data == null`, 0×0, **no exception**. Fails later ("Texture dimensions can't be 0") | Check `image.Data == null` immediately |
| T20 | `<None Include="Assets\x.png" CopyToOutputDirectory=…>` lands in `bin\…\Assets\` | Add `Link="x.png"` or embed it (7.10) |
| T32 | KTX2/Basis transcodes to **BC7** by default: `Ktx.Loader.LoadSettings.SuperCompressionTranscodeFormat = KtxTranscodeFmt.Bc7Rgba` [RUN: 7.38] | Request `Rgba32` (or a format your target GPU samples) in `LoadSettings` |

## Framework bugs (confirmed in source)

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B10 | `Brutal.Texture/Ktx/KtxTextureLoader.cs:40-49`, `Ktx/KtxTexture.cs:45-47` | `Ktx.Loader.LoadFromMemory` (and so `TextureLoader.LoadFromMemory`) never transcodes and ignores `LoadSettings`, so a Basis KTX2 from memory throws `TextureException: Texture is supercompressed.`, and the native handle it created is never destroyed [RUN: 7.38 asserts it] | Load from a file, or create, transcode and wrap it yourself (7.38) |
