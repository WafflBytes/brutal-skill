# Vulkan, VMA, ShaderC, Slang

Read this for Vulkan (instance, devices, compute, windowed rendering/swapchain) and for shader compilation (GLSL/HLSL via ShaderC, Slang).

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

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
    - To recreate: wait while the framebuffer is 0x0 (minimised; `Glfw.WaitEvents()`), `device.WaitIdle()`, destroy the views, framebuffers and semaphores, then create the swapchain again with `OldSwapchain`.
11. **Read-back for tests:** create the swapchain with `ImageUsage = ColorAttachmentBit | TransferSrcBit` (check `cap.SupportedUsageFlags`). After `EndRenderPass`, barrier `PresentSrcKHR -> TransferSrcOptimal`, `CopyImageToBuffer` into a host-visible `BufferEx`, and barrier back to `PresentSrcKHR`. `B8G8R8A8UNorm` reads back in BGRA byte order.

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
