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

    /// <summary>Pixels per world unit. The logical canvas is the whole field: 16 x 9 units = 640 x 360.</summary>
    public const float PixelsPerUnit = 40f;
    static readonly float2 Canvas = World.FieldSize * PixelsPerUnit;

    readonly SdlRenderer _r;
    readonly SdlTexture _logo;
    readonly int _logoW;
    readonly int _logoH;
    readonly TextRenderer _text;
    readonly TextRenderer.Label _creditsLabel;
    readonly TextRenderer.Label _helpLabel;
    readonly TextRenderer.Label _pausedLabel;
    readonly TextRenderer.Label _pausedHintLabel;

    public Gfx(SdlRenderer renderer)
    {
        _r = renderer;
        // Logical canvas (640x360), letterboxed: game coordinates stay fixed whatever the window size.
        Sdl.Render.SetRenderLogicalPresentation(
            _r,
            (int)Canvas.X,
            (int)Canvas.Y,
            SdlRendererLogicalPresentation.Letterbox
        );
        Sdl.Render.SetRenderDrawBlendMode(_r, 1); // 1 = SDL_BLENDMODE_BLEND
        (_logo, _logoW, _logoH) = LoadLogo(_r);
        _text = new TextRenderer(_r, Canvas.Y);
        _creditsLabel = _text.CreateLabel(13);
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
        var dst = new SdlFRect { x = (Canvas.X - w) / 2, y = (Canvas.Y - h) / 2 - 20, w = w, h = h };
        Sdl.Render.SetTextureAlphaMod(_logo, a);
        Sdl.Render.RenderTexture(_r, _logo, null, &dst);
        // Licence 2.1 asks for the credits notice on a credits screen or equivalent; the splash carries it too.
        _creditsLabel.Draw(Credits, Canvas.X / 2, Canvas.Y - 34, 160, 160, 160, a, Align.Center);
    }

    /// <summary>Placeholder: a grid and the marker. Replace with your game's drawing; keep the splash and pause.</summary>
    void DrawPlay(World world)
    {
        Sdl.Render.SetRenderDrawColor(_r, 26, 28, 36, 255);
        for (var x = 1; x < World.FieldSize.X; x++)
            Rect(new float2(x * PixelsPerUnit, 0), new float2(1, Canvas.Y));
        for (var y = 1; y < World.FieldSize.Y; y++)
            Rect(new float2(0, y * PixelsPerUnit), new float2(Canvas.X, 1));

        Sdl.Render.SetRenderDrawColor(_r, 80, 200, 255, 255);
        var half = new float2(World.MarkerHalfSize, World.MarkerHalfSize);
        Rect((world.Marker - half) * PixelsPerUnit, half * 2 * PixelsPerUnit);

        _helpLabel.Draw("Arrows/WASD move   P pause   Esc quit", 8, 5, 130, 130, 145);
    }

    void DrawPaused()
    {
        Sdl.Render.SetRenderDrawColor(_r, 0, 0, 0, 160);
        Rect(float2.Zero, Canvas);
        _pausedLabel.Draw("Paused", Canvas.X / 2, Canvas.Y / 2 - 30, 240, 240, 240, align: Align.Center);
        _pausedHintLabel.Draw("Press P to continue", Canvas.X / 2, Canvas.Y / 2 + 14, 170, 170, 180, align: Align.Center);
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
