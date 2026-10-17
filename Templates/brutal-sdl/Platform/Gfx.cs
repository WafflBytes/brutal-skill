using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.StbApi;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>
/// Draws the game with the SDL renderer. It only needs an <see cref="SdlRenderer"/>, so the same code draws to
/// the window and to the off-screen software renderer used by --snapshot. Owns the logo texture.
/// </summary>
public sealed unsafe class Gfx : IDisposable
{
    const string Credits = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

    readonly SdlRenderer _r;
    readonly SdlTexture _logo;
    readonly int _logoW;
    readonly int _logoH;
    readonly TextRenderer _text;
    readonly TextRenderer.Label _creditsLabel;
    readonly TextRenderer.Label _scoreLabel;
    readonly TextRenderer.Label _helpLabel;
    readonly TextRenderer.Label _pausedLabel;
    readonly TextRenderer.Label _pausedHintLabel;

    public Gfx(SdlRenderer renderer)
    {
        _r = renderer;
        // Logical 640x360, letterboxed: game coordinates stay fixed whatever the window size.
        Sdl.Render.SetRenderLogicalPresentation(
            _r,
            (int)World.ArenaSize.X,
            (int)World.ArenaSize.Y,
            SdlRendererLogicalPresentation.Letterbox
        );
        Sdl.Render.SetRenderDrawBlendMode(_r, 1); // 1 = SDL_BLENDMODE_BLEND
        (_logo, _logoW, _logoH) = LoadLogo(_r);
        _text = new TextRenderer(_r, World.ArenaSize.Y);
        _creditsLabel = _text.CreateLabel(13);
        _scoreLabel = _text.CreateLabel(18);
        _helpLabel = _text.CreateLabel(12);
        _pausedLabel = _text.CreateLabel(36);
        _pausedHintLabel = _text.CreateLabel(14);
    }

    static (SdlTexture, int, int) LoadLogo(SdlRenderer renderer)
    {
        using var stream =
            typeof(Gfx).Assembly.GetManifestResourceStream("logo.png")
            ?? throw new InvalidOperationException("logo.png resource missing");
        var png = new byte[stream.Length];
        stream.ReadExactly(png);
        var image = Stb.LoadFromMemory(png, 4);
        try
        {
            if (image.Data == null) // T1: Stb returns a null image instead of throwing
                throw new InvalidOperationException("Failed to decode the BRUTAL logo");
            var tex = Sdl.Render.CreateTexture(
                renderer,
                SdlPixelFormat.Abgr8888, // RGBA byte order
                SdlTextureAccess.Static,
                image.Width,
                image.Height
            );
            Sdl.Render.UpdateTexture(tex, null, image.Data, image.Width * 4);
            Sdl.Render.SetTextureBlendMode(tex, 1);
            Sdl.Render.SetTextureScaleMode(tex, SdlScaleMode.Linear);
            return (tex, image.Width, image.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    public void Frame(ScreenFlow flow, World world, bool paused = false)
    {
        _text.UpdateScale(); // keeps text sharp after a window resize
        Sdl.Render.SetRenderDrawColor(_r, 12, 12, 16, 255);
        Sdl.Render.RenderClear(_r);
        if (flow.Current == Screen.Splash)
            DrawSplash(flow.SplashAlpha);
        else
        {
            DrawPlay(world);
            if (paused)
                DrawPaused();
        }
    }

    void DrawSplash(float alpha)
    {
        var a = (byte)(alpha * 255);
        var w = _logoW * 0.75f;
        var h = _logoH * 0.75f;
        var dst = new SdlFRect { x = (World.ArenaSize.X - w) / 2, y = (World.ArenaSize.Y - h) / 2 - 20, w = w, h = h };
        Sdl.Render.SetTextureAlphaMod(_logo, a);
        Sdl.Render.RenderTexture(_r, _logo, null, &dst);
        // Licence 2.1 asks for the credits notice on a credits screen or equivalent; the splash carries it too.
        _creditsLabel.Draw(Credits, World.ArenaSize.X / 2, World.ArenaSize.Y - 34, 160, 160, 160, a, Align.Center);
    }

    void DrawPlay(World world)
    {
        // Arena border.
        Sdl.Render.SetRenderDrawColor(_r, 40, 44, 56, 255);
        Rect(new float2(0, World.HudHeight), new float2(World.ArenaSize.X, 2));

        Sdl.Render.SetRenderDrawColor(_r, 255, 200, 60, 255);
        foreach (var d in world.Dots)
            Rect(d - new float2(World.DotRadius, World.DotRadius), new float2(World.DotRadius * 2, World.DotRadius * 2));

        Sdl.Render.SetRenderDrawColor(_r, 80, 200, 255, 255);
        Rect(
            world.Player - new float2(World.PlayerHalfSize, World.PlayerHalfSize),
            new float2(World.PlayerHalfSize * 2, World.PlayerHalfSize * 2)
        );

        _scoreLabel.Draw($"Score {world.Score}", 8, 1, 235, 235, 235);
        _helpLabel.Draw("Arrows/WASD move   P pause   R restart   Esc quit", World.ArenaSize.X - 8, 5, 130, 130, 145, align: Align.Right);
    }

    void DrawPaused()
    {
        Sdl.Render.SetRenderDrawColor(_r, 0, 0, 0, 160);
        Rect(new float2(0, World.HudHeight), World.ArenaSize);
        _pausedLabel.Draw("Paused", World.ArenaSize.X / 2, World.ArenaSize.Y / 2 - 30, 240, 240, 240, align: Align.Center);
        _pausedHintLabel.Draw("Press P to continue", World.ArenaSize.X / 2, World.ArenaSize.Y / 2 + 14, 170, 170, 180, align: Align.Center);
    }

    void Rect(float2 pos, float2 size)
    {
        var rect = new SdlFRect { x = pos.X, y = pos.Y, w = size.X, h = size.Y };
        Sdl.Render.RenderFillRect(_r, &rect);
    }

    public float TextScale => _text.Scale;

    public void Dispose()
    {
        _text.Dispose();
        Sdl.Render.DestroyTexture(_logo);
    }
}
