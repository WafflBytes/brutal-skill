using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// GLFW window, Vulkan swapchain, main loop, input, pause and sounds for interactive play. Main thread only (T16).
/// The swapchain follows skill recipe 7.13: rebuild on any of three resize signals, reset the fence only after a
/// successful acquire, one render-done semaphore per swapchain image, and never block while minimised.
/// </summary>
public sealed unsafe partial class App : IDisposable
{
    const int FramesInFlight = 2;

    readonly GlfwWindow _window;
    readonly VkGpu _gpu;
    readonly VkSurfaceFormatKHR _format;
    readonly Renderer _renderer;
    readonly Audio _audio;
    readonly World _world = new();
    readonly ScreenFlow _flow = new();
    View _prevView;
    View _currView;

    // Rebuilt with the swapchain.
    VkSwapchainKHR _swapchain;
    VkExtent2D _extent;
    VkImageView[] _views = [];
    VkFramebuffer[] _framebuffers = [];
    VkSemaphore[] _renderDone = []; // one per swapchain image (7.13)
    ImageEx _depthImage;
    VkImageView _depthView;
    int _recreations;

    // Per frame in flight.
    readonly VkCommandPool[] _pools = new VkCommandPool[FramesInFlight];
    readonly CommandBuffer[] _cmds = new CommandBuffer[FramesInFlight];
    readonly VkFence[] _fences = new VkFence[FramesInFlight];
    readonly VkSemaphore[] _imageReady = new VkSemaphore[FramesInFlight];
    int _frame;

    bool _running = true;
    bool _paused;

    // Input comes only from GLFW callbacks: held keys are tracked from press/release, presses are edge-triggered and
    // consumed by the next fixed step. --smoke posts real key messages to the window, so the same path runs.
    readonly HashSet<GlfwKey> _held = [];
    bool _confirmPressed;

    // --smoke state.
    readonly float _smokeSeconds; // > 0: scripted run that exits by itself
    float _elapsed; // seconds of fixed steps run (counts while paused too)
    int _frames;
    int _smokeStep;
    readonly List<string> _smokeFailures = [];
    float _smokeMark;
    long _smokeTicks;

    bool Minimised => _window.FramebufferSize.X == 0 || _window.FramebufferSize.Y == 0;

    public App(float smokeSeconds = 0)
    {
        _smokeSeconds = smokeSeconds;
        if (!Glfw.Init()) // returns false instead of throwing (T10)
            throw new InvalidOperationException("GLFW couldn't initialise");
        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0); // no GL context: Vulkan draws
        Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "BrutalGame", Size = new int2(1280, 720) });
        _window.OnKey += OnKey;
        _window.OnMouseButton += (_, _, action, _) => _confirmPressed |= action == GlfwButtonAction.Press;
        _window.OnFocus += (_, e) =>
        {
            if (_smokeSeconds <= 0 && e == GlfwFocusEvent.LostFocus) // --smoke drives focus loss itself, see SmokeScript
                OnFocusLost();
        };
        try
        {
            _gpu = new VkGpu(_window);
            _format = _gpu.PickSurfaceFormat();
            _renderer = new Renderer(_gpu, _format.Format, VkImageLayout.PresentSrcKHR);
            for (var i = 0; i < FramesInFlight; i++)
            {
                _pools[i] = _gpu.Device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = _gpu.Family }, null);
                _cmds[i] = _gpu.Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = _pools[i], Level = VkCommandBufferLevel.Primary });
                _fences[i] = _gpu.Device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
                _imageReady[i] = _gpu.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            }
            CreateSwapchain();
        }
        catch
        {
            Dispose(); // the caller's using never sees a half-built app
            throw;
        }
        _audio = Audio.OpenDevice(gain: smokeSeconds > 0 ? 0 : 1); // --smoke exercises audio, muted
        if (!_audio.Enabled)
            Console.WriteLine($"Audio disabled: {_audio.DisabledReason}");
        _prevView = _currView = View.Of(_world);
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var acc = 0.0;
        while (_running && !_window.ShouldClose)
        {
            var now = clock.Elapsed.TotalSeconds;
            acc += Math.Min(now - last, 0.1); // clamp long stalls (window drag, breakpoint)
            last = now;
            if (_smokeSeconds > 0)
                SmokeScript();
            Glfw.PollEvents();

            var steps = 0;
            while (acc >= World.Dt && steps < 4)
            {
                FixedStep();
                acc -= World.Dt;
                steps++;
            }
            if (steps == 4)
                acc = 0; // too far behind: drop time rather than spiral

            DrawFrame(View.Lerp(_prevView, _currView, _paused ? 1f : (float)(acc / World.Dt)));
            _frames++;
        }
        _gpu.Device.WaitIdle();
    }

    void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action == GlfwKeyAction.Release)
        {
            _held.Remove(key);
            return;
        }
        if (action != GlfwKeyAction.Press) // ignore auto-repeat
            return;
        _held.Add(key);
        switch (key)
        {
            case GlfwKey.Escape:
                _running = false;
                break;
            case GlfwKey.Enter or GlfwKey.Space:
                _confirmPressed = true;
                break;
            case GlfwKey.P when _flow.Current == Screen.Play:
                _paused = !_paused;
                break;
        }
    }

    void OnFocusLost()
    {
        // Alt-Tab: stop the game and forget held keys, whose release goes to the other window.
        _held.Clear();
        if (_flow.Current == Screen.Play)
            _paused = true;
    }

    void FixedStep()
    {
        var input = ReadInput();
        _elapsed += World.Dt;
        _confirmPressed = false;
        if (_paused)
            return;
        var before = _flow.Current;
        _flow.Step(World.Dt, input.Confirm);
        if (before == Screen.Splash && _flow.Current == Screen.Play)
            _audio.Play(Sfx.Start);
        if (_flow.Current != Screen.Play)
            return;
        if (before == Screen.Splash)
            input = input with { Confirm = false }; // the key that skipped the splash isn't also a game input
        _prevView = _currView;
        _world.Step(input);
        _currView = View.Of(_world);
        // Play sounds here from per-step events your World reports (e.g. a PickupsThisStep counter).
    }

    InputState ReadInput()
    {
        var move = float2.Zero;
        if (_held.Contains(GlfwKey.Left) || _held.Contains(GlfwKey.A))
            move.X -= 1;
        if (_held.Contains(GlfwKey.Right) || _held.Contains(GlfwKey.D))
            move.X += 1;
        if (_held.Contains(GlfwKey.Up) || _held.Contains(GlfwKey.W))
            move.Y -= 1;
        if (_held.Contains(GlfwKey.Down) || _held.Contains(GlfwKey.S))
            move.Y += 1;
        return new InputState(move, _confirmPressed);
    }

    // ---- swapchain (recipe 7.13) --------------------------------------------------------------------------------

    void CreateSwapchain()
    {
        var device = _gpu.Device;
        var oldSwapchain = _swapchain;
        var cap = _gpu.Physical.GetSurfaceCapabilitiesKHR(_gpu.Surface);
        _extent = cap.CurrentExtent.Width != -1 ? cap.CurrentExtent : new VkExtent2D { Width = _window.FramebufferSize.X, Height = _window.FramebufferSize.Y };
        var imageCount = Math.Max(cap.MinImageCount, 3);
        if (cap.MaxImageCount != 0)
            imageCount = Math.Min(imageCount, cap.MaxImageCount);
        _swapchain = device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _gpu.Surface,
                MinImageCount = imageCount,
                ImageFormat = _format.Format,
                ImageColorSpace = _format.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = cap.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = VkPresentModeKHR.FifoKHR, // vsync; always supported
                Clipped = true,
                OldSwapchain = oldSwapchain,
            },
            null
        );
        if (oldSwapchain.IsNotNull())
            device.DestroySwapchainKHR(oldSwapchain, null);

        var count = device.GetSwapchainImagesKHR_Count(_swapchain);
        var images = new VkImage[count];
        device.GetSwapchainImagesKHR_Write(_swapchain, images);
        _depthImage = _gpu.CreateImage("depth", _gpu.DepthFormat, _extent.Width, _extent.Height, VkImageUsageFlags.DepthStencilAttachmentBit);
        _depthView = _gpu.CreateView(_depthImage.VkImage, _gpu.DepthFormat, _gpu.DepthAspect);
        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        _renderDone = new VkSemaphore[count];
        var attachments = stackalloc VkImageView[2];
        for (var i = 0; i < count; i++)
        {
            _views[i] = _gpu.CreateView(images[i], _format.Format, VkImageAspectFlags.ColorBit);
            attachments[0] = _views[i];
            attachments[1] = _depthView;
            _framebuffers[i] = device.CreateFramebuffer(
                new VkFramebufferCreateInfo { RenderPass = _renderer.RenderPass, AttachmentCount = 2, Attachments = attachments, Width = _extent.Width, Height = _extent.Height, Layers = 1 },
                null
            );
            _renderDone[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    void DestroySwapchainObjects()
    {
        var device = _gpu.Device;
        foreach (var fb in _framebuffers)
            device.DestroyFramebuffer(fb, null);
        foreach (var v in _views)
            device.DestroyImageView(v, null);
        foreach (var s in _renderDone)
            device.DestroySemaphore(s, null);
        _framebuffers = [];
        _views = [];
        _renderDone = [];
        if (_depthView.IsNotNull())
            device.DestroyImageView(_depthView, null);
        _depthView = default;
        if (!_depthImage.IsNull())
            _depthImage.Dispose();
        _depthImage = default;
    }

    void RecreateSwapchain()
    {
        if (Minimised)
            return; // a swapchain can't be 0x0; DrawFrame skips frames until the window is restored
        _gpu.Device.WaitIdle();
        DestroySwapchainObjects();
        CreateSwapchain();
        _recreations++;
    }

    void DrawFrame(in View view)
    {
        if (Minimised)
        {
            Glfw.WaitEventsTimeout(0.05); // sleep, but keep the loop (Escape, close button, fixed steps) alive
            return;
        }
        var fb = _window.FramebufferSize;
        if (fb.X != _extent.Width || fb.Y != _extent.Height)
            RecreateSwapchain(); // catches a resize before the driver reports it (7.13)

        var device = _gpu.Device;
        var f = _frame % FramesInFlight;
        device.WaitForFences([_fences[f]], true, nint.MaxValue);
        int imageIndex;
        var needRecreate = false;
        try
        {
            if (device.AcquireNextImageKHR(_swapchain, nint.MaxValue, _imageReady[f], default, out imageIndex) == VkResult.SuboptimalKHR)
                needRecreate = true; // usable this frame; rebuild after presenting
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) // T4: thrown, not returned
        {
            RecreateSwapchain();
            return; // fence not reset, so it stays signalled
        }
        device.ResetFences([_fences[f]]); // only after a successful acquire

        var cmd = _cmds[f];
        device.ResetCommandPool(_pools[f], VkCommandPoolResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        _renderer.Record(cmd, _framebuffers[imageIndex], _extent, _flow, view, _paused);
        cmd.End();
        Span<VkPipelineStageFlags> waitStage = [VkPipelineStageFlags.ColorAttachmentOutputBit];
        _gpu.Queue.Submit([_imageReady[f]], waitStage, [cmd], [_renderDone[imageIndex]], _fences[f]);

        var renderDone = _renderDone[imageIndex];
        var swapchain = _swapchain;
        try
        {
            var r = _gpu.Queue.PresentKHR(new VkPresentInfoKHR { WaitSemaphoreCount = 1, WaitSemaphores = &renderDone, SwapchainCount = 1, Swapchains = &swapchain, ImageIndices = &imageIndex });
            if (r == VkResult.SuboptimalKHR)
                needRecreate = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            needRecreate = true;
        }
        _frame++;
        if (needRecreate)
            RecreateSwapchain();
    }

    // ---- --smoke ----------------------------------------------------------------------------------------------

    /// <summary>
    /// --smoke: runs the real window loop (Vulkan, swapchain, presents, audio muted) for a few seconds and drives it
    /// with key messages posted to the window, so input, pause and resize run as they do for a player.
    /// Exit code 0 = every scripted check passed.
    /// </summary>
    public int RunSmoke()
    {
        Run();
        if (_smokeStep < SmokeSteps)
            _smokeFailures.Add($"script ran {_smokeStep}/{SmokeSteps} steps; use --smoke 4 or longer");
        Console.WriteLine($"Smoke: {_frames} frames in {_elapsed:F1} s game time, GPU {_gpu.GpuName}, {_format.Format}, swapchain rebuilt {_recreations}x, audio {(_audio.Enabled ? "on (muted)" : "unavailable")}");
        foreach (var f in _smokeFailures)
            Console.WriteLine($"Smoke FAIL: {f}");
        Console.WriteLine(_smokeFailures.Count == 0 ? "Smoke: all scripted checks passed." : $"Smoke: {_smokeFailures.Count} check(s) failed.");
        return _smokeFailures.Count;
    }

    const int SmokeSteps = 9;

    // Windows virtual-key and scan codes for the keys the script presses.
    const int VkReturn = 0x0D, ScReturn = 0x1C, VkD = 0x44, ScD = 0x20, VkP = 0x50, ScP = 0x19;

    /// <summary>Scripted by game time (T33), one step per entry, each waiting for its start time.</summary>
    void SmokeScript()
    {
        (float At, Action Do)[] script =
        [
            (1.2f, () => Key(VkReturn, ScReturn)), // skip the splash (allowed after 1 s)
            (1.4f, () => { _smokeMark = _world.Marker.X; Post(VkD, ScD, down: true); }),
            (1.9f, () => { Post(VkD, ScD, down: false); Expect(_world.Marker.X > _smokeMark + 1, $"holding D moved the marker ({_smokeMark:F1} -> {_world.Marker.X:F1})"); }),
            (2.0f, () => _window.Size = new int2(1600, 900)),
            (2.2f, () => { Expect(_recreations > 0 && _extent.Height > 720, $"the swapchain was rebuilt for the bigger window ({_recreations}x, {_extent.Width}x{_extent.Height})"); Key(VkP, ScP); }),
            (2.3f, () => { Expect(_paused, "P paused the game"); _smokeTicks = _world.Ticks; }),
            (2.7f, () => { Expect(_world.Ticks == _smokeTicks, "the world doesn't step while paused"); Key(VkP, ScP); }),
            // GLFW has no synthetic events, and real focus changes are ignored in --smoke, so call the handler directly.
            (2.9f, () => { Expect(!_paused && _world.Ticks > _smokeTicks, "P resumed the game"); OnFocusLost(); }),
            (3.1f, () => { Expect(_paused, "losing focus paused the game"); Key(VkP, ScP); _running = _elapsed < _smokeSeconds; }),
        ];
        if (_smokeStep < script.Length && _elapsed >= script[_smokeStep].At)
            script[_smokeStep++].Do();
        if (_elapsed >= _smokeSeconds)
            _running = false;
    }

    void Expect(bool ok, string what)
    {
        Console.WriteLine($"Smoke {(ok ? "PASS" : "FAIL")}: {what}");
        if (!ok)
            _smokeFailures.Add(what);
    }

    void Key(int virtualKey, int scanCode)
    {
        Post(virtualKey, scanCode, down: true);
        Post(virtualKey, scanCode, down: false);
    }

    /// <summary>Posts a real WM_KEYDOWN/WM_KEYUP to the window; GLFW turns it into OnKey on the next PollEvents.</summary>
    void Post(int virtualKey, int scanCode, bool down)
    {
        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        var lParam = down ? (nint)(1 | (scanCode << 16)) : (nint)(1 | (scanCode << 16) | (1 << 30) | unchecked((int)0x80000000));
        PostMessageW(Glfw.PInvoke.GetWin32Window(_window), down ? WM_KEYDOWN : WM_KEYUP, virtualKey, lParam);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    public void Dispose()
    {
        _audio?.Dispose();
        if (_gpu?.Device != null)
        {
            var device = _gpu.Device;
            device.WaitIdle();
            DestroySwapchainObjects();
            if (_swapchain.IsNotNull())
                device.DestroySwapchainKHR(_swapchain, null); // before the surface (VkGpu.Dispose)
            for (var i = 0; i < FramesInFlight; i++)
            {
                if (_imageReady[i].IsNotNull())
                    device.DestroySemaphore(_imageReady[i], null);
                if (_fences[i].IsNotNull())
                    device.DestroyFence(_fences[i], null);
                if (_pools[i].IsNotNull())
                    device.DestroyCommandPool(_pools[i], null);
            }
            _renderer?.Dispose();
        }
        _gpu?.Dispose();
        _window?.Dispose();
        Glfw.Terminate();
    }
}
