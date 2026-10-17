using Brutal.OpenGlApi;
using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;

namespace BrutalGame3D.Platform;

public enum Align
{
    Left,
    Center,
    Right,
}

/// <summary>
/// TrueType text for an OpenGL window. SDL_ttf rasterises a string to a surface (TTF_RenderText_Blended), which is
/// uploaded as a GL texture and drawn by <see cref="Hud"/>. A label re-rasterises only when its string or the UI
/// scale changes. Sizes are in 720p pixels; fonts are opened at size x scale so text stays sharp at any window size.
/// </summary>
public sealed unsafe class GlText : IDisposable
{
    readonly NativeBlob _fontData; // SDL_ttf reads fonts lazily: the bytes must outlive every CloseFont
    readonly Dictionary<float, TtfFont> _fonts = [];
    readonly List<Label> _labels = [];
    float _scale = 1;

    public float Scale => _scale;

    public GlText()
    {
        SdlTtf.Init(); // reference-counted; balanced by Quit in Dispose
        _fontData = new NativeBlob(Resources.Read("font.ttf"));
    }

    public Label CreateLabel(float size)
    {
        var label = new Label(this, size);
        _labels.Add(label);
        return label;
    }

    TtfFont Font(float size)
    {
        if (!_fonts.TryGetValue(size, out var font))
        {
            var io = Sdl.PInvoke.IOFromConstMem(_fontData.Data, (nuint)_fontData.Length);
            font = SdlTtf.OpenFontIO(io, closeio: true, size * _scale);
            _fonts[size] = font;
        }
        return font;
    }

    /// <summary>Call once per frame with output height / 720. Re-rasterises everything after a resize.</summary>
    public void SetScale(float scale)
    {
        scale = MathF.Max(0.25f, scale);
        if (MathF.Abs(scale - _scale) < 0.01f)
            return;
        _scale = scale;
        foreach (var (size, font) in _fonts)
            SdlTtf.SetFontSize(font, size * _scale);
        foreach (var label in _labels)
            label.Invalidate();
    }

    public sealed class Label
    {
        readonly GlText _owner;
        readonly float _size;
        string? _string;
        GlTexture _tex;
        bool _hasTex;

        /// <summary>Size of the current texture in output pixels.</summary>
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }

        internal Label(GlText owner, float size)
        {
            _owner = owner;
            _size = size;
        }

        internal void Invalidate() => _string = null;

        /// <summary>Makes sure the texture holds <paramref name="s"/>; returns false for an empty string.</summary>
        public bool Prepare(string s, out GlTexture texture)
        {
            if (s != _string)
            {
                _string = s;
                Free();
                if (s.Length > 0)
                    Rasterise(s);
            }
            texture = _tex;
            return _hasTex;
        }

        void Rasterise(string s)
        {
            var white = new SdlColor { r = 255, g = 255, b = 255, a = 255 }; // tinted in the shader
            var surface = SdlTtf.RenderTextBlended(_owner.Font(_size), s, 0, white);
            if (surface == null)
                return;
            var rgba = Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Abgr8888); // RGBA byte order
            Sdl.PInvoke.DestroySurface(surface);
            if (rgba == null)
                return;
            try
            {
                // GL wants tightly packed rows here; repack if SDL padded them.
                var w = rgba->w;
                var h = rgba->h;
                var src = (byte*)rgba->pixels;
                if (rgba->pitch == w * 4)
                    _tex = GlUtil.CreateTexture(w, h, src);
                else
                {
                    var packed = new byte[w * h * 4];
                    for (var y = 0; y < h; y++)
                        new ReadOnlySpan<byte>(src + y * rgba->pitch, w * 4).CopyTo(packed.AsSpan(y * w * 4));
                    fixed (byte* p = packed)
                        _tex = GlUtil.CreateTexture(w, h, p);
                }
                _hasTex = true;
                PixelWidth = w;
                PixelHeight = h;
            }
            finally
            {
                Sdl.PInvoke.DestroySurface(rgba);
            }
        }

        internal void Free()
        {
            if (_hasTex)
                GlUtil.DeleteTexture(_tex);
            _hasTex = false;
            PixelWidth = PixelHeight = 0;
        }
    }

    public void Dispose()
    {
        foreach (var label in _labels)
            label.Free();
        foreach (var font in _fonts.Values)
            SdlTtf.CloseFont(font);
        _fontData.Dispose();
        SdlTtf.Quit();
    }
}
