# Vulkan, VMA, ShaderC, Slang

Read this for Vulkan (instance, devices, compute, windowed rendering/swapchain) and for shader compilation (GLSL/HLSL via ShaderC, Slang).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Vulkan | `Brutal.VulkanApi` | **system** `vulkan-1.dll` (T5) | compute | Gen (Vulkan-Headers 1.4.335) | Wrappers `Instance`/`PhysicalDevice`/`Device`/`Queue`/`CommandBuffer` + extension methods |
| Vulkan.Abstractions | `Brutal.VulkanApi.Abstractions` | — | — | Hand | `DeviceEx`, `BufferEx`, `ImageEx`, `MappedMemory`, `StagingPool`, `Presets`, `VertexInput.For<T>()`, pipeline/fence/submit helpers. **No swapchain helper** |
| Vulkan.Vma | `Brutal.VulkanApi.Vma` | vma.dll | — | Gen | VMA |
| ShaderC | `Brutal.ShaderCApi` | shaderc_shared, SPIRV-Tools-shared | **yes** | Gen+Hand | GLSL/HLSL → SPIR-V on the CPU |
| Slang | `Brutal.SlangApi` | slang*.dll ×4 | yes | Gen | Outputs SPIR-V and source text (T28) |

## Name translation

**Vulkan**
- `vkCreateFoo(dev, &info, alloc, &out)` → `var foo = device.CreateFoo(in info, null)`. Returns the handle and throws `VkResultError` (T4). `VkAllocator?` takes `null`.
- `vkCmdX(cb, …)` → `cb.X(…)`.
- Two-call enumerations → `x.GetFoo_Count()` + `x.GetFoo_Write(span)`.
- Instance-level functions are extensions on `Instance`/`PhysicalDevice`; device-level ones on `Device`.
- Generated sources:
  - `BRUTAL/Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs` (26.7k lines), `VkInstanceExtensions.gen.cs`
  - `Generated/Structs/<Name>.gen.cs`, `Generated/Enums/<Name>.gen.cs`

**ShaderC**
- `ShaderC.CreateCompiler()`, `ShaderC.CreateCompilerOptions()`.
- `compiler.CompileIntoSpv(…)` returns a result; check its status yourself.
- `compiler.Compile(…)` throws `ShaderException`.

## Key source locations

| What | File:line |
|---|---|
| Vulkan loader | `Brutal.Vulkan/ImportResolver.cs:16-50` |
| `Vulkan.CreateInstance`, `Instance` | `Brutal.Vulkan/Manual/Partials/Vulkan.cs:10`, `Manual/Classes/Instance.cs:8` |
| `Device.GetQueue` | `Brutal.Vulkan/Manual/Classes/Device.cs:94` |
| `CreateDevice`, `FindQueueFamily` | `Brutal.Vulkan.Abstractions/Extensions/PhysicalDeviceExtensions.cs:7,48` |
| `CreatePipelineLayout`, `AllocateCommandBuffer`, `CreateFence`, `WaitForFence` | `Brutal.Vulkan.Abstractions/Extensions/DeviceExtensions.cs:236,346,615,632` |
| `Queue.Submit` helper | `Brutal.Vulkan.Abstractions/Extensions/QueueExtensions.cs:17` |
| `BufferEx`, `CreateBuffer`, `MappedMemory.AsSpan<T>` | `Brutal.Vulkan.Abstractions/BufferEx.cs:8,202`, `MappedMemory.cs:147` |
| `ShaderModuleExtensions.FromSpirv` | `Brutal.Vulkan.Abstractions/Extensions/ShaderModuleExtensions.cs:5` |
| ShaderC `Compile` (span / string overloads) | `Brutal.ShaderC/HighLevel.cs:104,191` |

The only full swapchain/present implementation in the repo: `BRUTAL/Brutal.ImGui.Backends/RendererBackends/VulkanBackend.Helpers.cs:160-320` and `VulkanBackend.cs`.

## Recipes

### 7.7 ShaderC GLSL → SPIR-V [RUN]
```csharp
using var compiler = ShaderC.CreateCompiler();
using var options = ShaderC.CreateCompilerOptions();
options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
options.SetOptimizationLevel(OptimizationLevel.Performance);
var glsl = """
    #version 450
    layout(location = 0) in vec2 inPos;
    void main() { gl_Position = vec4(inPos, 0.0, 1.0); }
    """u8;
using var result = compiler.Compile(glsl, ShaderKind.VertexShader, "tri.vert"u8, "main"u8, options); // throws ShaderException
unsafe { var spirv = new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray(); } // magic 0x07230203
```
- Other kinds: `FragmentShader`, `ComputeShader`, ….
- `ShaderC.InferShaderKind(path)` picks the kind from the file name.

### 7.11 Vulkan instance [RUN]
```csharp
using var instance = Vulkan.CreateInstance(new Instance.CreateInfo
{
    AppInfo = new VkApplicationInfo { ApiVersion = (1 << 22) | (3 << 12) },   // 1.3
    EnabledExtensions = [], EnabledLayers = [],                                // both must be non-null
});
foreach (var pd in instance.PhysicalDevices)
{
    var props = pd.GetProperties();   // .DeviceType, .ApiVersion (packed)
    unsafe { var name = System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0'); }
}
```
- Some drivers list a GPU twice; choose by `DeviceType` and features, not by index.
- Extension and layer helpers are in `VulkanHelpers` (`AddSurfaceExtensions`, `AddValidationLayers`, `Validation.IsSupported`) [SRC].

### 7.12 Vulkan compute, end to end [RUN on a GPU: 1024 values, 0 errors]
```csharp
var physical = instance.PhysicalDevices[0];
var family = physical.FindQueueFamily(VkQueueFlags.ComputeBit);   // prefers dedicated families
var priority = 1f;
var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = 1, QueuePriorities = &priority };
using var device = physical.CreateDevice(new DeviceEx.CreateInfo { QueueCreateInfos = [queueInfo] }); // DeviceEx = device + allocator
var queue = device.GetQueue(family, 0);
using var buffer = device.CreateBuffer(new BufferEx.CreateInfo {        // one vkAllocateMemory per buffer
    Name = "values", BufferSize = ByteSize.Of<uint>(1024), BufferUsage = VkBufferUsageFlags.StorageBufferBit,
    AllocPreference = MemoryPreference.PreferCpu,
    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit });
// SPIR-V via 7.7 with ShaderKind.ComputeShader:
//   layout(local_size_x = 64) in; layout(std430, binding = 0) buffer Data { uint values[]; };
//   void main() { uint i = gl_GlobalInvocationID.x; values[i] = i * i; }
var module = ShaderModuleExtensions.FromSpirv(device, spirvBytes, null);
var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
var setLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
var pipelineLayout = device.CreatePipelineLayout([setLayout], [], null);
Span<VkPipeline> pipelines = stackalloc VkPipeline[1];               // output array (T3)
fixed (byte* entry = "main"u8)
    device.CreateComputePipelines(default, [new VkComputePipelineCreateInfo {
        Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = entry },
        Layout = pipelineLayout }], null, pipelines);
var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 };
var descriptorPool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = descriptorPool, DescriptorSetCount = 1, SetLayouts = &setLayout }, sets);
var bufferInfo = new VkDescriptorBufferInfo { Buffer = buffer.VkBuffer, Offset = default, Range = buffer.BufferSize };
device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = sets[0], DstBinding = 0, DescriptorCount = 1,
    DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &bufferInfo }], []);
var commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
var cmd = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary });
cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
cmd.BindPipeline(VkPipelineBindPoint.Compute, pipelines[0]);
cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, pipelineLayout, 0, sets, []);
cmd.Dispatch(1024 / 64, 1, 1);
var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.HostReadBit };
cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.HostBit, default, [barrier], [], []);
cmd.End();
var fence = device.CreateFence(default, null);
queue.Submit([], [], [cmd], [], fence);        // waitSems, waitStages, cmds, signalSems, fence
device.WaitForFence(fence, nint.MaxValue);     // VkResult.Success
using (var mapped = buffer.Map()) { Span<uint> values = mapped.AsSpan<uint>(); } // values[1023] == 1046529
// Teardown, reverse order, each takes (handle, null):
// DestroyFence, DestroyCommandPool, DestroyDescriptorPool, DestroyPipeline, DestroyPipelineLayout,
// DestroyDescriptorSetLayout, DestroyShaderModule. Then dispose buffer, device, instance.
```

### 7.13 Vulkan windowed rendering: triangle, swapchain, resize [RUN]
Runnable copy: `Sandbox/VulkanWindow` (about 600 lines; opens a window for ~3 s). It renders a triangle at 640x400, resizes the window to 900x560, rebuilds the swapchain, and reads the presented image back both times to check the pixels. It follows BRUTAL's own ImGui Vulkan backend (`BRUTAL/Brutal.ImGui.Backends/RendererBackends/VulkanBackend.Helpers.cs:160-320`): a classic render pass, not dynamic rendering.

1. **Window:** `Glfw.Init()`, `Glfw.WindowHint(GlfwWindowHint.ClientApi, 0)` (no GL context), `Glfw.CreateWindow(...)`.
2. **Instance:** `var names = Glfw.GetRequiredInstanceExtensions(out var n)` returns `byte**` (`VK_KHR_surface`, `VK_KHR_win32_surface`); copy them with `Marshal.PtrToStringUTF8` into the `HashSet<string>` for `Instance.CreateInfo.EnabledExtensions`. Add validation layers with `VulkanHelpers.AddValidationLayers(layers)` only if `VulkanHelpers.Validation.IsSupported` (needs the Vulkan SDK). SDL alternative: `Sdl.Vulkan.GetInstanceExtensions()` returns `string[]` [SRC].
3. **Surface:** `Glfw.CreateWindowSurface(instance.VkHandle, window, out ulong s)` returns an int VkResult (0 = success); `new VkSurfaceKHR((nint)s)`. Destroy with `instance.DestroySurfaceKHR(surface, null)` after the swapchain, before the instance. SDL: `Sdl.Vulkan.CreateSurface(window, instance.VkHandle)`, window created with `SdlWindowFlags.Vulkan` [SRC].
4. **Device:** for each physical device (discrete first), require `pd.SupportsExtension("VK_KHR_swapchain"u8)`, a family from `pd.FindQueueFamily(VkQueueFlags.GraphicsBit)` and `pd.GetSurfaceSupportKHR(family, surface)` (returns `Bool32`). Then `pd.CreateDevice(new DeviceEx.CreateInfo { Extensions = { "VK_KHR_swapchain" }, QueueCreateInfos = [queueInfo] })`.
5. **Format:** `GetSurfaceFormatsKHR_Count/_Write`; prefer `VkFormat.B8G8R8A8UNorm` + `VkColorSpaceKHR.SRGBNonlinearKHR` (note the capitalisation: `UNorm`, `SRGB`). `VkPresentModeKHR.FifoKHR` is always available (vsync).
6. **Swapchain:** `var cap = pd.GetSurfaceCapabilitiesKHR(surface)` (returns the struct). Use `cap.CurrentExtent` unless its `Width` is -1. `MinImageCount` = max(`cap.MinImageCount`, 3), clamped to `cap.MaxImageCount` when that isn't 0. Pass the old swapchain as `OldSwapchain`, destroy it after the new one exists. Then `GetSwapchainImagesKHR_Count/_Write`, and one `CreateImageView` + `CreateFramebuffer` per image.
7. **Render pass:** one colour attachment, `LoadOp = Clear`, `InitialLayout = Undefined`, `FinalLayout = PresentSrcKHR`, and a subpass dependency from `VK.SUBPASS_EXTERNAL` on `ColorAttachmentOutputBit`. It depends only on the format, so it survives resizes. (Don't use `CreateDefaultRenderPass`: its final layout is `ShaderReadOnlyOptimal`.)
8. **Pipeline:** `ShaderModuleExtensions.FromSpirv` (GLSL via 7.7), `device.CreatePipelineLayout([], [], null)`, `device.CreateGraphicsPipeline(default, new VkGraphicsPipelineCreateInfo { ... RenderPass = renderPass }, null)`. Make **viewport and scissor dynamic** (`VkDynamicState.Viewport`, `.Scissor`) so a resize doesn't rebuild the pipeline. Create every state struct with `new` (T2).
9. **Per frame (2 frames in flight):**
   - `WaitForFences([fence[f]], true, nint.MaxValue)`, then `AcquireNextImageKHR(swapchain, nint.MaxValue, imageReady[f], default, out var index)`.
   - **Reset the fence only after a successful acquire**: if acquire fails and you recreate and return, a reset fence would deadlock the next wait.
   - Record: `ResetCommandPool`, `Begin`, `BeginRenderPass(..., VkSubpassContents.Inline)`, `BindPipeline`, `SetViewport(0, [...])`, `SetScissor(0, [...])`, `Draw(3, 1, 0, 0)`, `EndRenderPass`, `End`.
   - `queue.Submit([imageReady[f]], [ColorAttachmentOutputBit], [cmd], [renderDone[index]], fence[f])`. Use **one render-done semaphore per swapchain image**, not per frame in flight: a present can still hold the previous one.
   - `queue.PresentKHR(new VkPresentInfoKHR { ... })` returns `VkResult`.
10. **Resize** [RUN]. There are three signals, so handle all of them:
    - `ErrorOutOfDateKHR` **throws** `VkResultError` from acquire or present (T4): `catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)`.
    - `SuboptimalKHR` is **returned**, not thrown. On the RX 7900 XTX test machine this was the only signal the driver gave for a resize (twice), so ignoring it leaves a stretched old-size image.
    - The window's `FramebufferSize` differs from the swapchain extent. Checking this at the top of each frame catches the resize before the driver says anything.
    - To recreate: `device.WaitIdle()`, destroy the views, framebuffers and semaphores, then create the swapchain again with `OldSwapchain`.
    - **Minimised** (framebuffer 0x0): a swapchain can't be 0x0, so skip the frame. Don't block inside the rebuild: wait in the main loop with `Glfw.WaitEventsTimeout(0.1)` and keep checking `ShouldClose`, or Close and Escape do nothing until the window is restored [RUN: 0 frames presented while minimised, a close request handled straight away, correct pixels after restore]. `window.Iconify()` is only a request: Windows took about 0.5 s to reach 0x0, so tests must wait for it.
11. **Read-back for tests:** create the swapchain with `ImageUsage = ColorAttachmentBit | TransferSrcBit` (check `cap.SupportedUsageFlags`). After `EndRenderPass`, barrier `PresentSrcKHR -> TransferSrcOptimal`, `CopyImageToBuffer` into a host-visible `BufferEx`, and barrier back to `PresentSrcKHR`. `B8G8R8A8UNorm` reads back in BGRA byte order.

### 7.29 Vulkan depth buffer, vertex/index buffers, push constants, matrices [RUN]
Runnable copy: `Sandbox/VulkanDepth` (about 330 lines, **off-screen: no window or display needed**). It draws a back-facing quad (must be culled), a near green quad and then a big far red quad, reads the pixels back, and repeats with the depth test off as a control (red must then win). Written from the 2026-10-18 Vulkan-cube eval code and confirmed on the RX 7900 XTX (D32SFloat).
- **Depth format:** `pd.FindSupportedFormat([VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm], VkImageTiling.Optimal, VkFormatFeatureFlags.DepthStencilAttachmentBit)` (in `PhysicalDeviceExtensions`; returns `Undefined` when none fits). Aspect `DepthBit` (plus `StencilBit` for D24S8).
- **Depth image:** `device.CreateImage(new ImageEx.CreateInfo { ImageType = VkImageType._2D, ImageFormat = depthFormat, ImageExtent = new VkExtent3D { Width = w, Height = h, Depth = 1 }, ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit, ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu })`, plus a view on `.VkImage`. Rebuild it with the swapchain on every resize.
- **Render pass:** a second attachment (`LoadOp = Clear`, `FinalLayout = DepthStencilAttachmentOptimal`), `DepthStencilAttachment = &depthRef` in the subpass, and the dependency's masks extended with `EarlyFragmentTestsBit`/`LateFragmentTestsBit` and `DepthStencilAttachmentWriteBit`. Clear with `new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1 } }` as the second clear value. Pipeline: `VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = VkCompareOp.Less }`.
- **Buffers:** host-visible `BufferEx` with `VkBufferUsageFlags.VertexBufferBit` / `IndexBufferBit` (7.12's `CreateBuffer` + `Map`), then `cmd.BindVertexBuffer(0, vb.VkBuffer)`, `cmd.BindIndexBuffer(ib.VkBuffer, default, VkIndexType.UInt16)`, `cmd.DrawIndexed(count, 1, 0, 0, 0)`. Vertex layout: `VertexInput.For<TVertex>()` or hand-built `VkVertexInputBindingDescription`/`AttributeDescription`.
- **Push constants:** `new VkPushConstantRange { StageFlags = VertexBit | FragmentBit, Offset = default, Size = ByteSize.Of<TPush>() }` into `CreatePipelineLayout([], [range], null)`, then `cmd.PushConstants(layout, stages, ByteSize.Zero, push)` with an unmanaged struct (128 bytes is the guaranteed minimum).
- **Matrices:** Numerics is row-vector, so `mvp = model * view * proj`. Upload it unchanged and write `gl_Position = pc.mvp * vec4(pos, 1)` in GLSL: GLSL's column-major reading transposes it, which is exactly what makes the two conventions agree. `float4x4.CreatePerspectiveFieldOfView` already maps depth to 0..1 (Vulkan's range); flip Y for Vulkan with `proj.M22 = -proj.M22`. With that flip, counter-clockwise triangles face the camera: `FrontFace = VkFrontFace.CounterClockwise` with `CullMode = BackBit`.
- **Off-screen variant** (what the Sandbox tool does): a colour `ImageEx` with `ColorAttachmentBit | TransferSrcBit`, render-pass `FinalLayout = TransferSrcOptimal`, and a second subpass dependency (`ColorAttachmentOutputBit`/`ColorAttachmentWriteBit` to `TransferBit`/`TransferReadBit`) so no layout barrier is needed before `CopyImageToBuffer`. `R8G8B8A8UNorm` reads back as RGBA bytes. Unlike the swapchain case, nothing needs rebuilding on resize.
- **Checked by pixel:** with `proj.M22 = -proj.M22`, world +Y is the top of the image; `CounterClockwise` + `BackBit` culls a quad turned by `CreateRotationY(MathF.PI)`; `Push { float4x4 Mvp; float4 Color; }` (80 bytes, `[StructLayout(Sequential)]`) reaches both stages unchanged.
- Text for the credits line with no text renderer: render it once with SDL_ttf (`RenderTextBlended` to a surface) or bake it into the splash PNG, and draw it as a textured quad (`shipping.md`).

### 7.30 Vulkan texture upload and sampling [RUN]
Runnable copy: `Sandbox/VulkanTexture` (about 330 lines, off-screen, no window). It uploads a 4x4 quadrant texture, draws it full-target and reads pixels back. Adapted from `CreateTexture` in the 2026-10-18 Vulkan-cube eval's splash code.
1. **Image:** `device.CreateImage(new ImageEx.CreateInfo { ImageType = VkImageType._2D, ImageFormat = fmt, ImageExtent = ..., ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit, ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu })`. Dispose the `ImageEx` yourself; the view is separate (`CreateImageView` on `.VkImage`).
2. **Staging:** a host-visible `BufferEx` with `TransferSrcBit` (7.12's `CreateBuffer` + `Map`), `rgba.CopyTo(mapped.AsSpan<byte>())`, dispose it once the copy has finished.
3. **One-time submit:** barrier `Undefined -> TransferDstOptimal` (`TopOfPipeBit` to `TransferBit`, `DstAccessMask = TransferWriteBit`), `cmd.CopyBufferToImage(staging.VkBuffer, image.VkImage, TransferDstOptimal, [VkBufferImageCopy { ImageSubresource = ..., ImageExtent = ... }])`, barrier `TransferDstOptimal -> ShaderReadOnlyOptimal` (`TransferBit` to `FragmentShaderBit`, `TransferWriteBit` to `ShaderReadBit`), then submit with a fence and wait. Every barrier needs `SrcQueueFamilyIndex = DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED`. Row 0 of the bytes is the top of the image; there is no flip.
4. **Sampler:** `device.CreateSampler(new VkSamplerCreateInfo { MagFilter, MinFilter, MipmapMode, AddressModeU/V/W = ClampToEdge, MaxLod = 0 }, null)`. With one mip level and `MaxLod = 0`, `Linear` for UI and `Nearest` for pixel art.
5. **Descriptors:** a layout binding (`CombinedImageSampler`, `FragmentBit`), a pool with `DescriptorCount` and `MaxSets` at least the number of textures, `AllocateDescriptorSets` into a writable span (T3), then `UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = CombinedImageSampler, ImageInfo = &info }], [])` with `info = new VkDescriptorImageInfo { Sampler, ImageView, ImageLayout = ShaderReadOnlyOptimal }`. Put the layout in `CreatePipelineLayout([setLayout], [], null)` and bind with `cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, sets, [])`.
6. **Shader:** `layout(set = 0, binding = 0) uniform sampler2D tex;` and `texture(tex, uv)`. A full-target quad needs no vertex buffer: index a `const vec2 CORNERS[6]` with `gl_VertexIndex` and draw `Draw(6, 1, 0, 0)`; with uv = (0,0) at the top-left, `gl_Position = vec4(uv * 2.0 - 1.0, 0, 1)` draws the texture upright (Vulkan clip space has +Y down).
7. **sRGB vs UNorm** [RUN]: the same grey 128 sampled to a UNorm target reads back as 128 from an `R8G8B8A8UNorm` image and as 55 from an `R8G8B8A8SRGB` image (the sampler decodes to linear). Use `SRGB` for colour art and UNorm for data (normals, masks). (Standard Vulkan behaviour, not run here: an sRGB swapchain format re-encodes on write, so the pair is correct, while an SRGB texture drawn to a UNorm target looks too dark.)
8. **C# detail:** taking `&localStruct` inside a local function or lambda that captured it fails with CS1686. Copy it into a fresh local first.
9. **Teardown:** destroy the descriptor pool (frees its sets), set layout, sampler, view, then dispose the image.

### 7.31 Vulkan splash: logo + credits line as a textured quad [RUN]
A raw Vulkan app has no text renderer, but licence 2.1 and 2.2 need the credits notice and the unaltered logo before gameplay (`shipping.md`). Runnable copy: `Sandbox/VulkanSplash` (off-screen, about 400 lines, writes `splash-640x400.png` for a look). Upload and sampling are 7.30; this adds the composition and the draw.
1. **Compose once on the CPU** into one opaque-black RGBA canvas (the tool uses 760x280): the logo with `Stb.Load(path, 4)` (T1: a missing file returns a null image, so check `IsNull()`), the credits with SDL_ttf. Use `LOGO_BRUTAL_WHT.png` on a dark canvas; the logo must stay unaltered, so scale the whole canvas at draw time, never the logo alone.
2. **Credits text without a window or SDL_Init:** `SdlTtf.Init()`, `SdlTtf.OpenFont(path, 18)`, `SdlTtf.RenderTextBlended(font, text, 0, white)`, `Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Abgr8888)` (RGBA byte order), then copy rows using `rgba->pitch` (SDL may pad rows), `DestroySurface` on both, `CloseFont`, `SdlTtf.Quit()`. Sdl.Ttf ships no font (T39): embed an OFL font and ship its `OFL.txt` (the `brutal-3d` template's Atkinson Hyperlegible Bold is what the tool loads). The Windows-only alternative is baking `credits.png` once with `System.Drawing` and embedding it.
3. **Draw:** no vertex buffer. Push constants `{ float4 Rect (x0, y0, x1, y1 in NDC); float4 Params (x = fade) }`; the vertex shader does `gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, uv), 0, 1)` with uv from `CORNERS[gl_VertexIndex]`, the fragment shader `vec4(texture(tex, vUv).rgb * pc.params.x, 1)`. Letterbox: `scale = min(0.8 * W / canvasW, 0.8 * H / canvasH, 1.5)`, half-size in NDC `(canvasW * scale / W, canvasH * scale / H)`. Recompute it each frame so a resize keeps the aspect ratio.
4. **Sequence:** draw the splash for a couple of seconds with the fade ramping in and out before the scene's first frame; the credits line also belongs in the player README.
5. **Checked by pixel:** the quad sits where the letterbox says, the white logo has more than 800 bright pixels, the credits band has more than 150 lit pixels, the gap between them stays black, and fade 0.5 gives 0.50 of the brightness. When a check counts "lit" pixels, count only inside the quad: the clear colour outside it can pass a low threshold (this bit the first run of the tool).
6. A full Vulkan game template (`brutal-vulkan`) doesn't exist yet; the eval's complete app is outside the repo.

### Slang

**Slang** [DOC]: legacy compile-request API. `CreateCompileRequest()`, `SetCodeGenTarget(CompileTarget.Spirv)`, then `AddTranslationUnit`, `AddTranslationUnitSourceString`, `AddEntryPoint`, `Compile`.
```csharp
Slang.CreateGlobalSession(Constants.ApiVersion, out var s);
```

## Traps

| ID | Trap | Fix |
|---|---|---|
| T2 | Vulkan `sType` is set **only** by `new T()`. `default`, `stackalloc` and `new T[n]` leave it 0, causing validation errors or crashes | Always `new VkFoo { … }`; `arr[i] = new VkFoo { … }` |
| T3 | Vulkan outputs typed `ReadOnlySpan<T>` are still written (`CreateComputePipelines`, `AllocateDescriptorSets`) | Pass a writable span you own (`stackalloc`) |
| T4 | Every failing `VkResult` **throws `VkResultError`** (`.Result`), including `ErrorOutOfDateKHR` from acquire/present (`VkDeviceExtensions.gen.cs:8361`). The ImGui backend's checks for it are dead code, so a resize crashes (B7) | `catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) { RecreateSwapchain(); }` |
| T5 | The Vulkan loader is `vulkan-1.dll` from **System32 only** (`ImportResolver.cs:21`). Touching any `Vulkan.*` static (even `Vulkan.Log`) runs that loader; on failure you get `TypeInitializationException`. The packaged `VulkanEx.dll` isn't the loader | Install the Vulkan runtime. Keep Vulkan types off code paths that must run without it |
| T28 | Slang: no CPU/host-callable, LLVM or WGSL→SPIR-V targets; DXIL/Metal lib/PTX need your own toolchains | SPIR-V or source text |

Also B7 (`glfw-imgui.md`): the ImGui Vulkan backend's out-of-date checks never run.
