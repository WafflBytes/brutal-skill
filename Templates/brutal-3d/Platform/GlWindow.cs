using System.Runtime.InteropServices;
using Brutal.OpenGlApi;
using Brutal.SdlApi;

namespace BrutalGame3D.Platform;

/// <summary>
/// An SDL3 window with an OpenGL 4.1 core context and GLAD loaded (skill recipe 7.17). Main thread only (T16).
/// Tries 4x MSAA first and falls back to no MSAA if the driver refuses.
/// </summary>
public sealed unsafe class GlWindow : IDisposable
{
    static GlGetProcAddressFunc? s_loader; // must stay rooted: Glad doesn't keep the delegate alive (T35)

    public SdlWindow Window { get; }
    readonly nint _context;
    public bool Multisampled { get; }

    public GlWindow(string title, int width, int height, bool hidden)
    {
        var flags = SdlWindowFlags.Opengl | SdlWindowFlags.Resizable | (hidden ? SdlWindowFlags.Hidden : 0);
        foreach (var msaa in new[] { true, false })
        {
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextMajorVersion, 4);
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextMinorVersion, 1);
            Sdl.Video.GLSetAttribute(SdlGLAttr.ContextProfileMask, Sdl.Constants.GlContextProfileCore);
            Sdl.Video.GLSetAttribute(SdlGLAttr.DepthSize, 24);
            Sdl.Video.GLSetAttribute(SdlGLAttr.Doublebuffer, 1);
            Sdl.Video.GLSetAttribute(SdlGLAttr.Multisamplebuffers, msaa ? 1 : 0);
            Sdl.Video.GLSetAttribute(SdlGLAttr.Multisamplesamples, msaa ? 4 : 0);
            SdlWindow window;
            try
            {
                window = Sdl.Video.CreateWindow(title, width, height, flags);
            }
            catch (SdlException) when (msaa)
            {
                continue;
            }
            var ctx = Sdl.Video.GLCreateContext(window); // 0 = failure
            if (ctx == 0)
            {
                Sdl.Video.DestroyWindow(window);
                if (msaa)
                    continue;
                throw new InvalidOperationException("Couldn't create an OpenGL 4.1 context. Updating the graphics driver may help.");
            }
            Window = window;
            _context = ctx;
            Multisampled = msaa;
            break;
        }
        Sdl.Video.GLMakeCurrent(Window, _context);
        s_loader ??= name => (nint)Sdl.PInvoke.GLGetProcAddress(Marshal.PtrToStringUTF8((nint)name)!);
        if (!Glad.Initialize(s_loader))
            throw new InvalidOperationException("GLAD couldn't load OpenGL");
        if (Multisampled)
            OpenGl.Native.glEnable(GlCapability.Multisample);
    }

    public (int W, int H) PixelSize
    {
        get
        {
            Sdl.Video.GetWindowSizeInPixels(Window, out var w, out var h);
            return (w, h);
        }
    }

    public bool SetVSync(bool on) => Sdl.Video.GLSetSwapInterval(on ? 1 : 0);

    public void Swap() => Sdl.Video.GLSwapWindow(Window);

    public void Dispose()
    {
        Sdl.Video.GLDestroyContext(_context);
        Sdl.Video.DestroyWindow(Window);
    }
}
