// Recipe 7.36: a Dear ImGui renderer on SDL_RenderGeometry (no OpenGL, no window needed). BRUTAL's own renderers
// are OpenGL and Vulkan only, so a game on the plain SDL renderer (the brutal-sdl template) needs this ~120-line one.
// Headless test: a software renderer on a surface, synthetic events through SdlImGuiGlue (7.35), pixel checks.
using Brutal;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.Pointers;
using Brutal.SdlApi;

/// <summary>Draws ImGui's ImDrawData with SDL_RenderGeometryRaw. Font atlas and any other ImTextureData become SDL textures.
/// Every ImTextureID is an SDL_Texture pointer, so your own textures go into ImGui.Image through TextureRef. Draw callbacks are called.</summary>
sealed unsafe class SdlRenderGeometryBackend : IRendererBackend
{
    const nint ResetRenderState = -8; // ImDrawCallback_ResetRenderState in imgui.h: a marker, not a function
    readonly SdlRenderer _renderer;
    readonly Dictionary<int, SdlTexture> _textures = []; // textures this backend made, by ImTextureData.UniqueID
    SdlFColor[] _colors = new SdlFColor[4096];

    /// <summary>For ImGui.Image / ImageButton / AddImage with a texture you created on the same renderer. You keep ownership.</summary>
    public static ImTextureRef TextureRef(SdlTexture texture) => new() { _TexID = new ImTextureID(texture.Pointer.Value) };

    public int TextureCreates, TextureUpdates, TextureDestroys;

    public SdlRenderGeometryBackend(SdlRenderer renderer)
    {
        _renderer = renderer;
        var io = ImGui.GetIO();
        // Both flags are required: without RendererHasTextures ImGui 1.92 expects a font atlas you build yourself.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
    }

    public void NewFrame() { }

    public void RenderDrawData(ImDrawDataPtr drawData)
    {
        var fbScale = drawData.FramebufferScale;
        if (drawData.DisplaySize.X * fbScale.X <= 0 || drawData.DisplaySize.Y * fbScale.Y <= 0)
            return;

        if (drawData.Textures.IsNotNull())
            for (var i = 0; i < drawData.Textures.Value.Count; i++)
            {
                var tex = drawData.Textures.Value[i];
                if (tex.Status != ImTextureStatus.OK)
                    UpdateTexture(tex);
            }

        var clipOff = drawData.DisplayPos;
        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var list = drawData.CmdLists[n];
            var vtx = (ImDrawVert*)list.VtxBuffer.Data;
            var idx = (ushort*)list.IdxBuffer.Data;
            var vtxCount = list.VtxBuffer.Count;
            if (_colors.Length < vtxCount)
                _colors = new SdlFColor[Math.Max(vtxCount, _colors.Length * 2)];
            for (var v = 0; v < vtxCount; v++) // ImDrawVert.col is packed ABGR (R in the low byte); SDL wants float RGBA
            {
                var c = vtx[v].col;
                _colors[v] = new SdlFColor { r = (c & 0xFF) / 255f, g = ((c >> 8) & 0xFF) / 255f, b = ((c >> 16) & 0xFF) / 255f, a = (c >> 24) / 255f };
            }

            for (var i = 0; i < list.CmdBuffer.Count; i++)
            {
                var cmd = list.CmdBuffer[i];
                if (cmd.UserCallback != 0)
                {
                    // ImDrawList.AddCallback: run it instead of drawing. It sees the clip rectangle of the command before it.
                    if (cmd.UserCallback == ResetRenderState)
                        Sdl.Render.SetRenderClipRect(_renderer, null);
                    else
                        ((delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)cmd.UserCallback)(list.Ptr, (ImDrawCmd*)list.CmdBuffer.Data + i);
                    continue;
                }
                var min = new float2((cmd.ClipRect.X - clipOff.X) * fbScale.X, (cmd.ClipRect.Y - clipOff.Y) * fbScale.Y);
                var max = new float2((cmd.ClipRect.Z - clipOff.X) * fbScale.X, (cmd.ClipRect.W - clipOff.Y) * fbScale.Y);
                if (max.X <= min.X || max.Y <= min.Y)
                    continue;
                var clip = new SdlRect { x = (int)min.X, y = (int)min.Y, w = (int)(max.X - min.X), h = (int)(max.Y - min.Y) };
                Sdl.Render.SetRenderClipRect(_renderer, &clip);

                SdlTexture texture = (void*)cmd.GetTexID().Value;
                var first = vtx + cmd.VtxOffset; // indices are relative to VtxOffset: shift the vertex base instead
                fixed (SdlFColor* colors = &_colors[cmd.VtxOffset])
                    if (!Sdl.Render.RenderGeometryRaw(
                        _renderer, texture,
                        (float*)&first->pos, sizeof(ImDrawVert),
                        colors, sizeof(SdlFColor),
                        (float*)&first->uv, sizeof(ImDrawVert),
                        (int)(vtxCount - cmd.VtxOffset),
                        idx + cmd.IdxOffset, (int)cmd.ElemCount, sizeof(ushort)))
                        throw new InvalidOperationException("RenderGeometryRaw failed: " + Sdl.PInvoke.GetError());
            }
        }
        Sdl.Render.SetRenderClipRect(_renderer, null);
    }

    void UpdateTexture(ImTextureDataPtr tex)
    {
        switch (tex.Status)
        {
            case ImTextureStatus.WantCreate:
            {
                var t = Sdl.Render.CreateTexture(_renderer, SdlPixelFormat.Abgr8888, SdlTextureAccess.Static, tex.Width, tex.Height); // Abgr8888 = RGBA byte order
                Sdl.Render.UpdateTexture(t, null, (void*)tex.GetPixels(), tex.Width * 4);
                Sdl.Render.SetTextureBlendMode(t, 1); // SDL_BLENDMODE_BLEND: straight alpha, like ImGui
                Sdl.Render.SetTextureScaleMode(t, SdlScaleMode.Linear);
                _textures[tex.UniqueID] = t;
                tex.SetTexID(new ImTextureID(t.Pointer.Value));
                tex.SetStatus(ImTextureStatus.OK);
                TextureCreates++;
                break;
            }
            case ImTextureStatus.WantUpdates:
            {
                var t = _textures[tex.UniqueID];
                for (var i = 0; i < tex.Updates.Count; i++)
                {
                    var r = tex.Updates[i];
                    var rect = new SdlRect { x = r.x, y = r.y, w = r.w, h = r.h };
                    Sdl.Render.UpdateTexture(t, &rect, (void*)tex.GetPixelsAt(r.x, r.y), tex.Width * 4); // pitch of the whole atlas
                }
                tex.SetStatus(ImTextureStatus.OK);
                TextureUpdates++;
                break;
            }
            case ImTextureStatus.WantDestroy when tex.UnusedFrames > 0:
                DestroyTexture(tex);
                break;
        }
    }

    void DestroyTexture(ImTextureDataPtr tex)
    {
        if (_textures.Remove(tex.UniqueID, out var t))
            Sdl.Render.DestroyTexture(t);
        tex.SetTexID(ImTextureID.Invalid);
        tex.SetStatus(ImTextureStatus.Destroyed);
        TextureDestroys++;
    }

    public void Dispose()
    {
        foreach (var tex in ImGui.GetPlatformIO().Textures)
            if (tex.RefCount == 1 && _textures.ContainsKey(tex.UniqueID))
                DestroyTexture(tex);
        foreach (var t in _textures.Values) // anything ImGui no longer lists
            Sdl.Render.DestroyTexture(t);
        _textures.Clear();
        var io = ImGui.GetIO();
        io.BackendFlags &= ~(ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures);
    }
}

static unsafe partial class Recipes
{
    public static void ImGuiSdlRenderRecipe()
    {
        const int W = 320, H = 200;
        Sdl.Init.Initialize(SdlInitFlags.Events);
        var target = Sdl.PInvoke.CreateSurface(W, H, SdlPixelFormat.Abgr8888);
        var renderer = Sdl.Render.CreateSoftwareRenderer(target);
        try
        {
            Sdl.Render.SetRenderDrawBlendMode(renderer, 1);
            ImGui.CreateContext();
            var io = ImGui.GetIO();
            io.IniFilename = default;
            ImGui.StyleColorsDark();
            var backend = new SdlRenderGeometryBackend(renderer);
            // Your own texture for ImGui.Image: 2x2 pure green, made on the same renderer
            var green = Sdl.Render.CreateTexture(renderer, SdlPixelFormat.Abgr8888, SdlTextureAccess.Static, 2, 2);
            var greenPixels = stackalloc uint[] { 0xFF00FF00, 0xFF00FF00, 0xFF00FF00, 0xFF00FF00 }; // ABGR: A=FF, G=FF
            Sdl.Render.UpdateTexture(green, null, greenPixels, 2 * 4);
            // A draw callback that draws with SDL directly (a magenta square) and checks the user data ImGui copied for it
            var callbackCalls = 0;
            var callbackData = 0;
            ImDrawCallback fill = (list, cmd) =>
            {
                callbackCalls++;
                callbackData = *(int*)cmd->UserCallbackData;
                Sdl.Render.SetRenderDrawColor(renderer, 255, 0, 255, 255);
                var r = new SdlFRect { x = 250, y = 160, w = 20, h = 20 };
                Sdl.Render.RenderFillRect(renderer, &r);
            };
            // The glue's NewFrame needs an SdlWindow; headless we set the display fields ourselves. Events still go through it.
            var glue = new SdlImGuiGlue(default);
            try
            {
                var clicked = 0;
                var open = true;
                float2 buttonC = default, imageC = default;
                var frame = 0;
                void Frame(string label)
                {
                    while (Sdl.Events.PollEvent(out var ev))
                        glue.ProcessEvent(in ev);
                    io.DisplaySize = new float2(W, H);
                    io.DisplayFramebufferScale = float2.One;
                    io.DeltaTime = 1f / 60f;
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(new float2(10, 10));
                    ImGui.SetNextWindowSize(new float2(150, 120)); // narrower than the long text below: tests clipping
                    ImGui.Begin("Debug"u8, ref open);
                    ImGui.Text($"{label} {frame}");
                    if (ImGui.Button("Click me"u8, new float2(100, 30)))
                        clicked++;
                    buttonC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    ImGui.Text("A very long line of text that must be clipped at the window edge"u8);
                    // A rect far wider than the window: ImGui does not cut primitives on the CPU, so only the clip rectangle stops it.
                    ImGui.GetWindowDrawList().AddRectFilled(new float2(20, 108), new float2(300, 118), new ImColor8(255, 0, 0));
                    ImGui.End();
                    ImGui.SetNextWindowPos(new float2(180, 140));
                    ImGui.SetNextWindowSize(new float2(130, 55));
                    ImGui.Begin("Image"u8, ImGuiWindowFlags.NoDecoration);
                    ImGui.Image(SdlRenderGeometryBackend.TextureRef(green), new float2(40, 40));
                    imageC = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    var drawList = ImGui.GetWindowDrawList();
                    drawList.AddCallback(fill, 42);
                    ImGui.PInvoke.ImDrawList_AddCallback(drawList.Ptr, -8, null, 0); // ImDrawCallback_ResetRenderState: must not be called
                    ImGui.End();
                    ImGui.Render();
                    Sdl.Render.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                    Sdl.Render.RenderClear(renderer);
                    backend.RenderDrawData(ImGui.GetDrawData());
                    Sdl.Render.FlushRenderer(renderer); // the surface is only written when the queue is flushed (RenderPresent or RenderReadPixels also do it)
                    frame++;
                }
                void Push(SdlEvent e) => Sdl.Events.PushEvent(&e);
                void Move(float2 p)
                {
                    var e = new SdlEvent();
                    e.motion.type = SdlEventType.MouseMotion;
                    e.motion.x = p.X;
                    e.motion.y = p.Y;
                    Push(e);
                }
                void Button(float2 p, bool down)
                {
                    var e = new SdlEvent();
                    e.button.type = down ? SdlEventType.MouseButtonDown : SdlEventType.MouseButtonUp;
                    e.button.button = 1;
                    e.button.down = (byte)(down ? 1 : 0);
                    e.button.x = p.X;
                    e.button.y = p.Y;
                    Push(e);
                }

                Frame("Frame");
                Frame("Frame");
                var pos = buttonC;
                Move(pos); Frame("Frame");
                Button(pos, true); Frame("Frame");
                Button(pos, false); Frame("Frame");
                Frame("Frame 0123456789"); // new glyphs on a later frame make ImGui ask for texture updates

                // Count lit pixels inside and right of the window (window spans x 10..160)
                var lit = 0;
                var litOutside = 0;
                uint buttonPixel = 0, imagePixel = 0, callbackPixel = 0;
                var rgba = Sdl.PInvoke.ConvertSurface(target, SdlPixelFormat.Abgr8888);
                try
                {
                    for (var y = 0; y < H; y++)
                        for (var x = 0; x < W; x++)
                        {
                            var px = *(uint*)((byte*)rgba->pixels + y * rgba->pitch + x * 4);
                            var r = px & 0xFF;
                            var g = (px >> 8) & 0xFF;
                            var b = (px >> 16) & 0xFF;
                            if (r > 30 || g > 30 || b > 30)
                            {
                                lit++;
                                var inImageWindow = x >= 178 && x < 312 && y >= 138 && y < 197; // the image window plus its 1-px border
                                if ((x >= 163 || y >= 133) && !inImageWindow)
                                    litOutside++;
                            }
                        }
                    buttonPixel = *(uint*)((byte*)rgba->pixels + (int)buttonC.Y * rgba->pitch + ((int)buttonC.X - 40) * 4);
                    imagePixel = *(uint*)((byte*)rgba->pixels + (int)imageC.Y * rgba->pitch + (int)imageC.X * 4);
                    callbackPixel = *(uint*)((byte*)rgba->pixels + 170 * rgba->pitch + 260 * 4);
                }
                finally
                {
                    Sdl.PInvoke.DestroySurface(rgba);
                }
                var shotDir = Environment.GetEnvironmentVariable("RECIPECHECK_SHOTS");
                if (!string.IsNullOrEmpty(shotDir))
                {
                    var copy = Sdl.PInvoke.ConvertSurface(target, SdlPixelFormat.Abgr8888);
                    Directory.CreateDirectory(shotDir);
                    Brutal.StbApi.Stb.WritePng(Path.Combine(shotDir, "7.36-imgui-sdl-renderer.png"), W, H, 4,
                        new ReadOnlySpan<byte>(copy->pixels, W * H * 4).ToArray(), copy->pitch);
                    Sdl.PInvoke.DestroySurface(copy);
                }
                var br = buttonPixel & 0xFF;
                var bb = (buttonPixel >> 16) & 0xFF;
                Console.WriteLine($"     ImGui on SDL_RenderGeometry: {lit} lit pixels ({litOutside} outside the window), clicked {clicked}x, button pixel r{br} b{bb}, textures: {backend.TextureCreates} created / {backend.TextureUpdates} updated");
                Check(lit > 1500, "the window was drawn");
                Check(litOutside == 0, $"nothing leaked past the window's clip rectangle ({litOutside} lit pixels)");
                Check(bb > br + 40, "button pixels are bluish (vertex colours and blending arrive intact)");
                Check(clicked == 1, "synthetic click through the glue made Button() return true once");
                Check(backend.TextureCreates == 1, "one font atlas texture created");
                Check(backend.TextureUpdates >= 1, "glyphs baked on demand were uploaded with UpdateTexture");
                Console.WriteLine($"     image pixel 0x{imagePixel:X8}, callback pixel 0x{callbackPixel:X8}, callback ran {callbackCalls}x with data {callbackData}");
                Check((imagePixel & 0xFFFFFF) == 0x00FF00, $"ImGui.Image drew your own SdlTexture (pixel 0x{imagePixel:X8}, want green)");
                Check(callbackCalls == frame && callbackData == 42, $"the draw callback ran once per frame with its user data ({callbackCalls} calls in {frame} frames, data {callbackData})");
                Check((callbackPixel & 0xFFFFFF) == 0xFF00FF, $"what the callback drew with SDL is in the frame (pixel 0x{callbackPixel:X8}, want magenta)");
            }
            finally
            {
                GC.KeepAlive(fill); // ImGui holds only its function pointer
                Sdl.Render.DestroyTexture(green);
                backend.Dispose();
                glue.Dispose();
                ImGui.DestroyContext();
            }
        }
        finally
        {
            Sdl.Render.DestroyRenderer(renderer);
            Sdl.PInvoke.DestroySurface(target);
            Sdl.Init.Quit();
        }
    }
}
