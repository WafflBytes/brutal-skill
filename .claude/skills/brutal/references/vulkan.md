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
| Slang | `Brutal.SlangApi` | slang*.dll ×4 | yes | Gen | Outputs SPIR-V and source text (T28); 7.40, B11, B12 |

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
    - `SuboptimalKHR` is **returned**, not thrown. Some drivers give only this signal for a resize; ignoring it leaves a stretched old-size image.
    - The window's `FramebufferSize` differs from the swapchain extent. Checking this at the top of each frame catches the resize before the driver says anything.
    - To recreate: `device.WaitIdle()`, destroy the views, framebuffers and semaphores, then create the swapchain again with `OldSwapchain`.
    - **Minimised** (framebuffer 0x0): skip the frame. Wait in the main loop with `Glfw.WaitEventsTimeout(0.1)` and keep checking `ShouldClose`, not inside the rebuild, or Close does nothing until restore [RUN]. `window.Iconify()` is asynchronous (~0.5 s to reach 0x0 on Windows); tests must wait for it.
11. **Read-back for tests:** create the swapchain with `ImageUsage = ColorAttachmentBit | TransferSrcBit` (check `cap.SupportedUsageFlags`). After `EndRenderPass`, barrier `PresentSrcKHR -> TransferSrcOptimal`, `CopyImageToBuffer` into a host-visible `BufferEx`, and barrier back to `PresentSrcKHR`. `B8G8R8A8UNorm` reads back in BGRA byte order.

### 7.29 Vulkan depth buffer, vertex/index buffers, push constants, matrices [RUN]
Runnable copy: `Sandbox/VulkanDepth` (off-screen, no window needed; checked by pixel read-back).
- **Depth format:** `pd.FindSupportedFormat([VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm], VkImageTiling.Optimal, VkFormatFeatureFlags.DepthStencilAttachmentBit)` (in `PhysicalDeviceExtensions`; returns `Undefined` when none fits). Aspect `DepthBit` (plus `StencilBit` for D24S8).
- **Depth image:** `device.CreateImage(new ImageEx.CreateInfo { ImageType = VkImageType._2D, ImageFormat = depthFormat, ImageExtent = new VkExtent3D { Width = w, Height = h, Depth = 1 }, ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit, ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu })`, plus a view on `.VkImage`. Rebuild it with the swapchain on every resize.
- **Render pass:** a second attachment (`LoadOp = Clear`, `FinalLayout = DepthStencilAttachmentOptimal`), `DepthStencilAttachment = &depthRef` in the subpass, and the dependency's masks extended with `EarlyFragmentTestsBit`/`LateFragmentTestsBit` and `DepthStencilAttachmentWriteBit`. Clear with `new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1 } }` as the second clear value. Pipeline: `VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = VkCompareOp.Less }`.
- **Buffers:** host-visible `BufferEx` with `VkBufferUsageFlags.VertexBufferBit` / `IndexBufferBit` (7.12's `CreateBuffer` + `Map`), then `cmd.BindVertexBuffer(0, vb.VkBuffer)`, `cmd.BindIndexBuffer(ib.VkBuffer, default, VkIndexType.UInt16)`, `cmd.DrawIndexed(count, 1, 0, 0, 0)`. Vertex layout: `VertexInput.For<TVertex>()` or hand-built `VkVertexInputBindingDescription`/`AttributeDescription`.
- **Push constants:** `new VkPushConstantRange { StageFlags = VertexBit | FragmentBit, Offset = default, Size = ByteSize.Of<TPush>() }` into `CreatePipelineLayout([], [range], null)`, then `cmd.PushConstants(layout, stages, ByteSize.Zero, push)` with an unmanaged `[StructLayout(Sequential)]` struct.
- **Matrices:** Numerics is row-vector: `mvp = model * view * proj`, uploaded unchanged, with `gl_Position = pc.mvp * vec4(pos, 1)` in GLSL (no transpose). `float4x4.CreatePerspectiveFieldOfView` already maps depth to 0..1. Flip Y with `proj.M22 = -proj.M22`; then +Y is up and `FrontFace = CounterClockwise` + `CullMode = BackBit` is correct [RUN].
- **Off-screen variant** (what the Sandbox tool does): a colour `ImageEx` with `ColorAttachmentBit | TransferSrcBit`, render-pass `FinalLayout = TransferSrcOptimal`, and a second subpass dependency (`ColorAttachmentOutputBit`/`ColorAttachmentWriteBit` to `TransferBit`/`TransferReadBit`) so no layout barrier is needed before `CopyImageToBuffer`. `R8G8B8A8UNorm` reads back as RGBA bytes.

### 7.30 Vulkan texture upload and sampling [RUN]
Runnable copy: `Sandbox/VulkanTexture` (off-screen, no window).
1. **Image:** `device.CreateImage(new ImageEx.CreateInfo { ImageType = VkImageType._2D, ImageFormat = fmt, ImageExtent = ..., ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit, ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu })`. Dispose the `ImageEx` yourself; the view is separate (`CreateImageView` on `.VkImage`).
2. **Staging:** a host-visible `BufferEx` with `TransferSrcBit` (7.12's `CreateBuffer` + `Map`), `rgba.CopyTo(mapped.AsSpan<byte>())`, dispose it once the copy has finished.
3. **One-time submit:** barrier `Undefined -> TransferDstOptimal` (`TopOfPipeBit` to `TransferBit`, `DstAccessMask = TransferWriteBit`), `cmd.CopyBufferToImage(staging.VkBuffer, image.VkImage, TransferDstOptimal, [VkBufferImageCopy { ImageSubresource = ..., ImageExtent = ... }])`, barrier `TransferDstOptimal -> ShaderReadOnlyOptimal` (`TransferBit` to `FragmentShaderBit`, `TransferWriteBit` to `ShaderReadBit`), then submit with a fence and wait. Every barrier needs `SrcQueueFamilyIndex = DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED`. Row 0 of the bytes is the top of the image; there is no flip.
4. **Sampler:** `device.CreateSampler(new VkSamplerCreateInfo { MagFilter, MinFilter, MipmapMode, AddressModeU/V/W = ClampToEdge, MaxLod = 0 }, null)`. With one mip level and `MaxLod = 0`, `Linear` for UI and `Nearest` for pixel art.
5. **Descriptors:** a layout binding (`CombinedImageSampler`, `FragmentBit`), a pool with `DescriptorCount` and `MaxSets` at least the number of textures, `AllocateDescriptorSets` into a writable span (T3), then `UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = CombinedImageSampler, ImageInfo = &info }], [])` with `info = new VkDescriptorImageInfo { Sampler, ImageView, ImageLayout = ShaderReadOnlyOptimal }`. Put the layout in `CreatePipelineLayout([setLayout], [], null)` and bind with `cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, sets, [])`.
6. **Shader:** `layout(set = 0, binding = 0) uniform sampler2D tex;` and `texture(tex, uv)`. A full-target quad needs no vertex buffer: index a `const vec2 CORNERS[6]` with `gl_VertexIndex` and draw `Draw(6, 1, 0, 0)`; `gl_Position = vec4(uv * 2.0 - 1.0, 0, 1)` with uv (0,0) at the top-left draws it upright.
7. **sRGB vs UNorm** [RUN]: `R8G8B8A8SRGB` for colour art, `R8G8B8A8UNorm` for data (normals, masks).
8. **C# detail:** taking `&localStruct` inside a local function or lambda that captured it fails with CS1686. Copy it into a fresh local first.
9. **Teardown:** destroy the descriptor pool (frees its sets), set layout, sampler, view, then dispose the image.

### 7.31 Vulkan splash: logo + credits line as a textured quad [RUN]
The licence's credits notice and unaltered logo (`shipping.md`) in an app with no text renderer. Runnable copy: `Sandbox/VulkanSplash` (off-screen). Upload and sampling are 7.30.
1. **Compose once on the CPU** into one opaque-black RGBA canvas: the logo with `Stb.Load(path, 4)` (T1: a missing file returns a null image, so check `IsNull()`), the credits with SDL_ttf. Use `LOGO_BRUTAL_WHT.png` on a dark canvas; the logo must stay unaltered, so scale the whole canvas at draw time, never the logo alone.
2. **Credits text without a window or SDL_Init:** `SdlTtf.Init()`, `SdlTtf.OpenFont(path, 18)`, `SdlTtf.RenderTextBlended(font, text, 0, white)`, `Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Abgr8888)` (RGBA byte order), then copy rows using `rgba->pitch` (SDL may pad rows), `DestroySurface` on both, `CloseFont`, `SdlTtf.Quit()`. Sdl.Ttf ships no font (T39): embed an OFL font (the `brutal-3d` template has one) and ship its `OFL.txt`, or bake `credits.png` once and embed it.
3. **Draw:** no vertex buffer. Push constants `{ float4 Rect (x0, y0, x1, y1 in NDC); float4 Params (x = fade) }`; the vertex shader does `gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, uv), 0, 1)` with uv from `CORNERS[gl_VertexIndex]`, the fragment shader `vec4(texture(tex, vUv).rgb * pc.params.x, 1)`. Letterbox: `scale = min(0.8 * W / canvasW, 0.8 * H / canvasH, 1.5)`, half-size in NDC `(canvasW * scale / W, canvasH * scale / H)`. Recompute it each frame so a resize keeps the aspect ratio.
4. **Sequence:** fade in and out over a couple of seconds before the first scene frame. There's no Vulkan game template.

### 7.40 Slang: shader source to SPIR-V [RUN]
Runnable copy: `RecipeCheck 7.40` (`Sandbox/RecipeCheck/Recipes.Slang.cs`), Windows and Linux. Shaped by B11 and B12.
```csharp
var global = Slang.CreateGlobalSession();                         // throws SlangException; Try* forms return Result
ProfileID profile;
fixed (byte* p = "spirv_1_5"u8) profile = global.FindProfile(p);  // byte* APIs: u8 literals end in a hidden NUL
var target = new TargetDesc { StructureSize = (ByteSize64)sizeof(TargetDesc), Format = CompileTarget.Spirv, Profile = profile };
var desc = new SessionDesc { StructureSize = (ByteSize64)sizeof(SessionDesc), Targets = &target, TargetCount = 1 }; // no constructor sets StructureSize
global.CreateSession(ref desc, out var session).ThrowIfFailure();
var module = session.LoadModuleFromSourceString("doubler", "doubler.slang", source, out var diag); // null + diagnostics on a compile error
IEntryPoint entry;
fixed (byte* n = "main"u8) module!.FindEntryPointByName(n, out entry).ThrowIfFailure();  // needs [shader("compute")] etc.
entry.Link(out var linked, out _).ThrowIfFailure();               // link the entry point alone; it pulls in its module (not the composite, B12)
linked.GetEntryPointCode(0, 0, out var code, out _).ThrowIfFailure();
ReadOnlySpan<byte> spirv = BlobBytes(code);                       // NOT code.AsSpan() on Windows (B11); BlobBytes is in the recipe
```
- `BlobBytes` reads the blob through its vtable: `ComWrappers.TryGetComInstance(blob, out var unk)`, `Marshal.QueryInterface` for `typeof(ISlangBlob).GUID`, then slots 3 (`getBufferPointer`) and 4 (`getBufferSize`) as `delegate* unmanaged[MemberFunction]`. Use it for diagnostics blobs too, or they read as empty on Windows.
- COM objects have no `Dispose`; the GC releases them.
- Not run: `CreateCompileRequest`, reflection.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T2 | Vulkan `sType` is set **only** by `new T()`. `default`, `stackalloc` and `new T[n]` leave it 0, causing validation errors or crashes | Always `new VkFoo { … }`; `arr[i] = new VkFoo { … }` |
| T3 | Vulkan outputs typed `ReadOnlySpan<T>` are still written (`CreateComputePipelines`, `AllocateDescriptorSets`) | Pass a writable span you own (`stackalloc`) |
| T4 | Every failing `VkResult` **throws `VkResultError`** (`.Result`), including `ErrorOutOfDateKHR` from acquire/present (`VkDeviceExtensions.gen.cs:8361`). The ImGui backend's checks for it are dead code, so a resize crashes (B7) | `catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) { RecreateSwapchain(); }` |
| T5 | The Vulkan loader is `vulkan-1.dll` from **System32 only** (`ImportResolver.cs:21`). Touching any `Vulkan.*` static (even `Vulkan.Log`) runs that loader; on failure you get `TypeInitializationException`. The packaged `VulkanEx.dll` isn't the loader | Install the Vulkan runtime. Keep Vulkan types off code paths that must run without it |
| T28 | Slang: no CPU/host-callable, LLVM or WGSL→SPIR-V targets; DXIL/Metal lib/PTX need your own toolchains | SPIR-V or source text |

| ID | Where (under `BRUTAL/`) | Bug | Workaround |
|---|---|---|---|
| B11 | `Brutal.Slang/Generated/Interfaces.gen.cs:46` (`ISlangBlob.GetBufferSize` returns `ByteSize64`) | **Windows: every `ISlangBlob` reads as 0 bytes**, so `AsSpan()` is empty for SPIR-V and diagnostics alike, with success results. (struct-return ABI mismatch); Linux is fine [RUN: 7.40] | Read the size through the vtable (`BlobBytes` in 7.40) |
| B12 | `Brutal.Slang/ISessionExtensions.cs:34` + `Slang.GetComInstance` | `CreateCompositeComponentType(span, ...)` kills the process (access violation on Windows, SIGSEGV on Linux): `GetComInstance` hands Slang a managed wrapper, not its own object [RUN: 7.40] | Link the entry point alone, or pass native pointers from `ComWrappers.TryGetComInstance` + `QueryInterface` to the interface method |

Also B7 (`glfw-imgui.md`): the ImGui Vulkan backend's out-of-date checks never run.
