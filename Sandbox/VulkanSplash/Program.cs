// VulkanSplash: skill recipe 7.31. Off-screen (no window): the splash a raw Vulkan app needs for the BRUTAL licence.
// The official logo (unaltered) and the credits line are composed once on the CPU into one texture (the credits text
// is rasterised with SDL_ttf), uploaded as in 7.30, and drawn as a letterboxed screen-space quad with a fade factor.
// Read-back proves the quad bounds, that the logo and the credits text are really in the image, and the fade.
// Usage: dotnet run -c Release --project Sandbox/VulkanSplash [-- <png path to write the splash frame to>]
// Exit codes: 0 = all checks passed, 1 = a check failed, 2 = no usable GPU / Vulkan error / assets not found.
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;
using Brutal.ShaderCApi;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

const string Credits = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

try
{
    return Run(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "splash-640x400.png"));
}
catch (VkResultError e)
{
    Console.WriteLine($"Vulkan error: {e.Result}: {e.Message}");
    return 2;
}

static string? FindUp(string relative)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate))
            return candidate;
    }
    return null;
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

/// <summary>Alpha-blends tightly packed RGBA over the canvas (which is opaque black), centred horizontally at <paramref name="top"/>.</summary>
static unsafe void Blit(byte[] canvas, int canvasW, int canvasH, byte* src, int w, int h, int pitch, int top)
{
    var left = Math.Max(0, (canvasW - w) / 2);
    for (var y = 0; y < h && top + y < canvasH; y++)
        for (var x = 0; x < w && left + x < canvasW; x++)
        {
            var s = y * pitch + x * 4;
            var d = ((top + y) * canvasW + left + x) * 4;
            var a = src[s + 3];
            for (var c = 0; c < 3; c++)
                canvas[d + c] = (byte)((src[s + c] * a + canvas[d + c] * (255 - a)) / 255);
        }
}

/// <summary>Logo (PNG, via Stb) above the credits line (text, via SDL_ttf), both as RGBA over opaque black.</summary>
static unsafe byte[] ComposeSplash(string logoPath, string fontPath, string credits, int canvasW, int canvasH, int logoTop, int creditsTop)
{
    var canvas = new byte[canvasW * canvasH * 4];
    for (var i = 3; i < canvas.Length; i += 4)
        canvas[i] = 255;

    var logo = Stb.Load(logoPath, 4); // T1: a missing file gives a null image, not an exception
    if (logo.IsNull())
        throw new InvalidOperationException($"{logoPath} could not be decoded");
    try
    {
        fixed (byte* p = logo.AsSpan())
            Blit(canvas, canvasW, canvasH, p, logo.Width, logo.Height, logo.Width * 4, logoTop);
    }
    finally
    {
        logo.Dispose();
    }

    SdlTtf.Init();
    var font = SdlTtf.OpenFont(fontPath, 18);
    try
    {
        var white = new SdlColor { r = 255, g = 255, b = 255, a = 255 };
        var surface = SdlTtf.RenderTextBlended(font, credits, 0, white);
        var rgba = Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Abgr8888); // RGBA byte order
        Sdl.PInvoke.DestroySurface(surface);
        try
        {
            Blit(canvas, canvasW, canvasH, (byte*)rgba->pixels, rgba->w, rgba->h, rgba->pitch, creditsTop); // pitch may be padded
        }
        finally
        {
            Sdl.PInvoke.DestroySurface(rgba);
        }
    }
    finally
    {
        SdlTtf.CloseFont(font);
        SdlTtf.Quit();
    }
    return canvas;
}

static unsafe int Run(string pngOut)
{
    const int W = 640, H = 400; // not square, so the letterboxing is visible
    const int CanvasW = 760, CanvasH = 280, LogoTop = 24, CreditsTop = 232;
    var failures = new List<string>();
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (!ok)
            failures.Add(what);
    }

    var logoPath = FindUp(Path.Combine("BRUTAL", "docs", "branding", "LOGO_BRUTAL_WHT.png"));
    var fontPath = FindUp(Path.Combine("Templates", "brutal-3d", "Assets", "Fonts", "AtkinsonHyperlegible-Bold.ttf"));
    if (logoPath == null || fontPath == null)
    {
        Console.WriteLine("Assets not found: need the BRUTAL/ clone and Templates/brutal-3d above the tool's folder.");
        return 2;
    }
    var canvas = ComposeSplash(logoPath, fontPath, Credits, CanvasW, CanvasH, LogoTop, CreditsTop);

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

    // ---- texture upload (recipe 7.30) ----
    var texFormat = VkFormat.R8G8B8A8UNorm; // see 7.30: SRGB for a sRGB swapchain, UNorm for this UNorm target
    using var texture = device.CreateImage(
        new ImageEx.CreateInfo
        {
            Name = "splash", ImageType = VkImageType._2D, ImageFormat = texFormat,
            ImageExtent = new VkExtent3D { Width = CanvasW, Height = CanvasH, Depth = 1 },
            ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            ImageSharingMode = VkSharingMode.Exclusive, ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu,
        }
    );
    var colorRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
    using (var staging = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "splash staging", BufferSize = ByteSize.Of<byte>(canvas.Length), BufferUsage = VkBufferUsageFlags.TransferSrcBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    ))
    {
        using (var mapped = staging.Map())
            canvas.CopyTo(mapped.AsSpan<byte>());
        SubmitOnce(c =>
        {
            c.PipelineBarrier(
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, default, [], [],
                [new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit, OldLayout = VkImageLayout.Undefined, NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, Image = texture.VkImage, SubresourceRange = colorRange,
                }]
            );
            c.CopyBufferToImage(
                staging.VkBuffer, texture.VkImage, VkImageLayout.TransferDstOptimal,
                [new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D { Width = CanvasW, Height = CanvasH, Depth = 1 },
                }]
            );
            c.PipelineBarrier(
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, default, [], [],
                [new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal, NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, Image = texture.VkImage, SubresourceRange = colorRange,
                }]
            );
        });
    }
    var textureView = device.CreateImageView(
        new VkImageViewCreateInfo { Image = texture.VkImage, ViewType = VkImageViewType._2D, Format = texFormat, SubresourceRange = colorRange },
        null
    );

    // ---- render target (read back) and render pass ----
    var targetFormat = VkFormat.R8G8B8A8UNorm;
    using var target = device.CreateImage(
        new ImageEx.CreateInfo
        {
            Name = "target", ImageType = VkImageType._2D, ImageFormat = targetFormat,
            ImageExtent = new VkExtent3D { Width = W, Height = H, Depth = 1 },
            ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit, ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
            ImageInitialLayout = VkImageLayout.Undefined, AllocPreference = MemoryPreference.PreferGpu,
        }
    );
    var targetView = device.CreateImageView(
        new VkImageViewCreateInfo { Image = target.VkImage, ViewType = VkImageViewType._2D, Format = targetFormat, SubresourceRange = colorRange },
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
    var dependency = new VkSubpassDependency
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
        new VkFramebufferCreateInfo { RenderPass = renderPass, AttachmentCount = 1, Attachments = &tv, Width = W, Height = H, Layers = 1 },
        null
    );
    using var readback = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "readback", BufferSize = ByteSize.Of<uint>(W * H), BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );

    // ---- sampler, descriptor set, pipeline: the quad comes from gl_VertexIndex; rect (NDC) and fade arrive as push constants ----
    var sampler = device.CreateSampler(
        new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear, MinFilter = VkFilter.Linear, MipmapMode = VkSamplerMipmapMode.Nearest,
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
    var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
    var descriptorPool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
    Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1]; // output span (T3)
    device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = descriptorPool, DescriptorSetCount = 1, SetLayouts = &setLayout }, sets);
    var imageInfo = new VkDescriptorImageInfo { Sampler = sampler, ImageView = textureView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
    device.UpdateDescriptorSets(
        [new VkWriteDescriptorSet { DstSet = sets[0], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo }],
        []
    );
    var splashSet = sets[0]; // a stackalloc span can't be captured by the Draw local function
    var pushRange = new VkPushConstantRange
    {
        StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Offset = default, Size = ByteSize.Of<SplashPush>(),
    };
    var layout = device.CreatePipelineLayout([setLayout], [pushRange], null);

    var vs = ShaderModuleExtensions.FromSpirv(
        device,
        CompileShader(
            """
            #version 450
            layout(push_constant) uniform Push { vec4 rect; vec4 params; } pc;
            layout(location = 0) out vec2 vUv;
            const vec2 CORNERS[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
            void main() {
                vec2 uv = CORNERS[gl_VertexIndex];
                vUv = uv;
                gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, uv), 0.0, 1.0);
            }
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
            layout(push_constant) uniform Push { vec4 rect; vec4 params; } pc;
            layout(set = 0, binding = 0) uniform sampler2D tex;
            layout(location = 0) in vec2 vUv;
            layout(location = 0) out vec4 outColor;
            void main() { outColor = vec4(texture(tex, vUv).rgb * pc.params.x, 1.0); } // params.x = fade (1 = full)
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
        var viewport = new VkViewport { Width = W, Height = H, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = W, Height = H } };
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

    // Letterbox: 80% of the window, never more than 1.5x the texture's own size, aspect ratio kept.
    var scale = MathF.Min(MathF.Min(0.8f * W / CanvasW, 0.8f * H / CanvasH), 1.5f);
    var hx = CanvasW * scale / W; // half-size in NDC
    var hy = CanvasH * scale / H;

    uint[] Draw(float fade)
    {
        device.ResetCommandPool(commandPool, VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.05f, 0.2f, 1) } };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = renderPass, Framebuffer = framebuffer,
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = W, Height = H } },
                ClearValueCount = 1, ClearValues = &clear,
            },
            VkSubpassContents.Inline
        );
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        Span<VkDescriptorSet> bind = [splashSet];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, bind, []);
        cmd.PushConstants(layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero,
            new SplashPush { Rect = new float4(-hx, -hy, hx, hy), Params = new float4(fade, 0, 0, 0) });
        cmd.Draw(6, 1, 0, 0);
        cmd.EndRenderPass();
        cmd.CopyImageToBuffer(
            target.VkImage, VkImageLayout.TransferSrcOptimal, readback.VkBuffer,
            [new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = W, Height = H, Depth = 1 },
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

    // R8G8B8A8 packs little-endian as 0xAABBGGRR.
    static int Max3(uint p) => Math.Max((int)p & 0xFF, Math.Max((int)(p >> 8) & 0xFF, (int)(p >> 16) & 0xFF));
    // Canvas row -> frame row, and the count/sum of lit pixels in a frame-row band inside the quad.
    int FrameRow(int canvasRow) => (int)(H / 2f - hy * H / 2f + canvasRow * scale);
    (int Lit, long Sum) Band(uint[] px, int fromRow, int toRow, int minBrightness)
    {
        var lit = 0;
        long sum = 0;
        var x0 = (int)(W / 2f - hx * W / 2f) + 2; // only inside the quad: the clear colour outside would count too
        var x1 = (int)(W / 2f + hx * W / 2f) - 2;
        for (var y = fromRow; y < toRow; y++)
            for (var x = x0; x < x1; x++)
            {
                var m = Max3(px[y * W + x]);
                sum += m;
                if (m >= minBrightness)
                    lit++;
            }
        return (lit, sum);
    }

    var full = Draw(1f);
    var left = (int)(W / 2f - hx * W / 2f);
    var top = FrameRow(0);
    Console.WriteLine($"-- splash at {W}x{H}: quad {(int)(2 * hx * W / 2)}x{(int)(2 * hy * H / 2)} px, scale {scale:F3}");
    Expect(Max3(full[2 * W + 2]) is > 40 and < 70 && (full[2 * W + 2] & 0xFF) < 30, "outside the quad shows the clear colour (dark blue)");
    Expect(Max3(full[(top + 4) * W + left + 4]) == 0, "inside the quad, empty canvas is black: the quad is where the letterbox says");
    var logo = Band(full, FrameRow(LogoTop), FrameRow(LogoTop + 168), 200);
    var credits = Band(full, FrameRow(CreditsTop), FrameRow(CreditsTop + 30), 100);
    Expect(logo.Lit > 800, $"the white logo is drawn ({logo.Lit} bright pixels)");
    Expect(credits.Lit > 150, $"the credits line is drawn ({credits.Lit} lit pixels)");
    var gap = Band(full, FrameRow(LogoTop + 168 + 4), FrameRow(CreditsTop - 4), 50);
    Expect(gap.Lit == 0, $"the band between logo and credits is empty ({gap.Lit} lit pixels): nothing overlaps");

    var half = Draw(0.5f);
    var logoHalf = Band(half, FrameRow(LogoTop), FrameRow(LogoTop + 168), 0);
    var ratio = (double)logoHalf.Sum / Band(full, FrameRow(LogoTop), FrameRow(LogoTop + 168), 0).Sum;
    Expect(ratio is > 0.45 and < 0.55, $"fade 0.5 halves the brightness (ratio {ratio:F3})");

    // For a human: the full-brightness frame as a PNG.
    var bytes = new byte[W * H * 4];
    for (var i = 0; i < W * H; i++)
        BitConverter.GetBytes(full[i] | 0xFF000000u).CopyTo(bytes, i * 4);
    Stb.WritePng(pngOut, W, H, 4, bytes, W * 4);
    Console.WriteLine($"Wrote {pngOut}");

    device.WaitIdle();
    device.DestroyPipeline(pipeline, null);
    device.DestroyShaderModule(vs, null);
    device.DestroyShaderModule(fs, null);
    device.DestroyPipelineLayout(layout, null);
    device.DestroyDescriptorPool(descriptorPool, null);
    device.DestroyDescriptorSetLayout(setLayout, null);
    device.DestroySampler(sampler, null);
    device.DestroyFramebuffer(framebuffer, null);
    device.DestroyRenderPass(renderPass, null);
    device.DestroyImageView(targetView, null);
    device.DestroyImageView(textureView, null);
    device.DestroyFence(fence, null);
    device.DestroyCommandPool(commandPool, null);

    foreach (var f in failures)
        Console.WriteLine($"FAIL: {f}");
    Console.WriteLine(failures.Count == 0 ? "All checks passed." : $"{failures.Count} check(s) failed.");
    return failures.Count == 0 ? 0 : 1;
}

[StructLayout(LayoutKind.Sequential)]
struct SplashPush
{
    public float4 Rect; // x0, y0, x1, y1 in NDC
    public float4 Params; // x = fade
}
