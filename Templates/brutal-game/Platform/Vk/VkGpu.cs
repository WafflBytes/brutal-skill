using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Platform;

/// <summary>
/// One Vulkan instance, GPU, device and graphics queue, plus small helpers (host buffers, texture upload, GLSL ->
/// SPIR-V with ShaderC, one-off command submission). With a window it also owns the window's surface and picks a GPU
/// that can present to it; without one (--snapshot) it renders off-screen. Skill recipes 7.11, 7.13, 7.29, 7.30.
/// </summary>
public sealed unsafe class VkGpu : IDisposable
{
    public Instance Instance { get; }
    public VkSurfaceKHR Surface { get; }
    public PhysicalDevice Physical { get; private set; } = default!;
    public DeviceEx Device { get; private set; } = default!;
    public Queue Queue { get; private set; } = default!;
    public int Family { get; private set; }
    public VkFormat DepthFormat { get; private set; }
    public VkImageAspectFlags DepthAspect { get; private set; }
    public string GpuName { get; private set; } = "";

    readonly VkCommandPool _oneShotPool;

    /// <param name="window">The window to present to, or null for off-screen rendering.</param>
    public VkGpu(GlfwWindow? window)
    {
        var extensions = new HashSet<string>();
        if (window != null)
        {
            var names = Glfw.GetRequiredInstanceExtensions(out var count); // VK_KHR_surface + VK_KHR_win32_surface
            for (var i = 0; i < count; i++)
                extensions.Add(Marshal.PtrToStringUTF8((nint)names[i])!);
        }
        var layers = new HashSet<string>();
        if (Environment.GetEnvironmentVariable("BRUTAL_VK_VALIDATION") == "1" && VulkanHelpers.Validation.IsSupported)
            VulkanHelpers.AddValidationLayers(layers); // needs the Vulkan SDK; opt-in while developing
        // Touching Vulkan.* loads vulkan-1.dll from System32; without a Vulkan driver it throws (T5).
        Instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            }
        );
        try
        {
            if (window != null)
            {
                var result = Glfw.CreateWindowSurface(Instance.VkHandle, window, out var surface); // int VkResult
                if (result != 0)
                    throw new InvalidOperationException($"glfwCreateWindowSurface failed: {(VkResult)result}");
                Surface = new VkSurfaceKHR((nint)surface);
            }
            PickDevice(window != null);
            CreateDevice(window != null);
            _oneShotPool = Device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = Family }, null);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void PickDevice(bool present)
    {
        foreach (var discreteFirst in new[] { true, false })
        foreach (var pd in Instance.PhysicalDevices)
        {
            var props = pd.GetProperties();
            if (discreteFirst && props.DeviceType != VkPhysicalDeviceType.DiscreteGpu)
                continue;
            if (present && !pd.SupportsExtension("VK_KHR_swapchain"u8))
                continue;
            var family = pd.FindQueueFamily(VkQueueFlags.GraphicsBit);
            if (family < 0 || (present && !pd.GetSurfaceSupportKHR(family, Surface)))
                continue;
            Physical = pd;
            Family = family;
            GpuName = Marshal.PtrToStringUTF8((nint)props.DeviceName) ?? "?";
            return;
        }
        throw new NotSupportedException("No GPU with Vulkan graphics support was found. Updating the graphics driver may help.");
    }

    void CreateDevice(bool present)
    {
        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo { QueueFamilyIndex = Family, QueueCount = 1, QueuePriorities = &priority };
        var info = new DeviceEx.CreateInfo { QueueCreateInfos = [queueInfo] };
        if (present)
            info.Extensions.Add("VK_KHR_swapchain");
        Device = Physical.CreateDevice(info);
        Queue = Device.GetQueue(Family, 0);
        DepthFormat = Physical.FindSupportedFormat(
            [VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm],
            VkImageTiling.Optimal,
            VkFormatFeatureFlags.DepthStencilAttachmentBit
        );
        if (DepthFormat == VkFormat.Undefined)
            throw new NotSupportedException("The GPU supports no depth buffer format.");
        DepthAspect = DepthFormat == VkFormat.D24UNormS8UInt ? VkImageAspectFlags.DepthBit | VkImageAspectFlags.StencilBit : VkImageAspectFlags.DepthBit;
    }

    /// <summary>A UNorm 8-bit swapchain format if the surface offers one (pixels pass through unchanged), else the first.</summary>
    public VkSurfaceFormatKHR PickSurfaceFormat()
    {
        var n = Physical.GetSurfaceFormatsKHR_Count(Surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[n];
        Physical.GetSurfaceFormatsKHR_Write(Surface, formats);
        foreach (var f in formats)
            if (f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                return f;
        return formats[0];
    }

    public BufferEx CreateHostBuffer(string name, ByteSize size, VkBufferUsageFlags usage) =>
        Device.CreateBuffer(
            new BufferEx.CreateInfo
            {
                Name = name,
                BufferSize = size,
                BufferUsage = usage,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            }
        );

    public ImageEx CreateImage(string name, VkFormat format, int w, int h, VkImageUsageFlags usage) =>
        Device.CreateImage(
            new ImageEx.CreateInfo
            {
                Name = name,
                ImageType = VkImageType._2D,
                ImageFormat = format,
                ImageExtent = new VkExtent3D { Width = w, Height = h, Depth = 1 },
                ImageMipLevels = 1,
                ImageArrayLayers = 1,
                ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal,
                ImageUsage = usage,
                ImageInitialLayout = VkImageLayout.Undefined,
                AllocPreference = MemoryPreference.PreferGpu,
            }
        );

    public VkImageView CreateView(VkImage image, VkFormat format, VkImageAspectFlags aspect) =>
        Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
            null
        );

    /// <summary>Records commands with <paramref name="record"/>, submits them and waits. For uploads and read-backs, not per frame.</summary>
    public void Submit(Action<CommandBuffer> record)
    {
        Device.ResetCommandPool(_oneShotPool, VkCommandPoolResetFlags.None);
        var cmd = Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = _oneShotPool, Level = VkCommandBufferLevel.Primary });
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        record(cmd);
        cmd.End();
        var fence = Device.CreateFence(default, null);
        Queue.Submit([], [], [cmd], [], fence);
        Device.WaitForFence(fence, nint.MaxValue);
        Device.DestroyFence(fence, null);
    }

    /// <summary>An RGBA8 texture from tightly packed pixels, ready to sample (layout ShaderReadOnlyOptimal).</summary>
    public VkTexture UploadTexture(string name, ReadOnlySpan<byte> rgba, int w, int h)
    {
        const VkFormat format = VkFormat.R8G8B8A8UNorm; // UNorm: bytes reach the UNorm swapchain unchanged
        var image = CreateImage(name, format, w, h, VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit);
        using (var staging = CreateHostBuffer(name + " staging", ByteSize.Of<byte>(rgba.Length), VkBufferUsageFlags.TransferSrcBit))
        {
            using (var mapped = staging.Map())
                rgba.CopyTo(mapped.AsSpan<byte>());
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
            Submit(cmd =>
            {
                cmd.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, default, [], [],
                    [new VkImageMemoryBarrier { DstAccessMask = VkAccessFlags.TransferWriteBit, OldLayout = VkImageLayout.Undefined, NewLayout = VkImageLayout.TransferDstOptimal, SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, Image = image.VkImage, SubresourceRange = range }]);
                cmd.CopyBufferToImage(staging.VkBuffer, image.VkImage, VkImageLayout.TransferDstOptimal,
                    [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = w, Height = h, Depth = 1 } }]);
                cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, default, [], [],
                    [new VkImageMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit, OldLayout = VkImageLayout.TransferDstOptimal, NewLayout = VkImageLayout.ShaderReadOnlyOptimal, SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, Image = image.VkImage, SubresourceRange = range }]);
            });
        }
        return new VkTexture(image, CreateView(image.VkImage, format, VkImageAspectFlags.ColorBit), w, h);
    }

    public VkShaderModule CompileShader(ReadOnlySpan<byte> glsl, ShaderKind kind, ReadOnlySpan<byte> name)
    {
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
        options.SetOptimizationLevel(OptimizationLevel.Performance);
        using var result = compiler.Compile(glsl, kind, name, "main"u8, options); // throws ShaderException with the log
        var spirv = new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray();
        return ShaderModuleExtensions.FromSpirv(Device, spirv, null);
    }

    public void Dispose()
    {
        if (Device != null)
        {
            Device.WaitIdle();
            if (_oneShotPool.IsNotNull())
                Device.DestroyCommandPool(_oneShotPool, null);
            Device.Dispose();
        }
        if (Surface.IsNotNull())
            Instance.DestroySurfaceKHR(Surface, null); // after the swapchain and device, before the instance
        Instance?.Dispose();
    }
}

/// <summary>A sampled RGBA texture and its view. Dispose with <see cref="Destroy"/> while the device is alive.</summary>
public readonly record struct VkTexture(ImageEx Image, VkImageView View, int Width, int Height)
{
    public void Destroy(DeviceEx device)
    {
        device.DestroyImageView(View, null);
        Image.Dispose();
    }
}
