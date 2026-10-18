// VulkanDepth: skill recipe 7.29. Off-screen (no window): depth image + depth-tested render pass, vertex and index
// buffers, push constants carrying an MVP matrix, and a pixel read-back that proves each piece works.
// Scene: a small near green quad is drawn BEFORE a big far red quad, so only the depth test keeps the red from
// covering the green. The same scene is drawn again with the depth test off as a control (red must win).
// Usage: dotnet run -c Release --project Sandbox/VulkanDepth
// Exit codes: 0 = all checks passed, 1 = a check failed, 2 = no usable GPU / Vulkan error.
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
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
    const int Size = 256;
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

    // ---- depth format: PhysicalDeviceExtensions.FindSupportedFormat does the loop over candidates ----
    var depthFormat = physical.FindSupportedFormat(
        [VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm],
        VkImageTiling.Optimal,
        VkFormatFeatureFlags.DepthStencilAttachmentBit
    );
    Expect(depthFormat != VkFormat.Undefined, $"a depth format is supported ({depthFormat})");
    var depthAspect = VkImageAspectFlags.DepthBit;
    if (depthFormat == VkFormat.D24UNormS8UInt)
        depthAspect |= VkImageAspectFlags.StencilBit;
    var colorFormat = VkFormat.R8G8B8A8UNorm;

    // ---- images: colour (read back) and depth ----
    using var colorImage = device.CreateImage(
        new ImageEx.CreateInfo
        {
            Name = "color",
            ImageType = VkImageType._2D,
            ImageFormat = colorFormat,
            ImageExtent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
        }
    );
    using var depthImage = device.CreateImage(
        new ImageEx.CreateInfo
        {
            Name = "depth",
            ImageType = VkImageType._2D,
            ImageFormat = depthFormat,
            ImageExtent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
        }
    );
    var colorView = device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = colorImage.VkImage, ViewType = VkImageViewType._2D, Format = colorFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        },
        null
    );
    var depthView = device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = depthImage.VkImage, ViewType = VkImageViewType._2D, Format = depthFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = depthAspect, LevelCount = 1, LayerCount = 1 },
        },
        null
    );

    // ---- render pass: colour + depth ----
    var attachments = stackalloc VkAttachmentDescription[2];
    attachments[0] = new VkAttachmentDescription
    {
        Format = colorFormat, Samples = VkSampleCountFlags._1Bit,
        LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
        StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
        InitialLayout = VkImageLayout.Undefined, FinalLayout = VkImageLayout.TransferSrcOptimal, // read back next
    };
    attachments[1] = new VkAttachmentDescription
    {
        Format = depthFormat, Samples = VkSampleCountFlags._1Bit,
        LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.DontCare,
        StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
        InitialLayout = VkImageLayout.Undefined, FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
    };
    var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
    var depthRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
    var subpass = new VkSubpassDescription
    {
        PipelineBindPoint = VkPipelineBindPoint.Graphics,
        ColorAttachmentCount = 1, ColorAttachments = &colorRef,
        DepthStencilAttachment = &depthRef,
    };
    var dependencies = stackalloc VkSubpassDependency[2];
    dependencies[0] = new VkSubpassDependency
    {
        SrcSubpass = VK.SUBPASS_EXTERNAL, DstSubpass = 0,
        SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
        DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
        DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
    };
    dependencies[1] = new VkSubpassDependency // make the colour writes visible to the read-back copy
    {
        SrcSubpass = 0, DstSubpass = VK.SUBPASS_EXTERNAL,
        SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = VkPipelineStageFlags.TransferBit,
        SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit,
    };
    var renderPass = device.CreateRenderPass(
        new VkRenderPassCreateInfo
        {
            AttachmentCount = 2, Attachments = attachments, SubpassCount = 1, Subpasses = &subpass,
            DependencyCount = 2, Dependencies = dependencies,
        },
        null
    );
    var views = stackalloc VkImageView[2] { colorView, depthView };
    var framebuffer = device.CreateFramebuffer(
        new VkFramebufferCreateInfo { RenderPass = renderPass, AttachmentCount = 2, Attachments = views, Width = Size, Height = Size, Layers = 1 },
        null
    );

    // ---- vertex + index buffers (host visible), a unit quad in the XY plane facing +Z, counter-clockwise ----
    using var vb = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "vertices", BufferSize = ByteSize.Of<float3>(4), BufferUsage = VkBufferUsageFlags.VertexBufferBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );
    using var ib = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "indices", BufferSize = ByteSize.Of<ushort>(6), BufferUsage = VkBufferUsageFlags.IndexBufferBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );
    using (var m = vb.Map())
    {
        var v = m.AsSpan<float3>();
        v[0] = new float3(-0.5f, -0.5f, 0);
        v[1] = new float3(0.5f, -0.5f, 0);
        v[2] = new float3(0.5f, 0.5f, 0);
        v[3] = new float3(-0.5f, 0.5f, 0);
    }
    using (var m = ib.Map())
    {
        var i = m.AsSpan<ushort>();
        i[0] = 0; i[1] = 1; i[2] = 2; i[3] = 2; i[4] = 3; i[5] = 0;
    }
    using var readback = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "readback", BufferSize = ByteSize.Of<uint>(Size * Size), BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );

    // ---- shaders: the MVP and the colour arrive as push constants (80 bytes) ----
    var vert = CompileShader(
        """
        #version 450
        layout(push_constant) uniform Push { mat4 mvp; vec4 color; } pc;
        layout(location = 0) in vec3 inPos;
        void main() { gl_Position = pc.mvp * vec4(inPos, 1.0); }
        """u8,
        ShaderKind.VertexShader
    );
    var frag = CompileShader(
        """
        #version 450
        layout(push_constant) uniform Push { mat4 mvp; vec4 color; } pc;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = pc.color; }
        """u8,
        ShaderKind.FragmentShader
    );
    var vs = ShaderModuleExtensions.FromSpirv(device, vert, null);
    var fs = ShaderModuleExtensions.FromSpirv(device, frag, null);
    var pushRange = new VkPushConstantRange
    {
        StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Offset = default, Size = ByteSize.Of<Push>(),
    };
    var layout = device.CreatePipelineLayout([], [pushRange], null);

    VkPipeline MakePipeline(bool depthTest)
    {
        fixed (byte* entry = "main"u8)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vs, Name = entry };
            stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fs, Name = entry };
            var binding = new VkVertexInputBindingDescription { Binding = 0, Stride = ByteSize.Of<float3>(), InputRate = VkVertexInputRate.Vertex };
            var attribute = new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = default };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1, VertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 1, VertexAttributeDescriptions = &attribute,
            };
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkViewport { Width = Size, Height = Size, MaxDepth = 1 };
            var scissor = new VkRect2D { Extent = new VkExtent2D { Width = Size, Height = Size } };
            var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, Viewports = &viewport, ScissorCount = 1, Scissors = &scissor };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.BackBit, FrontFace = VkFrontFace.CounterClockwise, LineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depthState = new VkPipelineDepthStencilStateCreateInfo
            {
                DepthTestEnable = depthTest, DepthWriteEnable = depthTest, DepthCompareOp = VkCompareOp.Less,
            };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
            return device.CreateGraphicsPipeline(
                default,
                new VkGraphicsPipelineCreateInfo
                {
                    StageCount = 2, Stages = stages, VertexInputState = &vertexInput, InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState, RasterizationState = &raster, MultisampleState = &multisample,
                    DepthStencilState = &depthState, ColorBlendState = &blend, Layout = layout, RenderPass = renderPass, Subpass = 0,
                },
                null
            );
        }
    }

    // ---- matrices: Numerics is row-vector, so mvp = model * view * proj; flip Y for Vulkan ----
    var proj = float4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1f, 0.1f, 50f); // depth already maps to 0..1
    proj.M22 = -proj.M22;
    var view = float4x4.Identity; // camera at the origin looking down -Z
    Push MakePush(float4x4 model, float4 color) => new() { Mvp = model * view * proj, Color = color };

    var commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
    var cmd = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary });
    var fence = device.CreateFence(default, null);

    uint[] Render(VkPipeline pipeline)
    {
        device.ResetCommandPool(commandPool, VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var clears = stackalloc VkClearValue[2];
        clears[0] = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.05f, 0.2f, 1) } };
        clears[1] = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1 } };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = renderPass, Framebuffer = framebuffer,
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = Size, Height = Size } },
                ClearValueCount = 2, ClearValues = clears,
            },
            VkSubpassContents.Inline
        );
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        cmd.BindVertexBuffer(0, vb.VkBuffer);
        cmd.BindIndexBuffer(ib.VkBuffer, default, VkIndexType.UInt16);
        var stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
        // 1. Back-facing blue quad (turned around Y), nearest. Culling must remove it.
        var blue = MakePush(float4x4.CreateRotationY(MathF.PI) * float4x4.CreateTranslation(-0.5f, -0.5f, -1f), new float4(0, 0, 1, 1));
        cmd.PushConstants(layout, stages, ByteSize.Zero, blue);
        cmd.DrawIndexed(6, 1, 0, 0, 0);
        // 2. Near green quad, up and to the right of the centre.
        var green = MakePush(float4x4.CreateTranslation(0.5f, 0.5f, -2f), new float4(0, 1, 0, 1));
        cmd.PushConstants(layout, stages, ByteSize.Zero, green);
        cmd.DrawIndexed(6, 1, 0, 0, 0);
        // 3. Big far red quad, drawn LAST: without a depth test it would cover the green.
        var red = MakePush(float4x4.CreateScale(3f) * float4x4.CreateTranslation(0, 0, -4f), new float4(1, 0, 0, 1));
        cmd.PushConstants(layout, stages, ByteSize.Zero, red);
        cmd.DrawIndexed(6, 1, 0, 0, 0);
        cmd.EndRenderPass(); // colour image is now TransferSrcOptimal
        cmd.CopyImageToBuffer(
            colorImage.VkImage, VkImageLayout.TransferSrcOptimal, readback.VkBuffer,
            [new VkBufferImageCopy
            {
                BufferOffset = default,
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

    // R8G8B8A8: byte 0 = red, 1 = green, 2 = blue, so the packed little-endian uint is 0xAABBGGRR.
    static (int R, int G, int B) Rgb(uint p) => ((int)p & 0xFF, (int)(p >> 8) & 0xFF, (int)(p >> 16) & 0xFF);
    static bool Near(uint p, int r, int g, int b)
    {
        var c = Rgb(p);
        return Math.Abs(c.R - r) < 12 && Math.Abs(c.G - g) < 12 && Math.Abs(c.B - b) < 12;
    }
    const int C = Size / 2;
    const int D = 40;

    var withDepth = MakePipeline(true);
    var px = Render(withDepth);
    Console.WriteLine("-- depth test on");
    Expect(Near(px[(C - D) * Size + C + D], 0, 255, 0), $"upper right is the near green quad ({Rgb(px[(C - D) * Size + C + D])}): depth test hides the later far quad, and +Y points up after the flip");
    Expect(Near(px[(C + D) * Size + C + D], 255, 0, 0), $"lower right is the far red quad ({Rgb(px[(C + D) * Size + C + D])}): the green quad is not mirrored in Y");
    Expect(Near(px[(C + D) * Size + C - D], 255, 0, 0), $"lower left is red, not blue ({Rgb(px[(C + D) * Size + C - D])}): the back-facing quad was culled");
    Expect(Near(px[2 * Size + 2], 13, 13, 51), $"the corner shows the clear colour ({Rgb(px[2 * Size + 2])})");

    var noDepth = MakePipeline(false);
    var px2 = Render(noDepth);
    Console.WriteLine("-- depth test off (control)");
    Expect(Near(px2[(C - D) * Size + C + D], 255, 0, 0), $"upper right is red without the depth test ({Rgb(px2[(C - D) * Size + C + D])}): the test above really was the depth buffer");

    device.WaitIdle();
    device.DestroyFence(fence, null);
    device.DestroyCommandPool(commandPool, null);
    device.DestroyPipeline(withDepth, null);
    device.DestroyPipeline(noDepth, null);
    device.DestroyPipelineLayout(layout, null);
    device.DestroyShaderModule(vs, null);
    device.DestroyShaderModule(fs, null);
    device.DestroyFramebuffer(framebuffer, null);
    device.DestroyRenderPass(renderPass, null);
    device.DestroyImageView(colorView, null);
    device.DestroyImageView(depthView, null);

    foreach (var f in failures)
        Console.WriteLine($"FAIL: {f}");
    Console.WriteLine(failures.Count == 0 ? "All checks passed." : $"{failures.Count} check(s) failed.");
    return failures.Count == 0 ? 0 : 1;
}

[StructLayout(LayoutKind.Sequential)]
struct Push
{
    public float4x4 Mvp;
    public float4 Color;
}
