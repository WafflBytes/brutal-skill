using Brutal.SdlApi;
using Brutal.StbApi;

namespace BrutalGame.Platform;

public static unsafe class Screenshot
{
    /// <summary>
    /// Reads the current render target into a PNG (call before RenderPresent). Returns the number of pixels that
    /// aren't near-black, so callers can catch a blank frame.
    /// </summary>
    public static int Save(SdlRenderer renderer, string path)
    {
        var raw = Sdl.PInvoke.RenderReadPixels(renderer, null); // raw layer only (T11)
        if (raw == null)
            throw new InvalidOperationException("RenderReadPixels failed");
        var rgba = Sdl.PInvoke.ConvertSurface(raw, SdlPixelFormat.Abgr8888);
        try
        {
            if (rgba == null)
                throw new InvalidOperationException("ConvertSurface failed");
            var bytes = new ReadOnlySpan<byte>(rgba->pixels, rgba->pitch * rgba->h);
            Stb.WritePng(path, rgba->w, rgba->h, 4, bytes, rgba->pitch);

            var lit = 0;
            for (var y = 0; y < rgba->h; y++)
            {
                var row = bytes.Slice(y * rgba->pitch, rgba->w * 4);
                for (var x = 0; x < row.Length; x += 4)
                    if (row[x] > 40 || row[x + 1] > 40 || row[x + 2] > 40)
                        lit++;
            }
            return lit;
        }
        finally
        {
            Sdl.PInvoke.DestroySurface(raw);
            if (rgba != null)
                Sdl.PInvoke.DestroySurface(rgba);
        }
    }
}
