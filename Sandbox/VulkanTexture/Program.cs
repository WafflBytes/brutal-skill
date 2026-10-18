// VulkanTexture: skill recipe 7.30. Off-screen (no window): upload RGBA pixels to a GPU image through a staging
// buffer, transition layouts with barriers, make a view + sampler + descriptor set, and sample it in a fragment
// shader. Read-back proves orientation (no flip), nearest filtering, and the sRGB-vs-UNorm format difference.
// Usage: dotnet run -c Release --project Sandbox/VulkanTexture
// Exit codes: 0 = all checks passed, 1 = a check failed, 2 = no usable GPU / Vulkan error.
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

try
{
    return Run();
}
catch (VkResultError e)
{
    Console.WriteLine($"Vulkan error: {e.Result}: {e.Message}");
    return 2;
}

static byte[] CompileShader(ReadOnlySpan<byte> glsl, ShaderKind kind)
{
    using var compiler = ShaderC.CreateCompiler();
    using var options = ShaderC.CreateCompilerOptions();
    options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
    using var result = compiler.Compile(glsl, kind, "shader.glsl"u8, "main"u8, options);
    unsafe
    {
        return new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray();
    }
}

static unsafe int Run()
{
    const int Size = 64;
    var failures = new List<string>();
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (!ok)
            failures.Add(what);
    }

    using var instance = Vulkan.CreateInstance(
        new Instance.CreateInfo { AppInfo = new VkApplicationInfo { ApiVersion = (1 << 22) | (3 << 12) }, EnabledExtensions = [], EnabledLayers = [] }
    );
    if (instance.PhysicalDevices.Length == 0)
    {
        Console.WriteLine("No Vulkan physical device.");
        return 2;
    }
    var physical = instance.PhysicalDevices[0];
    foreach (var pd in instance.PhysicalDevices)
        if (pd.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu)
        {
            physical = pd;
            break;
        }
    var props = physical.GetProperties(); // a local: DeviceName is a fixed buffer
    Console.WriteLine($"GPU: {System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0')}");

    var family = physical.FindQueueFamily(VkQueueFlags.GraphicsBit);
    var priority = 1f;
    var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = 1, QueuePriorities = &priority };
    using var device = physical.CreateDevice(new DeviceEx.CreateInfo { QueueCreateInfos = [queueInfo] });
    var queue = device.GetQueue(family, 0);

    var commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
    var cmd = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary });
    var fence = device.CreateFence(default, null);

    void SubmitOnce(Action<CommandBuffer> record)
    {
        device.ResetCommandPool(commandPool, VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        record(cmd);
        cmd.End();
        queue.Submit([], [], [cmd], [], fence);
        device.WaitForFence(fence, nint.MaxValue);
        device.ResetFences([fence]);
    }

    // ---- THE RECIPE: upload RGBA bytes to a sampled image (staging buffer -> TransferDst -> ShaderReadOnly) ----
    (ImageEx Image, VkImageView View) CreateTexture(byte[] rgba, int width, int height, VkFormat format)
    {
        var image = device.CreateImage(
            new ImageEx.CreateInfo
            {
                Name = "texture",
                ImageType = VkImageType._2D,
                ImageFormat = format,
                ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                ImageMipLevels = 1,
                ImageArrayLayers = 1,
                ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal,
                ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                ImageInitialLayout = VkImageLayout.Undefined,
                AllocPreference = MemoryPreference.PreferGpu,
            }
        );
        using var staging = device.CreateBuffer(
            new BufferEx.CreateInfo
            {
                Name = "texture staging", BufferSize = ByteSize.Of<byte>(rgba.Length), BufferUsage = VkBufferUsageFlags.TransferSrcBit,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            }
        );
        using (var mapped = staging.Map())
            rgba.CopyTo(mapped.AsSpan<byte>());
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        SubmitOnce(c =>
        {
            c.PipelineBarrier(
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, default, [], [],
                [new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined, NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image.VkImage, SubresourceRange = range,
                }]
            );
            c.CopyBufferToImage(
                staging.VkBuffer, image.VkImage, VkImageLayout.TransferDstOptimal,
                [new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                }]
            );
            c.PipelineBarrier(
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, default, [], [],
                [new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal, NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image.VkImage, SubresourceRange = range,
                }]
            );
        });
        var view = device.CreateImageView(
            new VkImageViewCreateInfo { Image = image.VkImage, ViewType = VkImageViewType._2D, Format = format, SubresourceRange = range },
            null
        );
        return (image, view);
    }

    // ---- render target (read back) and a single-colour render pass ----
    var targetFormat = VkFormat.R8G8B8A8UNorm;
    using var target = device.CreateImage(
        new ImageEx.CreateInfo
        {
            Name = "target", ImageType = VkImageType._2D, ImageFormat = targetFormat,
            ImageExtent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
            ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
            ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu,
        }
    );
    var targetView = device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = target.VkImage, ViewType = VkImageViewType._2D, Format = targetFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        },
        null
    );
    var attachment = new VkAttachmentDescription
    {
        Format = targetFormat, Samples = VkSampleCountFlags._1Bit,
        LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
        StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
        InitialLayout = VkImageLayout.Undefined, FinalLayout = VkImageLayout.TransferSrcOptimal,
    };
    var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
    var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &colorRef };
    var dependency = new VkSubpassDependency // colour writes visible to the read-back copy
    {
        SrcSubpass = 0, DstSubpass = VK.SUBPASS_EXTERNAL,
        SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = VkPipelineStageFlags.TransferBit,
        SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit,
    };
    var renderPass = device.CreateRenderPass(
        new VkRenderPassCreateInfo { AttachmentCount = 1, Attachments = &attachment, SubpassCount = 1, Subpasses = &subpass, DependencyCount = 1, Dependencies = &dependency },
        null
    );
    var tv = targetView;
    var framebuffer = device.CreateFramebuffer(
        new VkFramebufferCreateInfo { RenderPass = renderPass, AttachmentCount = 1, Attachments = &tv, Width = Size, Height = Size, Layers = 1 },
        null
    );
    using var readback = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "readback", BufferSize = ByteSize.Of<uint>(Size * Size), BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );

    // ---- sampler, descriptor set layout/pool, pipeline: a full-target quad from gl_VertexIndex, no vertex buffer ----
    var sampler = device.CreateSampler(
        new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest, MinFilter = VkFilter.Nearest, MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge, AddressModeV = VkSamplerAddressMode.ClampToEdge, AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        },
        null
    );
    var binding = new VkDescriptorSetLayoutBinding
    {
        Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit,
    };
    var setLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
    var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 };
    var descriptorPool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 3, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
    var layout = device.CreatePipelineLayout([setLayout], [], null);

    VkDescriptorSet MakeSet(VkImageView view)
    {
        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1]; // output span (T3)
        var layoutCopy = setLayout; // a captured local can't have its address taken
        device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = descriptorPool, DescriptorSetCount = 1, SetLayouts = &layoutCopy }, sets);
        var imageInfo = new VkDescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        device.UpdateDescriptorSets(
            [new VkWriteDescriptorSet { DstSet = sets[0], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo }],
            []
        );
        return sets[0];
    }

    var vs = ShaderModuleExtensions.FromSpirv(
        device,
        CompileShader(
            """
            #version 450
            layout(location = 0) out vec2 vUv;
            const vec2 CORNERS[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
            void main() { vec2 uv = CORNERS[gl_VertexIndex]; vUv = uv; gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0); }
            """u8,
            ShaderKind.VertexShader
        ),
        null
    );
    var fs = ShaderModuleExtensions.FromSpirv(
        device,
        CompileShader(
            """
            #version 450
            layout(set = 0, binding = 0) uniform sampler2D tex;
            layout(location = 0) in vec2 vUv;
            layout(location = 0) out vec4 outColor;
            void main() { outColor = texture(tex, vUv); }
            """u8,
            ShaderKind.FragmentShader
        ),
        null
    );
    VkPipeline pipeline;
    fixed (byte* entry = "main"u8)
    {
        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vs, Name = entry };
        stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fs, Name = entry };
        var vertexInput = new VkPipelineVertexInputStateCreateInfo();
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
        var viewport = new VkViewport { Width = Size, Height = Size, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = Size, Height = Size } };
        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, Viewports = &viewport, ScissorCount = 1, Scissors = &scissor };
        var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, FrontFace = VkFrontFace.Clockwise, LineWidth = 1 };
        var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
        var blendAttachment = new VkPipelineColorBlendAttachmentState
        {
            ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
        };
        var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
        pipeline = device.CreateGraphicsPipeline(
            default,
            new VkGraphicsPipelineCreateInfo
            {
                StageCount = 2, Stages = stages, VertexInputState = &vertexInput, InputAssemblyState = &inputAssembly,
                ViewportState = &viewportState, RasterizationState = &raster, MultisampleState = &multisample,
                ColorBlendState = &blend, Layout = layout, RenderPass = renderPass, Subpass = 0,
            },
            null
        );
    }

    uint[] Draw(VkDescriptorSet set)
    {
        device.ResetCommandPool(commandPool, VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = new Brutal.Numerics.float4(0, 0, 0, 1) } };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = renderPass, Framebuffer = framebuffer,
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = Size, Height = Size } },
                ClearValueCount = 1, ClearValues = &clear,
            },
            VkSubpassContents.Inline
        );
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        Span<VkDescriptorSet> sets = [set];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, sets, []);
        cmd.Draw(6, 1, 0, 0);
        cmd.EndRenderPass();
        cmd.CopyImageToBuffer(
            target.VkImage, VkImageLayout.TransferSrcOptimal, readback.VkBuffer,
            [new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
            }]
        );
        var hostBarrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.HostReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.HostBit, default, [hostBarrier], [], []);
        cmd.End();
        queue.Submit([], [], [cmd], [], fence);
        device.WaitForFence(fence, nint.MaxValue);
        device.ResetFences([fence]);
        using var mapped = readback.Map();
        return mapped.AsSpan<uint>().ToArray();
    }

    // R8G8B8A8: byte 0 = red, so the packed little-endian uint is 0xAABBGGRR.
    static (int R, int G, int B) Rgb(uint p) => ((int)p & 0xFF, (int)(p >> 8) & 0xFF, (int)(p >> 16) & 0xFF);
    static bool Near(uint p, int r, int g, int b, int tol = 6)
    {
        var c = Rgb(p);
        return Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;
    }

    // 4x4 texture, 2x2-pixel blocks: top-left red, top-right green, bottom-left blue, bottom-right white (row 0 = top).
    var quad = new byte[4 * 4 * 4];
    for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var (r, g, b) = (y < 2, x < 2) switch
            {
                (true, true) => (255, 0, 0),
                (true, false) => (0, 255, 0),
                (false, true) => (0, 0, 255),
                _ => (255, 255, 255),
            };
            var o = (y * 4 + x) * 4;
            quad[o] = (byte)r; quad[o + 1] = (byte)g; quad[o + 2] = (byte)b; quad[o + 3] = 255;
        }
    var (quadImage, quadView) = CreateTexture(quad, 4, 4, VkFormat.R8G8B8A8UNorm);
    var px = Draw(MakeSet(quadView));
    Console.WriteLine("-- 4x4 quadrant texture, nearest sampler");
    Expect(Near(px[16 * Size + 16], 255, 0, 0), $"top-left is red ({Rgb(px[16 * Size + 16])}): row 0 of the upload is the top, no flip");
    Expect(Near(px[16 * Size + 48], 0, 255, 0), $"top-right is green ({Rgb(px[16 * Size + 48])})");
    Expect(Near(px[48 * Size + 16], 0, 0, 255), $"bottom-left is blue ({Rgb(px[48 * Size + 16])})");
    Expect(Near(px[48 * Size + 48], 255, 255, 255), $"bottom-right is white ({Rgb(px[48 * Size + 48])})");
    Expect(Near(px[(Size / 2 - 3) * Size + 16], 255, 0, 0) && Near(px[(Size / 2 + 3) * Size + 16], 0, 0, 255), "nearest filtering: a hard edge at the middle, no blending");

    // The same grey (128) as a UNorm and as an sRGB image: the sampler decodes sRGB to linear (about 55).
    var grey = new byte[] { 128, 128, 128, 255 };
    var (greyImage, greyView) = CreateTexture(grey, 1, 1, VkFormat.R8G8B8A8UNorm);
    var (srgbImage, srgbView) = CreateTexture(grey, 1, 1, VkFormat.R8G8B8A8SRGB);
    var unorm = Draw(MakeSet(greyView))[32 * Size + 32];
    var srgb = Draw(MakeSet(srgbView))[32 * Size + 32];
    Console.WriteLine("-- 1x1 grey 128");
    Expect(Near(unorm, 128, 128, 128), $"R8G8B8A8UNorm samples as written ({Rgb(unorm)})");
    Expect(Near(srgb, 55, 55, 55, 3), $"R8G8B8A8SRGB is decoded to linear when sampled ({Rgb(srgb)}): use SRGB for colour art, UNorm for data");

    device.WaitIdle();
    device.DestroyPipeline(pipeline, null);
    device.DestroyShaderModule(vs, null);
    device.DestroyShaderModule(fs, null);
    device.DestroyPipelineLayout(layout, null);
    device.DestroyDescriptorPool(descriptorPool, null); // frees the sets with it
    device.DestroyDescriptorSetLayout(setLayout, null);
    device.DestroySampler(sampler, null);
    device.DestroyFramebuffer(framebuffer, null);
    device.DestroyRenderPass(renderPass, null);
    device.DestroyImageView(targetView, null);
    foreach (var v in new[] { quadView, greyView, srgbView })
        device.DestroyImageView(v, null);
    quadImage.Dispose();
    greyImage.Dispose();
    srgbImage.Dispose();
    device.DestroyFence(fence, null);
    device.DestroyCommandPool(commandPool, null);

    foreach (var f in failures)
        Console.WriteLine($"FAIL: {f}");
    Console.WriteLine(failures.Count == 0 ? "All checks passed." : $"{failures.Count} check(s) failed.");
    return failures.Count == 0 ? 0 : 1;
}
