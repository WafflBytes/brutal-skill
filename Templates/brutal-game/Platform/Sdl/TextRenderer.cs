using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;

namespace BrutalGame.Platform;

public enum Align
{
    Left,
    Center,
    Right,
}

/// <summary>
/// TrueType text through SDL_ttf's renderer text engine (glyphs cached in a texture atlas).
/// Text stays sharp at any window size: fonts are rasterised at the real output pixel size and drawn with a
/// render scale that cancels the logical-presentation scale. Create one <see cref="Label"/> per piece of text
/// at startup and change its string as needed; don't create text objects every frame.
/// </summary>
public sealed unsafe class TextRenderer : IDisposable
{
    readonly SdlRenderer _r;
    readonly NativeBlob _fontData; // SDL_ttf reads fonts lazily: the bytes must outlive every CloseFont
    readonly Dictionary<float, TtfFont> _fonts = []; // one font object per size: SetFontSize clears its glyph cache
    readonly TtfTextEngine* _engine;
    readonly List<Label> _labels = [];
    readonly float _logicalHeight;
    float _scale;

    /// <summary>Output pixels per logical pixel that text is currently rasterised for.</summary>
    public float Scale => _scale;

    public TextRenderer(SdlRenderer renderer, float logicalHeight)
    {
        _r = renderer;
        _logicalHeight = logicalHeight;
        SdlTtf.Init(); // reference-counted by SDL_ttf; balanced by Quit in Dispose
        _fontData = new NativeBlob(Resources.Read("font.ttf"));
        _engine = SdlTtf.CreateRendererTextEngine(_r);
        _scale = CurrentScale();
    }

    /// <summary>A text object drawn at <paramref name="size"/> logical pixels (font size in points at 1:1).</summary>
    public Label CreateLabel(float size)
    {
        if (!_fonts.TryGetValue(size, out var font))
        {
            var io = Sdl.PInvoke.IOFromConstMem(_fontData.Data, (nuint)_fontData.Length);
            font = SdlTtf.OpenFontIO(io, closeio: true, size * _scale);
            _fonts[size] = font;
        }
        var label = new Label(this, SdlTtf.CreateText(_engine, font, "", 0));
        _labels.Add(label);
        return label;
    }

    float CurrentScale()
    {
        Sdl.Render.GetRenderOutputSize(_r, out _, out var outH);
        return MathF.Max(0.25f, outH / _logicalHeight);
    }

    /// <summary>Call once per frame before drawing text. After a resize it re-rasterises every font at the new
    /// pixel size; SetFontSize updates the existing text objects that use it.</summary>
    public void UpdateScale()
    {
        var scale = CurrentScale();
        if (MathF.Abs(scale - _scale) < 0.001f)
            return;
        _scale = scale;
        foreach (var (size, font) in _fonts)
            SdlTtf.SetFontSize(font, size * _scale);
    }

    public sealed class Label
    {
        readonly TextRenderer _owner;
        readonly TtfText* _text;
        string _string = "";

        internal Label(TextRenderer owner, TtfText* text)
        {
            _owner = owner;
            _text = text;
        }

        /// <summary>Width in logical pixels of the current string.</summary>
        public float Width
        {
            get
            {
                SdlTtf.GetTextSize(_text, out var w, out _);
                return w / _owner._scale;
            }
        }

        public void Draw(string s, float x, float y, byte r, byte g, byte b, byte a = 255, Align align = Align.Left)
        {
            if (s != _string)
            {
                SdlTtf.SetTextString(_text, s, 0); // re-shapes only when the string changes
                _string = s;
            }
            SdlTtf.SetTextColor(_text, r, g, b, a);
            var left = align switch
            {
                Align.Center => x - Width / 2,
                Align.Right => x - Width,
                _ => x,
            };
            var k = _owner._scale;
            Sdl.Render.SetRenderScale(_owner._r, 1 / k, 1 / k); // cancels the logical scale: glyphs land 1:1
            SdlTtf.DrawRendererText(_text, MathF.Round(left * k), MathF.Round(y * k));
            Sdl.Render.SetRenderScale(_owner._r, 1, 1);
        }

        internal void Destroy() => SdlTtf.DestroyText(_text);
    }

    public void Dispose()
    {
        foreach (var label in _labels)
            label.Destroy(); // order: texts -> engine -> fonts -> font bytes
        SdlTtf.DestroyRendererTextEngine(_engine);
        foreach (var font in _fonts.Values)
            SdlTtf.CloseFont(font);
        _fontData.Dispose();
        SdlTtf.Quit();
    }
}
