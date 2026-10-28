// VulkanWindow: skill recipe 7.13, windowed Vulkan end to end. GLFW window + surface, swapchain, render pass,
// a triangle, frames in flight, a live resize with swapchain recreation, and a read-back of the presented image.
// Usage: dotnet run -c Release --project Sandbox/VulkanWindow
// Exit codes: 0 = all checks passed, 1 = a check failed, 2 = no usable GPU / Vulkan or GLFW error.
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

try
{
    using var app = new VulkanTriangle();
    return app.Run();
}
catch (VkResultError e)
{
    Console.WriteLine($"Vulkan error: {e.Result}: {e.Message}");
    return 2;
}

sealed unsafe class VulkanTriangle : IDisposable
{
    const int FramesInFlight = 2;
    readonly List<string> _failures = [];

    GlfwWindow _window = default!;
    Instance _instance = default!;
    VkSurfaceKHR _surface;
    PhysicalDevice _physical = default!;
    DeviceEx _device = default!;
    Queue _queue = default!;
    int _family;
    VkSurfaceFormatKHR _format;
    VkPresentModeKHR _presentMode;

    // Swapchain-dependent objects: rebuilt on every resize.
    VkSwapchainKHR _swapchain;
    VkExtent2D _extent;
    VkRenderPass _renderPass;
    VkImage[] _images = [];
    VkImageView[] _views = [];
    VkFramebuffer[] _framebuffers = [];
    VkSemaphore[] _renderDone = []; // one per swapchain image: a present may still hold the previous one

    // Per frame in flight.
    readonly VkCommandPool[] _pools = new VkCommandPool[FramesInFlight];
    readonly CommandBuffer[] _cmds = new CommandBuffer[FramesInFlight];
    readonly VkFence[] _fences = new VkFence[FramesInFlight];
    readonly VkSemaphore[] _imageReady = new VkSemaphore[FramesInFlight];

    VkPipelineLayout _layout;
    VkPipeline _pipeline;
    BufferEx _readback = default!;
    int _frame;
    int _recreations;
    int _outOfDateThrown;
    int _suboptimal;

    public int Run()
    {
        if (!Glfw.Init())
        {
            Console.WriteLine("GLFW init failed");
            return 2;
        }
        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0); // no GL context: Vulkan draws
        Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "VulkanWindow", Size = new int2(640, 400) });

        CreateInstanceAndSurface();
        if (!PickDevice())
            return 2;
        CreateDevice();
        CreatePerFrame();
        CreatePipelineLayoutAndReadback();
        CreateSwapchain();

        // 1. Render at the start size and read the presented image back.
        RenderFrames(30);
        CheckPixels("before resize");
        var before = _extent;

        // 2. Resize the window. Keep rendering; the swapchain must be rebuilt for the new size.
        _window.Size = new int2(900, 560);
        RenderFrames(60);
        Expect(_extent.Width != before.Width || _extent.Height != before.Height, $"swapchain extent followed the resize ({before.Width}x{before.Height} -> {_extent.Width}x{_extent.Height})");
        if (!_sizeCheck)
        {
            Console.WriteLine($"--no-size-check: the driver signalled the resize: ErrorOutOfDateKHR thrown {_outOfDateThrown}x, SuboptimalKHR returned {_suboptimal}x; swapchain is {_extent.Width}x{_extent.Height}, window {_window.FramebufferSize.X}x{_window.FramebufferSize.Y}");
            return 0;
        }
        Expect(_recreations >= 1, $"swapchain recreated {_recreations} time(s)");
        CheckPixels("after resize");

        // 3. Minimise: the loop must keep running (no frames presented) and still react to a close request.
        // Iconify is a request: the OS may take a while, or refuse while another app is in front. Wait for the
        // framebuffer to reach 0x0 first; if it never does, report SKIP rather than a false PASS or FAIL.
        _window.Iconify();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!Minimised && wait.ElapsedMilliseconds < 3000)
        {
            Glfw.WaitEventsTimeout(0.05);
            if (wait.ElapsedMilliseconds is > 1000 and < 1060)
                _window.Iconify(); // ask once more
        }
        if (!Minimised)
            Console.WriteLine($"SKIP minimise checks: the OS didn't minimise the window within 3 s (framebuffer {_window.FramebufferSize.X}x{_window.FramebufferSize.Y})");
        else
        {
            var presentedWhileMinimised = 0;
            var loops = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500 && !_window.ShouldClose)
            {
                Glfw.PollEvents();
                if (DrawFrame())
                    presentedWhileMinimised++;
                loops++;
                if (sw.ElapsedMilliseconds > 500)
                    _window.ShouldClose = true; // what the close button does, while minimised
            }
            _device.WaitIdle();
            Console.WriteLine($"minimised after {wait.ElapsedMilliseconds} ms");
            Expect(presentedWhileMinimised == 0, $"nothing presented while minimised ({presentedWhileMinimised} frames, {loops} loop turns)");
            Expect(_window.ShouldClose && sw.ElapsedMilliseconds < 1200, $"a close request while minimised ends the loop ({sw.ElapsedMilliseconds} ms)");
            _window.ShouldClose = false;
        }
        _window.Restore();
        for (var i = 0; i < 100 && Minimised; i++)
            Glfw.WaitEventsTimeout(0.02);
        RenderFrames(30);
        CheckPixels("after restore");

        Console.WriteLine($"Frames: {_frame}; recreations {_recreations}; ErrorOutOfDateKHR thrown {_outOfDateThrown}x; SuboptimalKHR returned {_suboptimal}x; present mode {_presentMode}; format {_format.Format}");
        foreach (var f in _failures)
            Console.WriteLine($"FAIL: {f}");
        Console.WriteLine(_failures.Count == 0 ? "All checks passed." : $"{_failures.Count} check(s) failed.");
        return _failures.Count == 0 ? 0 : 1;
    }

    void Expect(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (!ok)
            _failures.Add(what);
    }

    // ---- setup ------------------------------------------------------------------------------------------------

    void CreateInstanceAndSurface()
    {
        var extensions = new HashSet<string>();
        var names = Glfw.GetRequiredInstanceExtensions(out var count); // VK_KHR_surface + VK_KHR_win32_surface
        for (var i = 0; i < count; i++)
            extensions.Add(Marshal.PtrToStringUTF8((nint)names[i])!);
        var layers = new HashSet<string>();
        if (VulkanHelpers.Validation.IsSupported)
            VulkanHelpers.AddValidationLayers(layers); // only when the Vulkan SDK is installed
        Console.WriteLine($"Instance extensions: {string.Join(", ", extensions)}; validation layers: {(layers.Count > 0 ? "on" : "not installed")}");
        _instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            }
        );
        var result = Glfw.CreateWindowSurface(_instance.VkHandle, _window, out var surface); // int VkResult
        if (result != 0)
            throw new InvalidOperationException($"glfwCreateWindowSurface: {(VkResult)result}");
        _surface = new VkSurfaceKHR((nint)surface);
    }

    bool PickDevice()
    {
        foreach (var discreteFirst in new[] { true, false })
            foreach (var pd in _instance.PhysicalDevices)
            {
                if (discreteFirst && pd.GetProperties().DeviceType != VkPhysicalDeviceType.DiscreteGpu)
                    continue;
                if (!pd.SupportsExtension("VK_KHR_swapchain"u8))
                    continue;
                var family = pd.FindQueueFamily(VkQueueFlags.GraphicsBit);
                if (family < 0 || !pd.GetSurfaceSupportKHR(family, _surface))
                    continue; // a graphics queue that can't present to this surface
                _physical = pd;
                _family = family;
                var props = pd.GetProperties();
                Console.WriteLine($"GPU: {Marshal.PtrToStringUTF8((nint)props.DeviceName)}, queue family {family}");
                return true;
            }
        Console.WriteLine("No GPU with a graphics queue that can present to the window.");
        return false;
    }

    void CreateDevice()
    {
        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = _family, QueueCount = 1, QueuePriorities = &priority };
        _device = _physical.CreateDevice(new DeviceEx.CreateInfo { Extensions = { "VK_KHR_swapchain" }, QueueCreateInfos = [queueInfo] });
        _queue = _device.GetQueue(_family, 0);

        // Surface format: prefer B8G8R8A8_UNORM so the read-back bytes are predictable.
        var n = _physical.GetSurfaceFormatsKHR_Count(_surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[n];
        _physical.GetSurfaceFormatsKHR_Write(_surface, formats);
        _format = formats[0];
        foreach (var f in formats)
            if (f.Format == VkFormat.B8G8R8A8UNorm && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                _format = f;
        // FIFO is always available and is vsync; the frame loop's waits don't depend on it.
        _presentMode = VkPresentModeKHR.FifoKHR;
    }

    void CreatePerFrame()
    {
        for (var i = 0; i < FramesInFlight; i++)
        {
            _pools[i] = _device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = _family }, null);
            _cmds[i] = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = _pools[i], Level = VkCommandBufferLevel.Primary });
            _fences[i] = _device.CreateFence(VkFenceCreateFlags.SignaledBit, null); // signalled: the first wait passes
            _imageReady[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    void CreatePipelineLayoutAndReadback()
    {
        _layout = _device.CreatePipelineLayout([], [], null);
        _readback = _device.CreateBuffer(
            new BufferEx.CreateInfo
            {
                Name = "readback",
                BufferSize = ByteSize.Of<uint>(3),
                BufferUsage = VkBufferUsageFlags.TransferDstBit,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            }
        );
    }

    void CreateSwapchain()
    {
        var oldSwapchain = _swapchain;
        var cap = _physical.GetSurfaceCapabilitiesKHR(_surface);
        // CurrentExtent is the window's framebuffer size, or -1 when the app picks it.
        _extent = cap.CurrentExtent.Width != -1
            ? cap.CurrentExtent
            : new VkExtent2D { Width = _window.FramebufferSize.X, Height = _window.FramebufferSize.Y };
        var imageCount = Math.Max(cap.MinImageCount, 3);
        if (cap.MaxImageCount != 0)
            imageCount = Math.Min(imageCount, cap.MaxImageCount);
        if (!cap.SupportedUsageFlags.HasFlag(VkImageUsageFlags.TransferSrcBit))
            throw new NotSupportedException("swapchain images can't be read back on this GPU");

        _swapchain = _device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = _format.Format,
                ImageColorSpace = _format.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = cap.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = _presentMode,
                Clipped = true,
                OldSwapchain = oldSwapchain, // lets the driver hand resources over
            },
            null
        );
        if (oldSwapchain.IsNotNull())
            _device.DestroySwapchainKHR(oldSwapchain, null);

        var count = _device.GetSwapchainImagesKHR_Count(_swapchain);
        _images = new VkImage[count];
        _device.GetSwapchainImagesKHR_Write(_swapchain, _images);

        if (_renderPass.IsNull())
            CreateRenderPassAndPipeline(); // depends on the format only, so it survives resizes

        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        _renderDone = new VkSemaphore[count];
        for (var i = 0; i < count; i++)
        {
            _views[i] = _device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = _images[i],
                    ViewType = VkImageViewType._2D,
                    Format = _format.Format,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                },
                null
            );
            var view = _views[i];
            _framebuffers[i] = _device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    Attachments = &view,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                },
                null
            );
            _renderDone[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    void DestroySwapchainObjects()
    {
        foreach (var fb in _framebuffers)
            _device.DestroyFramebuffer(fb, null);
        foreach (var v in _views)
            _device.DestroyImageView(v, null);
        foreach (var s in _renderDone)
            _device.DestroySemaphore(s, null);
        _framebuffers = [];
        _views = [];
        _renderDone = [];
    }

    bool Minimised => _window.FramebufferSize.X == 0 || _window.FramebufferSize.Y == 0;

    void RecreateSwapchain()
    {
        // A minimised window has a 0x0 framebuffer and a swapchain can't be 0x0. Don't wait here: that would block
        // the main loop, so Close and Escape would be ignored until the window is restored. DrawFrame skips instead.
        if (Minimised)
            return;
        _device.WaitIdle(); // simplest correct option: nothing may still use the old images
        DestroySwapchainObjects();
        CreateSwapchain(); // passes the old swapchain as OldSwapchain, then destroys it
        _recreations++;
    }

    void CreateRenderPassAndPipeline()
    {
        var attachment = new VkAttachmentDescription
        {
            Format = _format.Format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR, // ready to present when the pass ends
        };
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &colorRef };
        // Wait for the acquire semaphore's stage before writing the image.
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
        };
        _renderPass = _device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = 1,
                Attachments = &attachment,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            },
            null
        );

        var vert = CompileShader(
            """
            #version 450
            vec2 positions[3] = vec2[](vec2(0.0, -0.8), vec2(0.8, 0.8), vec2(-0.8, 0.8));
            void main() { gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0); }
            """u8,
            ShaderKind.VertexShader
        );
        var frag = CompileShader(
            """
            #version 450
            layout(location = 0) out vec4 outColor;
            void main() { outColor = vec4(1.0, 0.5, 0.0, 1.0); }
            """u8,
            ShaderKind.FragmentShader
        );
        var vs = ShaderModuleExtensions.FromSpirv(_device, vert, null);
        var fs = ShaderModuleExtensions.FromSpirv(_device, frag, null);
        try
        {
            fixed (byte* entry = "main"u8)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
                stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vs, Name = entry };
                stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fs, Name = entry };
                var vertexInput = new VkPipelineVertexInputStateCreateInfo(); // positions come from the shader
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
                var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 }; // dynamic
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = VkCullModeFlags.None,
                    FrontFace = VkFrontFace.Clockwise,
                    LineWidth = 1,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
                // Dynamic viewport and scissor: the pipeline doesn't depend on the window size, so a resize
                // only rebuilds the swapchain, image views and framebuffers.
                var dynamics = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamics };
                _pipeline = _device.CreateGraphicsPipeline(
                    default,
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &inputAssembly,
                        ViewportState = &viewportState,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = _layout,
                        RenderPass = _renderPass,
                        Subpass = 0,
                    },
                    null
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(vs, null);
            _device.DestroyShaderModule(fs, null);
        }
    }

    static byte[] CompileShader(ReadOnlySpan<byte> glsl, ShaderKind kind)
    {
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
        using var result = compiler.Compile(glsl, kind, "shader.glsl"u8, "main"u8, options);
        return new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray();
    }

    // ---- frame loop -------------------------------------------------------------------------------------------

    /// <summary>--no-size-check: rely only on what the driver reports, to see whether it signals a resize.</summary>
    readonly bool _sizeCheck = !Environment.GetCommandLineArgs().Contains("--no-size-check");
    bool _captureNext;
    uint _capturedCentre;
    uint _capturedCorner;
    VkExtent2D _capturedExtent;

    void RenderFrames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Glfw.PollEvents();
            _captureNext = i == count - 1;
            DrawFrame();
        }
        _device.WaitIdle();
    }

    /// <summary>Renders one frame; returns false (and presents nothing) while the window is minimised.</summary>
    bool DrawFrame()
    {
        if (Minimised)
        {
            Glfw.WaitEventsTimeout(0.1); // sleep until something happens, but keep the loop (and ShouldClose) alive
            return false;
        }
        // A resize doesn't always produce ErrorOutOfDateKHR (Windows often keeps presenting a stretched image),
        // so compare the window's framebuffer size with the swapchain too.
        var fb = _window.FramebufferSize;
        if (_sizeCheck && (fb.X != _extent.Width || fb.Y != _extent.Height))
            RecreateSwapchain();

        var f = _frame % FramesInFlight;
        _device.WaitForFences([_fences[f]], true, nint.MaxValue);

        int imageIndex;
        try
        {
            var r = _device.AcquireNextImageKHR(_swapchain, nint.MaxValue, _imageReady[f], default, out imageIndex);
            if (r == VkResult.SuboptimalKHR)
                _suboptimal++; // still usable this frame; recreate after presenting
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) // T4: errors throw, never return
        {
            _outOfDateThrown++;
            RecreateSwapchain();
            return true; // the fence wasn't reset, so it stays signalled for the next try
        }
        _device.ResetFences([_fences[f]]); // only after a successful acquire, or the next wait deadlocks

        var cmd = _cmds[f];
        _device.ResetCommandPool(_pools[f], VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.05f, 0.2f, 1) } };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[imageIndex],
                RenderArea = new VkRect2D { Extent = _extent },
                ClearValueCount = 1,
                ClearValues = &clear,
            },
            VkSubpassContents.Inline
        );
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.SetViewport(0, [new VkViewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = _extent }]);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();
        if (_captureNext)
            RecordReadback(cmd, _images[imageIndex]);
        cmd.End();

        Span<VkPipelineStageFlags> waitStage = [VkPipelineStageFlags.ColorAttachmentOutputBit];
        _queue.Submit([_imageReady[f]], waitStage, [cmd], [_renderDone[imageIndex]], _fences[f]);

        var renderDone = _renderDone[imageIndex];
        var swapchain = _swapchain;
        var needRecreate = false;
        try
        {
            var r = _queue.PresentKHR(
                new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &renderDone,
                    SwapchainCount = 1,
                    Swapchains = &swapchain,
                    ImageIndices = &imageIndex,
                }
            );
            if (r == VkResult.SuboptimalKHR)
            {
                _suboptimal++;
                needRecreate = true;
            }
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            _outOfDateThrown++;
            needRecreate = true;
        }
        _frame++;
        if (_captureNext)
            FinishReadback();
        if (needRecreate)
            RecreateSwapchain();
        return true;
    }

    // Copies the centre pixel and a corner pixel of the rendered image into a host-visible buffer.
    void RecordReadback(CommandBuffer cmd, VkImage image)
    {
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        cmd.PipelineBarrier(
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.TransferBit,
            default,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
                    DstAccessMask = VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.PresentSrcKHR,
                    NewLayout = VkImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        cmd.CopyImageToBuffer(
            image,
            VkImageLayout.TransferSrcOptimal,
            _readback.VkBuffer,
            [
                new VkBufferImageCopy
                {
                    BufferOffset = default,
                    ImageSubresource = layers,
                    ImageOffset = new VkOffset3D { X = _extent.Width / 2, Y = _extent.Height / 2 },
                    ImageExtent = new VkExtent3D { Width = 1, Height = 1, Depth = 1 },
                },
                new VkBufferImageCopy
                {
                    BufferOffset = ByteSize.Of<uint>(1),
                    ImageSubresource = layers,
                    ImageOffset = new VkOffset3D { X = 2, Y = 2 },
                    ImageExtent = new VkExtent3D { Width = 1, Height = 1, Depth = 1 },
                },
            ]
        );
        cmd.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            default,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.TransferSrcOptimal,
                    NewLayout = VkImageLayout.PresentSrcKHR, // back to what present expects
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        _capturedExtent = _extent;
    }

    void FinishReadback()
    {
        _device.WaitIdle();
        using var mapped = _readback.Map();
        var px = mapped.AsSpan<uint>();
        _capturedCentre = px[0];
        _capturedCorner = px[1];
    }

    void CheckPixels(string when)
    {
        // B8G8R8A8: byte 0 = blue, 1 = green, 2 = red.
        static (int R, int G, int B) Rgb(uint p) => ((int)(p >> 16) & 0xFF, (int)(p >> 8) & 0xFF, (int)p & 0xFF);
        var c = Rgb(_capturedCentre);
        var k = Rgb(_capturedCorner);
        var bgra = _format.Format == VkFormat.B8G8R8A8UNorm;
        Console.WriteLine($"{when}: {_capturedExtent.Width}x{_capturedExtent.Height}, centre RGB {c}, corner RGB {k}{(bgra ? "" : $" (format {_format.Format}: byte order not checked)")}");
        if (!bgra)
            return;
        Expect(c.R > 230 && c.G is > 100 and < 160 && c.B < 30, $"{when}: the triangle is drawn at the centre");
        Expect(k.R < 30 && k.G < 30 && k.B is > 30 and < 80, $"{when}: the corner shows the clear colour");
    }

    public void Dispose()
    {
        if (_device != null)
        {
            _device.WaitIdle();
            DestroySwapchainObjects();
            if (_swapchain.IsNotNull())
                _device.DestroySwapchainKHR(_swapchain, null);
            _device.DestroyPipeline(_pipeline, null);
            _device.DestroyRenderPass(_renderPass, null);
            _device.DestroyPipelineLayout(_layout, null);
            for (var i = 0; i < FramesInFlight; i++)
            {
                _device.DestroySemaphore(_imageReady[i], null);
                _device.DestroyFence(_fences[i], null);
                _device.DestroyCommandPool(_pools[i], null);
            }
            if (!_readback.IsNull()) _readback.Dispose();
            _device.Dispose();
        }
        if (_instance != null)
        {
            if (_surface.IsNotNull())
                _instance.DestroySurfaceKHR(_surface, null); // after the swapchain, before the instance
            _instance.Dispose();
        }
        _window?.Dispose();
        Glfw.Terminate();
    }
}
