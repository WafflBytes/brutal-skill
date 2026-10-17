using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.StbApi;
using BrutalGame3D.Sim;
using GL = Brutal.OpenGlApi.OpenGl.Native;

namespace BrutalGame3D.Platform;

/// <summary>What the renderer needs from the world for one frame, interpolated between fixed steps.</summary>
public readonly record struct View(float3 BallPosition, floatQuat BallRotation, float3 CameraPosition, float3 CameraTarget, float Time)
{
    public static View Of(World w) => new(w.BallPosition, w.BallRotation, w.CameraPosition, w.CameraTarget, w.Time);

    public static View Lerp(in View a, in View b, float t)
    {
        // Normalised lerp is plenty for one step's worth of rotation; flip to the short way round first.
        var qb = b.BallRotation;
        var dot = a.BallRotation.X * qb.X + a.BallRotation.Y * qb.Y + a.BallRotation.Z * qb.Z + a.BallRotation.W * qb.W;
        if (dot < 0)
            qb = new floatQuat(-qb.X, -qb.Y, -qb.Z, -qb.W);
        var q = new float4(a.BallRotation.X, a.BallRotation.Y, a.BallRotation.Z, a.BallRotation.W) * (1 - t)
            + new float4(qb.X, qb.Y, qb.Z, qb.W) * t;
        q = float4.NormalizeOrZero(q);
        return new(
            float3.Lerp(a.BallPosition, b.BallPosition, t),
            new floatQuat(q.X, q.Y, q.Z, q.W),
            float3.Lerp(a.CameraPosition, b.CameraPosition, t),
            float3.Lerp(a.CameraTarget, b.CameraTarget, t),
            a.Time + (b.Time - a.Time) * t
        );
    }
}

/// <summary>
/// Draws the game with OpenGL 4.1 core: a lit 3D scene (directional sun + sky/ground ambient + specular, fog, a
/// blob shadow under the ball) and a 2D HUD of TrueType labels. It only needs a current GL context, so the same
/// code draws to the window and to the off-screen framebuffer used by --snapshot. Owns all its GL objects.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    public const string Credits = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";
    static readonly float3 Fog = new(0.62f, 0.74f, 0.88f);
    static readonly float3 LightDir = float3.Normalize(new float3(-0.45f, -1f, -0.3f));

    readonly GlProgram _scene;
    readonly int _uModel, _uViewProj, _uColor, _uMode, _uLightDir, _uCamPos, _uFog;
    readonly GlProgram _hud;
    readonly int _uRect, _uScreen, _uHudColor, _uTex;
    readonly Mesh _cube, _sphere, _cylinder, _disc;
    GlVertexArray _quadVao;
    GlBuffer _quadVbo;
    readonly GlTexture _white;
    readonly GlTexture _logo;
    readonly int _logoW, _logoH;

    readonly GlText _text;
    readonly GlText.Label _creditsLabel, _coinsLabel, _timeLabel, _helpLabel, _bigLabel, _subLabel, _hintLabel;

    int _w, _h; // output size in pixels
    float _ui; // pixels per 720p HUD unit

    public float TextScale => _text.Scale;

    public Renderer()
    {
        _scene = GlUtil.BuildProgram(SceneVs, SceneFs);
        _uModel = GlUtil.Uniform(_scene, "uModel"u8);
        _uViewProj = GlUtil.Uniform(_scene, "uViewProj"u8);
        _uColor = GlUtil.Uniform(_scene, "uColor"u8);
        _uMode = GlUtil.Uniform(_scene, "uMode"u8);
        _uLightDir = GlUtil.Uniform(_scene, "uLightDir"u8);
        _uCamPos = GlUtil.Uniform(_scene, "uCamPos"u8);
        _uFog = GlUtil.Uniform(_scene, "uFog"u8);

        _hud = GlUtil.BuildProgram(HudVs, HudFs);
        _uRect = GlUtil.Uniform(_hud, "uRect"u8);
        _uScreen = GlUtil.Uniform(_hud, "uScreen"u8);
        _uHudColor = GlUtil.Uniform(_hud, "uColor"u8);
        _uTex = GlUtil.Uniform(_hud, "uTex"u8);

        _cube = Mesh.Cube();
        _sphere = Mesh.Sphere();
        _cylinder = Mesh.Cylinder();
        _disc = Mesh.Disc();
        CreateQuad();

        var white = stackalloc uint[1] { 0xFFFFFFFF };
        _white = GlUtil.CreateTexture(1, 1, white);
        (_logo, _logoW, _logoH) = LoadLogo();

        _text = new GlText();
        _creditsLabel = _text.CreateLabel(15);
        _coinsLabel = _text.CreateLabel(30);
        _timeLabel = _text.CreateLabel(30);
        _helpLabel = _text.CreateLabel(15);
        _bigLabel = _text.CreateLabel(64);
        _subLabel = _text.CreateLabel(26);
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
    public void Frame(ScreenFlow flow, World world, in View view, bool paused, int w, int h)
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

        GL.glClearColor(Fog.X, Fog.Y, Fog.Z, 1);
        GL.glClear(OpenGl.GL_COLOR_BUFFER_BIT | OpenGl.GL_DEPTH_BUFFER_BIT);
        DrawScene(world, view);
        DrawHud(world, paused);
    }

    // ---- 3D --------------------------------------------------------------------------------------------------

    void DrawScene(World world, in View view)
    {
        GL.glEnable(GlCapability.DepthTest);
        GL.glEnable(GlCapability.CullFace);
        GL.glCullFace(GlFace.Back);
        GL.glDisable(GlCapability.Blend);
        GL.glDepthMask(true);

        var proj = Mat4.Perspective(60f * MathF.PI / 180f, (float)_w / _h, 0.1f, 160f);
        var look = Mat4.LookAt(view.CameraPosition, view.CameraTarget + new float3(0, 0.6f, 0), new float3(0, 1, 0));
        var viewProj = proj * look;

        GL.glUseProgram(_scene);
        GlUtil.SetMatrix(_uViewProj, viewProj);
        GL.glUniform3f(_uLightDir, LightDir.X, LightDir.Y, LightDir.Z);
        GL.glUniform3f(_uCamPos, view.CameraPosition.X, view.CameraPosition.Y, view.CameraPosition.Z);
        GL.glUniform3f(_uFog, Fog.X, Fog.Y, Fog.Z);

        foreach (var b in Level.Blocks)
        {
            var (color, mode) = b.Kind switch
            {
                BlockKind.Floor => (new float3(0.42f, 0.50f, 0.62f), 1),
                BlockKind.Wall => (new float3(0.86f, 0.55f, 0.36f), 0),
                BlockKind.Platform => (new float3(0.30f, 0.66f, 0.62f), 0),
                _ => (new float3(0.95f, 0.70f, 0.30f), 6),
            };
            Draw(_cube, Mat4.Trs(b.Center, b.Rotation, b.HalfExtents), color, mode);
        }

        // Coins: spinning, bobbing gold discs standing on edge.
        var stand = floatQuat.CreateFromAxisAngle(new float3(1, 0, 0), MathF.PI / 2); // cylinder axis Y -> Z
        for (var i = 0; i < Level.Coins.Length; i++)
        {
            if (world.CoinTaken[i])
                continue;
            var spin = Level.Mul(Level.Yaw(view.Time * 2.6f + i), stand);
            var bob = new float3(0, MathF.Sin(view.Time * 2.2f + i * 1.3f) * 0.12f, 0);
            Draw(_cylinder, Mat4.Trs(Level.Coins[i] + bob, spin, new float3(0.42f, 0.07f, 0.42f)), new float3(1f, 0.76f, 0.18f), 5);
        }

        // Ball.
        Draw(_sphere, Mat4.Trs(view.BallPosition, view.BallRotation, new float3(PhysicsScene.BallRadius)), new float3(0.88f, 0.18f, 0.22f), 2);

        // Blob shadow on the surface under the ball, tilted to the surface (ramps).
        if (Level.CastDown(view.BallPosition, out var hit, out var normal))
        {
            var height = view.BallPosition.Y - hit.Y - PhysicsScene.BallRadius;
            var alpha = 0.5f * Math.Clamp(1 - height / 6f, 0, 1);
            if (alpha > 0.01f)
            {
                var r = 0.55f + MathF.Max(0, height) * 0.08f;
                GL.glEnable(GlCapability.Blend);
                GL.glBlendFunc(OpenGl.GL_SRC_ALPHA, OpenGl.GL_ONE_MINUS_SRC_ALPHA);
                GL.glDepthMask(false);
                GL.glEnable(GlCapability.PolygonOffsetFill);
                GL.glPolygonOffset(-2, -2);
                Draw(_disc, Mat4.Trs(hit + normal * 0.01f, FromUp(normal), new float3(r, 1, r)), float3.Zero, 4, alpha);
                GL.glDisable(GlCapability.PolygonOffsetFill);
                GL.glDepthMask(true);
                GL.glDisable(GlCapability.Blend);
            }
        }
        GL.glBindVertexArray(default);
    }

    void Draw(Mesh mesh, in Mat4 model, float3 color, int mode, float alpha = 1)
    {
        GlUtil.SetMatrix(_uModel, model);
        GL.glUniform4f(_uColor, color.X, color.Y, color.Z, alpha);
        GL.glUniform1i(_uMode, mode);
        mesh.Draw();
    }

    /// <summary>Shortest rotation taking +Y to <paramref name="n"/>.</summary>
    static floatQuat FromUp(float3 n)
    {
        var up = new float3(0, 1, 0);
        var d = float3.Dot(up, n);
        if (d > 0.9999f)
            return floatQuat.Identity;
        var axis = float3.NormalizeOrZero(float3.Cross(up, n));
        return floatQuat.CreateFromAxisAngle(axis, MathF.Acos(Math.Clamp(d, -1, 1)));
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
        var cx = HudWidth / 2;
        var scale = 0.75f * _ui;
        var lw = _logoW * scale;
        var lh = _logoH * scale;
        Quad(_logo, (_w - lw) / 2, (_h - lh) / 2 - 30 * _ui, lw, lh, new float4(1, 1, 1, alpha));
        // Licence 2.1 asks for the credits notice on a credits screen or equivalent; the splash carries it too.
        Text(_creditsLabel, Credits, cx, 720 - 70, new float4(0.65f, 0.65f, 0.65f, alpha), Align.Center);
        GL.glBindVertexArray(default);
    }

    void DrawHud(World world, bool paused)
    {
        BeginHud();
        var right = HudWidth;
        var shade = new float4(0.05f, 0.07f, 0.12f, 0.55f);

        Panel(16, 14, 230, 50, shade);
        Text(_coinsLabel, $"Coins  {world.Score} / {world.CoinCount}", 30, 20, new float4(1f, 0.85f, 0.35f, 1));

        var t = world.TimeLeft;
        var hurry = t <= 10 && world.State == GameState.Playing;
        var pulse = hurry ? 0.75f + 0.25f * MathF.Cos(world.Time * 8) : 1;
        Panel(right - 196, 14, 180, 50, shade);
        Text(_timeLabel, $"Time  {MathF.Ceiling(t):0}", right - 30, 20, hurry ? new float4(1, 0.35f * pulse, 0.3f * pulse, 1) : new float4(0.95f, 0.97f, 1, 1), Align.Right);

        if (world.State == GameState.Playing && !paused)
            Text(_helpLabel, "WASD / arrows roll    Q / E turn camera    P pause    R restart    Esc quit", 20, 720 - 34, new float4(1, 1, 1, 0.75f));

        if (world.State != GameState.Playing)
        {
            Panel(0, 0, right, 720, new float4(0.02f, 0.03f, 0.06f, 0.55f));
            var won = world.State == GameState.Won;
            Text(_bigLabel, won ? "All coins!" : "Time's up!", right / 2, 230, won ? new float4(1, 0.85f, 0.3f, 1) : new float4(1, 0.45f, 0.4f, 1), Align.Center);
            var sub = won
                ? $"You found all {world.CoinCount} coins with {world.TimeLeft:0.0} s to spare"
                : $"You collected {world.Score} of {world.CoinCount} coins";
            Text(_subLabel, sub, right / 2, 330, new float4(0.95f, 0.95f, 1, 1), Align.Center);
            if (world.Time - world.FinishTime > 0.75f)
                Text(_hintLabel, "Press Enter to play again", right / 2, 400, new float4(0.8f, 0.85f, 0.95f, 1), Align.Center);
        }
        else if (paused)
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
        _disc.Dispose();
        _cylinder.Dispose();
        _sphere.Dispose();
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
        out vec3 vWorld;
        out vec3 vNormal;
        out vec3 vLocal;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            vWorld = w.xyz;
            vNormal = transpose(inverse(mat3(uModel))) * aNormal; // correct under non-uniform scale
            vLocal = aPos;
            gl_Position = uViewProj * w;
        }
        """u8;

    // Modes: 0 plain, 1 floor checker, 2 ball, 4 blob shadow, 5 coin (shiny), 6 ramp stripes.
    static ReadOnlySpan<byte> SceneFs =>
        """
        #version 410 core
        in vec3 vWorld;
        in vec3 vNormal;
        in vec3 vLocal;
        uniform vec4 uColor;
        uniform int uMode;
        uniform vec3 uLightDir;
        uniform vec3 uCamPos;
        uniform vec3 uFog;
        out vec4 fragColor;
        void main() {
            if (uMode == 4) {
                float r = length(vLocal.xz);
                fragColor = vec4(0.0, 0.0, 0.0, uColor.a * (1.0 - smoothstep(0.25, 1.0, r)));
                return;
            }
            vec3 n = normalize(vNormal);
            vec3 albedo = uColor.rgb;
            if (uMode == 1) {
                vec2 cell = floor(vWorld.xz / 2.0);
                albedo *= mix(0.84, 1.0, mod(cell.x + cell.y, 2.0));
                vec2 g = abs(fract(vWorld.xz / 2.0) - 0.5);
                albedo *= mix(1.0, 0.8, smoothstep(0.475, 0.5, max(g.x, g.y)));
            } else if (uMode == 2) {
                albedo = mix(albedo, vec3(0.97), step(abs(vLocal.y), 0.22));
                albedo = mix(albedo, vec3(0.12, 0.12, 0.18), step(0.9, abs(vLocal.x)));
            } else if (uMode == 6) {
                albedo *= mix(0.82, 1.0, step(0.5, fract(vLocal.z * 5.0)));
            }
            vec3 L = normalize(-uLightDir);
            float diff = max(dot(n, L), 0.0);
            vec3 ambient = mix(vec3(0.32, 0.28, 0.25), vec3(0.62, 0.72, 0.88), n.y * 0.5 + 0.5);
            vec3 V = normalize(uCamPos - vWorld);
            vec3 H = normalize(L + V);
            float shininess = uMode == 5 ? 60.0 : (uMode == 2 ? 40.0 : 16.0);
            float specK = uMode == 5 ? 1.0 : (uMode == 2 ? 0.55 : 0.08);
            float spec = pow(max(dot(n, H), 0.0), shininess) * specK * step(0.0, dot(n, L));
            vec3 color = albedo * (ambient * 0.55 + diff * vec3(1.0, 0.95, 0.85) * 0.8) + vec3(spec);
            if (uMode == 5) color += albedo * 0.22;
            float dist = length(uCamPos - vWorld);
            color = mix(color, uFog, smoothstep(40.0, 110.0, dist));
            fragColor = vec4(color, uColor.a);
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
