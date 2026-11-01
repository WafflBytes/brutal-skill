using Brutal;
using Brutal.Numerics;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// --snapshot: plays a scripted session through the real drawing code and saves PNGs. It renders off-screen into a
/// Vulkan image and copies it back, so no window or display is needed, only a Vulkan GPU (recipe 7.29's off-screen
/// variant). Driven by game time, never frame count (T33). Exit code 0 = every shot saved and not blank.
/// </summary>
public static unsafe class SnapshotRun
{
    const VkFormat ColorFormat = VkFormat.R8G8B8A8UNorm; // reads back as RGBA bytes

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var failures = 0;
        using var gpu = new VkGpu(window: null);
        using var renderer = new Renderer(gpu, ColorFormat, VkImageLayout.TransferSrcOptimal);
        using var target = new OffscreenTarget(gpu, renderer, 1280, 720);
        var world = new World();
        var flow = new ScreenFlow();
        Console.WriteLine($"GPU: {gpu.GpuName}");

        // (time in seconds since start, input held until the next entry, screenshot taken at that time)
        (float At, InputState Input, string? Shot)[] script =
        [
            (0.0f, default, null),
            (0.8f, default, "01_splash.png"),
            (1.2f, new InputState(float2.Zero, Confirm: true), null), // skip is allowed after 1 s
            (1.25f, new InputState(new float2(1, 0), false), null),
            (2.5f, new InputState(new float2(0, 1), false), "02_play.png"),
            (3.5f, new InputState(new float2(-1, -1), false), "03_play.png"),
            (4.5f, default, "04_play.png"),
        ];

        var t = 0f;
        for (var i = 0; i < script.Length; i++)
        {
            var (at, input, shot) = script[i];
            // Run fixed steps until this entry's time, holding the previous entry's input.
            var held = i == 0 ? default : script[i - 1].Input;
            while (t + World.Dt * 0.5f < at)
            {
                flow.Step(World.Dt, held.Confirm);
                if (flow.Current == Screen.Play)
                    world.Step(held);
                held = held with { Confirm = false }; // edge inputs last one step
                t += World.Dt;
            }
            if (shot != null)
                failures += Shot(target, flow, world, false, Path.Combine(dir, shot), t);
        }
        if (flow.Current != Screen.Play)
        {
            Console.WriteLine("Script never left the splash screen.");
            failures++;
        }

        // The pause overlay over the last play frame, then the same frame at 1080p.
        failures += Shot(target, flow, world, true, Path.Combine(dir, "05_paused.png"), t);
        using var big = new OffscreenTarget(gpu, renderer, 1920, 1080);
        failures += Shot(big, flow, world, false, Path.Combine(dir, "06_play_1080p.png"), t);
        return failures;
    }

    static int Shot(OffscreenTarget target, ScreenFlow flow, World world, bool paused, string path, float t)
    {
        var rgba = target.Render(flow, View.Of(world), paused);
        Stb.WritePng(path, target.Width, target.Height, 4, rgba, target.Width * 4);
        var lit = 0;
        var colours = new HashSet<int>();
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40)
                lit++;
            if ((i & 63) == 0)
                colours.Add((rgba[i] >> 3) | (rgba[i + 1] >> 3 << 5) | (rgba[i + 2] >> 3 << 10));
        }
        var ok = lit > 5000 && colours.Count > 5;
        Console.WriteLine($"{(ok ? "saved" : "BLANK")} {Path.GetFileName(path)}: {lit} lit px, {colours.Count} colours, t={t:F2}s, screen={flow.Current}, marker={world.Marker}");
        return ok ? 0 : 1;
    }

    /// <summary>A colour + depth framebuffer for the renderer's off-screen render pass, plus a host buffer to read it back.</summary>
    sealed class OffscreenTarget : IDisposable
    {
        readonly VkGpu _gpu;
        readonly Renderer _renderer;
        readonly ImageEx _color, _depth;
        readonly VkImageView _colorView, _depthView;
        readonly VkFramebuffer _framebuffer;
        readonly BufferEx _readback;
        public int Width { get; }
        public int Height { get; }

        public OffscreenTarget(VkGpu gpu, Renderer renderer, int w, int h)
        {
            _gpu = gpu;
            _renderer = renderer;
            Width = w;
            Height = h;
            _color = gpu.CreateImage("snapshot colour", ColorFormat, w, h, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit);
            _colorView = gpu.CreateView(_color.VkImage, ColorFormat, VkImageAspectFlags.ColorBit);
            _depth = gpu.CreateImage("snapshot depth", gpu.DepthFormat, w, h, VkImageUsageFlags.DepthStencilAttachmentBit);
            _depthView = gpu.CreateView(_depth.VkImage, gpu.DepthFormat, gpu.DepthAspect);
            var attachments = stackalloc VkImageView[2] { _colorView, _depthView };
            _framebuffer = gpu.Device.CreateFramebuffer(
                new VkFramebufferCreateInfo { RenderPass = renderer.RenderPass, AttachmentCount = 2, Attachments = attachments, Width = w, Height = h, Layers = 1 },
                null
            );
            _readback = gpu.CreateHostBuffer("snapshot read-back", ByteSize.Of<uint>(w * h), VkBufferUsageFlags.TransferDstBit);
        }

        public byte[] Render(ScreenFlow flow, in View view, bool paused)
        {
            var v = view;
            _gpu.Submit(cmd =>
            {
                var extent = new VkExtent2D { Width = Width, Height = Height };
                _renderer.Record(cmd, _framebuffer, extent, flow, v, paused); // ends in TransferSrcOptimal
                cmd.CopyImageToBuffer(_color.VkImage, VkImageLayout.TransferSrcOptimal, _readback.VkBuffer,
                    [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = Width, Height = Height, Depth = 1 } }]);
            });
            var rgba = new byte[Width * Height * 4];
            using (var mapped = _readback.Map())
                mapped.AsSpan<byte>()[..rgba.Length].CopyTo(rgba);
            for (var i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255; // the PNG shows the image as presented, opaque
            return rgba;
        }

        public void Dispose()
        {
            var device = _gpu.Device;
            device.WaitIdle();
            _readback.Dispose();
            device.DestroyFramebuffer(_framebuffer, null);
            device.DestroyImageView(_depthView, null);
            _depth.Dispose();
            device.DestroyImageView(_colorView, null);
            _color.Dispose();
        }
    }
}
