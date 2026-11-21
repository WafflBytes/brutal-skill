using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;
using Brutal.StbApi;

namespace BrutalGame.Platform;

/// <summary>RGBA pixels and their size, tightly packed rows.</summary>
public readonly record struct Pixels(byte[] Rgba, int Width, int Height);

/// <summary>
/// CPU-side images for the Vulkan renderer: text rasterised with SDL_ttf (no SDL window or video init needed) and the
/// licence splash (skill recipe 7.31). The renderer uploads each one once as a texture. Text is rasterised at a fixed
/// size and scaled with the window; re-rasterise for the window height if you need it razor-sharp.
/// </summary>
public static unsafe class VkText
{
    public const string Credits = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

    /// <summary>White text on transparent, to be tinted by the quad shader.</summary>
    public static Pixels Render(string text, float size)
    {
        SdlTtf.Init(); // reference-counted
        using var fontData = new NativeBlob(Resources.Read("font.ttf")); // SDL_ttf reads the font lazily: keep it alive
        var font = SdlTtf.OpenFontIO(Sdl.PInvoke.IOFromConstMem(fontData.Data, (nuint)fontData.Length), closeio: true, size);
        try
        {
            var surface = SdlTtf.RenderTextBlended(font, text, 0, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
            if (surface == null)
                throw new InvalidOperationException($"SDL_ttf couldn't render \"{text}\"");
            var rgba = Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Abgr8888); // RGBA byte order
            Sdl.PInvoke.DestroySurface(surface);
            try
            {
                int w = rgba->w, h = rgba->h;
                var packed = new byte[w * h * 4];
                for (var y = 0; y < h; y++) // SDL may pad rows: copy by pitch
                    new ReadOnlySpan<byte>((byte*)rgba->pixels + y * rgba->pitch, w * 4).CopyTo(packed.AsSpan(y * w * 4));
                return new Pixels(packed, w, h);
            }
            finally
            {
                Sdl.PInvoke.DestroySurface(rgba);
            }
        }
        finally
        {
            SdlTtf.CloseFont(font); // before the font bytes are freed
            SdlTtf.Quit();
        }
    }

    /// <summary>The splash canvas: the unaltered logo above the credits line, opaque black background.</summary>
    public static Pixels Splash()
    {
        const int W = 760, H = 280;
        var canvas = new byte[W * H * 4];
        for (var i = 3; i < canvas.Length; i += 4)
            canvas[i] = 255;
        var logo = Stb.LoadFromMemory(Resources.Read("logo.png"), 4);
        try
        {
            if (logo.Data == null) // T1: a bad image gives a null image, not an exception
                throw new InvalidOperationException("Failed to decode the BRUTAL logo");
            Blit(canvas, W, H, new ReadOnlySpan<byte>(logo.Data, logo.Width * logo.Height * 4), logo.Width, logo.Height, 24);
        }
        finally
        {
            logo.Dispose();
        }
        var credits = Render(Credits, 18);
        Blit(canvas, W, H, credits.Rgba, credits.Width, credits.Height, 232);
        return new Pixels(canvas, W, H);
    }

    /// <summary>Alpha-blends tightly packed RGBA over the canvas, centred horizontally at row <paramref name="top"/>.</summary>
    static void Blit(byte[] canvas, int cw, int ch, ReadOnlySpan<byte> src, int w, int h, int top)
    {
        var left = Math.Max(0, (cw - w) / 2);
        for (var y = 0; y < h && top + y < ch; y++)
        for (var x = 0; x < w && left + x < cw; x++)
        {
            var s = (y * w + x) * 4;
            var d = ((top + y) * cw + left + x) * 4;
            var a = src[s + 3];
            for (var c = 0; c < 3; c++)
                canvas[d + c] = (byte)((src[s + c] * a + canvas[d + c] * (255 - a)) / 255);
        }
    }
}
