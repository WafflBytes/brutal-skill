using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.StbApi;
using BrutalGame.Sim;
using GL = Brutal.OpenGlApi.OpenGl.Native;

namespace BrutalGame.Platform;

/// <summary>What the renderer needs from the world for one frame, interpolated between fixed steps.</summary>
public readonly record struct View(float2 Marker)
{
    public static View Of(World w) => new(w.Marker);

    public static View Lerp(in View a, in View b, float t) => new(float2.Lerp(a.Marker, b.Marker, t));
}

/// <summary>
/// Draws the game with OpenGL 4.1 core: the licence splash, a lit 3D scene and a 2D HUD of TrueType labels. It only
/// needs a current GL context, so the same code draws to the window and to the off-screen framebuffer used by
/// --snapshot. Owns all its GL objects. The scene is a placeholder (a floor and the marker as a cube): replace
/// <see cref="DrawScene"/> and <see cref="DrawHud"/> with your game, and keep the splash and the pause overlay.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    public const string Credits = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";
    static readonly float3 Sky = new(0.62f, 0.74f, 0.88f);
    static readonly float3 LightDir = float3.Normalize(new float3(-0.45f, -1f, -0.3f));

    readonly GlProgram _scene;
    readonly int _uModel, _uViewProj, _uColor, _uLightDir;
    readonly GlProgram _hud;
    readonly int _uRect, _uScreen, _uHudColor, _uTex;
    readonly Mesh _cube;
    GlVertexArray _quadVao;
    GlBuffer _quadVbo;
    readonly GlTexture _white;
    readonly GlTexture _logo;
    readonly int _logoW, _logoH;

    readonly GlText _text;
    readonly GlText.Label _creditsLabel, _helpLabel, _bigLabel, _hintLabel;

    int _w, _h; // output size in pixels
    float _ui; // pixels per 720p HUD unit

    public float TextScale => _text.Scale;

    public Renderer()
    {
        _scene = GlUtil.BuildProgram(SceneVs, SceneFs);
        _uModel = GlUtil.Uniform(_scene, "uModel"u8);
        _uViewProj = GlUtil.Uniform(_scene, "uViewProj"u8);
        _uColor = GlUtil.Uniform(_scene, "uColor"u8);
        _uLightDir = GlUtil.Uniform(_scene, "uLightDir"u8);

        _hud = GlUtil.BuildProgram(HudVs, HudFs);
        _uRect = GlUtil.Uniform(_hud, "uRect"u8);
        _uScreen = GlUtil.Uniform(_hud, "uScreen"u8);
        _uHudColor = GlUtil.Uniform(_hud, "uColor"u8);
        _uTex = GlUtil.Uniform(_hud, "uTex"u8);

        _cube = Mesh.Cube();
        CreateQuad();

        var white = stackalloc uint[1] { 0xFFFFFFFF };
        _white = GlUtil.CreateTexture(1, 1, white);
        (_logo, _logoW, _logoH) = LoadLogo();

        _text = new GlText();
        _creditsLabel = _text.CreateLabel(15);
        _helpLabel = _text.CreateLabel(17);
        _bigLabel = _text.CreateLabel(64);
        _hintLabel = _text.CreateLabel(20);
    }

    void CreateQuad()
    {
        var quad = stackalloc float2[6] { new(0, 0), new(1, 0), new(1, 1), new(0, 0), new(1, 1), new(0, 1) };
        GlVertexArray vao;
        GL.glGenVertexArrays(1, &vao);
        _quadVao = vao;
        GL.glBindVertexArray(vao);
        GlBuffer vbo;
        GL.glGenBuffers(1, &vbo);
        _quadVbo = vbo;
        GL.glBindBuffer(OpenGl.GL_ARRAY_BUFFER, vbo);
        GL.glBufferData(OpenGl.GL_ARRAY_BUFFER, 6 * sizeof(float2), quad, OpenGl.GL_STATIC_DRAW);
        GL.glVertexAttribPointer(0, 2, OpenGl.GL_FLOAT, false, sizeof(float2), null);
        GL.glEnableVertexAttribArray(0);
        GL.glBindVertexArray(default);
    }

    static (GlTexture, int, int) LoadLogo()
    {
        var png = Resources.Read("logo.png");
        var image = Stb.LoadFromMemory(png, 4);
        try
        {
            if (image.Data == null) // T1: Stb returns a null image instead of throwing
                throw new InvalidOperationException("Failed to decode the BRUTAL logo");
            return (GlUtil.CreateTexture(image.Width, image.Height, image.Data), image.Width, image.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>Draws one frame into the currently bound framebuffer of size <paramref name="w"/> x <paramref name="h"/>.</summary>
    public void Frame(ScreenFlow flow, in View view, bool paused, int w, int h)
    {
        _w = Math.Max(1, w);
        _h = Math.Max(1, h);
        _ui = _h / 720f;
        _text.SetScale(_ui);
        GL.glViewport(0, 0, _w, _h);

        if (flow.Current == Screen.Splash)
        {
            GL.glClearColor(0.05f, 0.05f, 0.06f, 1);
            GL.glClear(OpenGl.GL_COLOR_BUFFER_BIT | OpenGl.GL_DEPTH_BUFFER_BIT);
            DrawSplash(flow.SplashAlpha);
            return;
        }

        GL.glClearColor(Sky.X, Sky.Y, Sky.Z, 1);
        GL.glClear(OpenGl.GL_COLOR_BUFFER_BIT | OpenGl.GL_DEPTH_BUFFER_BIT);
        DrawScene(view);
        DrawHud(paused);
    }

    // ---- 3D --------------------------------------------------------------------------------------------------

    /// <summary>World X/Y map to 3D X/Z, centred on the origin; the camera looks down at the field from the front.</summary>
    static float3 ToWorld(float2 p, float height) => new(p.X - World.FieldSize.X / 2, height, p.Y - World.FieldSize.Y / 2);

    void DrawScene(in View view)
    {
        GL.glEnable(GlCapability.DepthTest);
        GL.glEnable(GlCapability.CullFace);
        GL.glCullFace(GlFace.Back);
        GL.glDisable(GlCapability.Blend);

        var proj = Mat4.Perspective(50f * MathF.PI / 180f, (float)_w / _h, 0.1f, 100f);
        var look = Mat4.LookAt(new float3(0, 11, 12), new float3(0, 0, 0.8f), new float3(0, 1, 0));
        GL.glUseProgram(_scene);
        GlUtil.SetMatrix(_uViewProj, proj * look);
        GL.glUniform3f(_uLightDir, LightDir.X, LightDir.Y, LightDir.Z);

        // Floor: a flat box the size of the field.
        var half = World.FieldSize / 2;
        Draw(Mat4.Trs(new float3(0, -0.1f, 0), floatQuat.Identity, new float3(half.X, 0.1f, half.Y)), new float3(0.42f, 0.50f, 0.62f));
        // The marker as a cube standing on the floor.
        var s = World.MarkerHalfSize;
        Draw(Mat4.Trs(ToWorld(view.Marker, s), floatQuat.Identity, new float3(s, s, s)), new float3(0.31f, 0.78f, 1f));
        GL.glBindVertexArray(default);
    }

    void Draw(in Mat4 model, float3 color)
    {
        GlUtil.SetMatrix(_uModel, model);
        GL.glUniform4f(_uColor, color.X, color.Y, color.Z, 1);
        _cube.Draw();
    }

    // ---- 2D --------------------------------------------------------------------------------------------------

    void BeginHud()
    {
        GL.glDisable(GlCapability.DepthTest);
        GL.glDisable(GlCapability.CullFace);
        GL.glEnable(GlCapability.Blend);
        GL.glBlendFunc(OpenGl.GL_SRC_ALPHA, OpenGl.GL_ONE_MINUS_SRC_ALPHA);
        GL.glUseProgram(_hud);
        GL.glUniform2f(_uScreen, _w, _h);
        GL.glUniform1i(_uTex, 0);
        GL.glActiveTexture(OpenGl.GL_TEXTURE0);
        GL.glBindVertexArray(_quadVao);
    }

    /// <summary>Rectangle in output pixels, y down.</summary>
    void Quad(GlTexture tex, float x, float y, float w, float h, float4 color)
    {
        GL.glBindTexture(OpenGl.GL_TEXTURE_2D, tex.Id);
        GL.glUniform4f(_uRect, x, y, w, h);
        GL.glUniform4f(_uHudColor, color.X, color.Y, color.Z, color.W);
        GL.glDrawArrays(OpenGl.GL_TRIANGLES, 0, 6);
    }

    /// <summary>Panel in HUD units (720p pixels).</summary>
    void Panel(float x, float y, float w, float h, float4 color) => Quad(_white, x * _ui, y * _ui, w * _ui, h * _ui, color);

    /// <summary>Text at HUD units; returns its width in HUD units.</summary>
    float Text(GlText.Label label, string s, float x, float y, float4 color, Align align = Align.Left)
    {
        if (!label.Prepare(s, out var tex))
            return 0;
        var px = x * _ui;
        var w = label.PixelWidth;
        px = align switch
        {
            Align.Center => px - w / 2f,
            Align.Right => px - w,
            _ => px,
        };
        Quad(tex, MathF.Round(px), MathF.Round(y * _ui), w, label.PixelHeight, color);
        return w / _ui;
    }

    float HudWidth => _w / _ui;

    void DrawSplash(float alpha)
    {
        BeginHud();
        var scale = 0.75f * _ui;
        var lw = _logoW * scale;
        var lh = _logoH * scale;
        Quad(_logo, (_w - lw) / 2, (_h - lh) / 2 - 30 * _ui, lw, lh, new float4(1, 1, 1, alpha));
        // Licence 2.1 asks for the credits notice on a credits screen or equivalent; the splash carries it too.
        Text(_creditsLabel, Credits, HudWidth / 2, 720 - 70, new float4(0.65f, 0.65f, 0.65f, alpha), Align.Center);
        GL.glBindVertexArray(default);
    }

    void DrawHud(bool paused)
    {
        BeginHud();
        var right = HudWidth;
        if (!paused)
            Text(_helpLabel, "WASD / arrows move    P pause    Esc quit", 20, 18, new float4(0.1f, 0.12f, 0.18f, 0.8f));
        else
        {
            Panel(0, 0, right, 720, new float4(0, 0, 0, 0.5f));
            Text(_bigLabel, "Paused", right / 2, 260, new float4(0.95f, 0.95f, 0.95f, 1), Align.Center);
            Text(_hintLabel, "Press P to continue", right / 2, 360, new float4(0.75f, 0.75f, 0.8f, 1), Align.Center);
        }
        GL.glBindVertexArray(default);
    }

    public void Dispose()
    {
        _text.Dispose();
        GlUtil.DeleteTexture(_logo);
        GlUtil.DeleteTexture(_white);
        var vbo = _quadVbo;
        GL.glDeleteBuffers(1, &vbo);
        var vao = _quadVao;
        GL.glDeleteVertexArrays(1, &vao);
        _cube.Dispose();
        GL.glDeleteProgram(_hud);
        GL.glDeleteProgram(_scene);
    }

    // ---- shaders ---------------------------------------------------------------------------------------------

    static ReadOnlySpan<byte> SceneVs =>
        """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uModel;
        uniform mat4 uViewProj;
        out vec3 vNormal;
        void main() {
            vNormal = transpose(inverse(mat3(uModel))) * aNormal; // correct under non-uniform scale
            gl_Position = uViewProj * uModel * vec4(aPos, 1.0);
        }
        """u8;

    // Directional sun plus a sky/ground ambient: enough to read 3D shapes. Add specular, fog or shadows as needed.
    static ReadOnlySpan<byte> SceneFs =>
        """
        #version 410 core
        in vec3 vNormal;
        uniform vec4 uColor;
        uniform vec3 uLightDir;
        out vec4 fragColor;
        void main() {
            vec3 n = normalize(vNormal);
            float diff = max(dot(n, normalize(-uLightDir)), 0.0);
            vec3 ambient = mix(vec3(0.32, 0.28, 0.25), vec3(0.62, 0.72, 0.88), n.y * 0.5 + 0.5);
            fragColor = vec4(uColor.rgb * (ambient * 0.55 + diff * vec3(1.0, 0.95, 0.85) * 0.8), uColor.a);
        }
        """u8;

    static ReadOnlySpan<byte> HudVs =>
        """
        #version 410 core
        layout(location = 0) in vec2 aPos;
        uniform vec4 uRect;
        uniform vec2 uScreen;
        out vec2 vUV;
        void main() {
            vec2 p = uRect.xy + aPos * uRect.zw;
            vUV = aPos;
            gl_Position = vec4(p.x / uScreen.x * 2.0 - 1.0, 1.0 - p.y / uScreen.y * 2.0, 0.0, 1.0);
        }
        """u8;

    static ReadOnlySpan<byte> HudFs =>
        """
        #version 410 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() { fragColor = texture(uTex, vUV) * uColor; }
        """u8;
}
