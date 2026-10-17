// Recipe 7.25: load a glTF 2.0 binary (.glb) with Brutal.GltfApi and draw its mesh with OpenGL 4.1 core.
// The test writes its own tiny .glb (a square with vertex colours), so no asset ships with the recipe.
// Needs a display (a hidden window is enough); skipped with --no-window.
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using Brutal.GltfApi;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using GL = Brutal.OpenGlApi.OpenGl.Native; // static class of typed GL function pointers

static unsafe partial class Recipes
{
    public static void GltfRecipe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recipecheck-{Environment.ProcessId}.glb");
        File.WriteAllBytes(path, MakeTestGlb());
        try
        {
            // ---- load ----
            using var loader = new GltfLoader(path); // parses JSON; buffers load lazily (GLB BIN chunk or files)
            var prim = loader.GltfJson.Meshes[0].Primitives[0];
            Check(prim.Mode == MeshPrimitive.ModeEnum.Triangles, $"primitive mode {prim.Mode}");
            var posAcc = loader.GltfJson.Accessors[prim.Attributes["POSITION"]];
            var colAcc = loader.GltfJson.Accessors[prim.Attributes["COLOR_0"]];
            var idxAcc = loader.GltfJson.Accessors[prim.Indices!.Value];
            Check(posAcc.ComponentType == Accessor.ComponentTypeEnum.Float && posAcc.Type == Accessor.TypeEnum.Vec3, "POSITION is float VEC3");
            Check(idxAcc.ComponentType == Accessor.ComponentTypeEnum.UnsignedShort, "indices are ushort");
            // Raw bytes of each accessor (no sparse accessors; interleaved views keep their stride).
            var positions = MemoryMarshal.Cast<byte, float3>(loader.GetAccessorBufferSpan(prim.Attributes["POSITION"])).ToArray();
            var colors = MemoryMarshal.Cast<byte, float3>(loader.GetAccessorBufferSpan(prim.Attributes["COLOR_0"])).ToArray();
            var indices = MemoryMarshal.Cast<byte, ushort>(loader.GetAccessorBufferSpan(prim.Indices.Value)).ToArray();
            Check(positions.Length == 4 && colors.Length == 4 && indices.Length == 6, $"4 vertices, 6 indices ({positions.Length}/{indices.Length})");
            Check(colAcc.Count == 4 && positions[0].Equals(new float3(-0.5f, -0.5f, 0)), "data round-trips");

            // ---- draw ----
            DrawWithOpenGl(positions, colors, indices);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static void DrawWithOpenGl(float3[] positions, float3[] colors, ushort[] indices)
    {
        if (!Glfw.Init())
            throw new Exception("GLFW init failed");
        GlfwWindow window = default!;
        var made = false;
        try
        {
            Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
            Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 4);
            Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 1);
            Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);
            const int W = 200, H = 200;
            window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "RecipeCheck glTF", Size = new int2(W, H) });
            made = true;
            Glfw.PInvoke.MakeContextCurrent(window);
            s_glLoader = name => Glfw.PInvoke.GetProcAddress(name); // shared with 7.21; rooted (T35)
            Check(Glad.Initialize(s_glLoader), "Glad.Initialize");

            var program = BuildProgram(
                """
                #version 410 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec3 aColor;
                out vec3 vColor;
                void main() { vColor = aColor; gl_Position = vec4(aPos, 1.0); }
                """u8,
                """
                #version 410 core
                in vec3 vColor;
                out vec4 fragColor;
                void main() { fragColor = vec4(vColor, 1.0); }
                """u8
            );

            GlVertexArray vao;
            GL.glGenVertexArrays(1, &vao);
            GL.glBindVertexArray(vao);
            var buffers = stackalloc GlBuffer[3];
            GL.glGenBuffers(3, buffers);
            fixed (float3* p = positions)
                Upload(OpenGl.GL_ARRAY_BUFFER, buffers[0], p, positions.Length * sizeof(float3));
            GL.glVertexAttribPointer(0, 3, OpenGl.GL_FLOAT, false, sizeof(float3), null);
            GL.glEnableVertexAttribArray(0);
            fixed (float3* p = colors)
                Upload(OpenGl.GL_ARRAY_BUFFER, buffers[1], p, colors.Length * sizeof(float3));
            GL.glVertexAttribPointer(1, 3, OpenGl.GL_FLOAT, false, sizeof(float3), null);
            GL.glEnableVertexAttribArray(1);
            fixed (ushort* p = indices) // the element buffer binding is part of the VAO
                Upload(OpenGl.GL_ELEMENT_ARRAY_BUFFER, buffers[2], p, indices.Length * sizeof(ushort));

            GL.glViewport(0, 0, W, H);
            GL.glClearColor(0, 0, 0, 1);
            GL.glClear(OpenGl.GL_COLOR_BUFFER_BIT);
            GL.glUseProgram(program);
            GL.glDrawElements(OpenGl.GL_TRIANGLES, indices.Length, OpenGl.GL_UNSIGNED_SHORT, null);

            var pixels = new byte[W * H * 4];
            fixed (byte* p = pixels)
                GL.glReadPixels(0, 0, W, H, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, p); // before SwapBuffers
            byte R(int x, int y) => pixels[(y * W + x) * 4]; // GL rows start at the bottom
            byte G(int x, int y) => pixels[(y * W + x) * 4 + 1];
            byte B(int x, int y) => pixels[(y * W + x) * 4 + 2];
            Check(R(5, 5) == 0 && G(5, 5) == 0 && B(5, 5) == 0, "outside the square is the clear colour");
            Check(R(52, 52) > 200 && G(52, 52) < 60 && B(52, 52) < 60, $"bottom-left vertex is red ({R(52, 52)},{G(52, 52)},{B(52, 52)})");
            Check(B(52, 147) > 200 && R(52, 147) < 60, $"top-left vertex is blue ({R(52, 147)},{G(52, 147)},{B(52, 147)})");
            Check(R(100, 100) > 40 && G(100, 100) > 40 && B(100, 100) > 40, "centre is a blend of the vertex colours");

            var shotDir = Environment.GetEnvironmentVariable("RECIPECHECK_SHOTS");
            if (!string.IsNullOrEmpty(shotDir))
            {
                var flipped = new byte[pixels.Length];
                for (var y = 0; y < H; y++)
                    Array.Copy(pixels, y * W * 4, flipped, (H - 1 - y) * W * 4, W * 4);
                Directory.CreateDirectory(shotDir);
                Brutal.StbApi.Stb.WritePng(Path.Combine(shotDir, "7.25-gltf.png"), W, H, 4, flipped, W * 4);
            }

            GL.glDeleteBuffers(3, buffers);
            GL.glDeleteVertexArrays(1, &vao);
            GL.glDeleteProgram(program);
        }
        finally
        {
            if (made)
                window.Dispose();
            Glfw.Terminate();
        }
    }

    static void Upload(GlEnum target, GlBuffer buffer, void* data, int bytes)
    {
        GL.glBindBuffer(target, buffer);
        GL.glBufferData(target, bytes, data, OpenGl.GL_STATIC_DRAW);
    }

    static GlProgram BuildProgram(ReadOnlySpan<byte> vertexSource, ReadOnlySpan<byte> fragmentSource)
    {
        GlShader Compile(GlShaderType type, ReadOnlySpan<byte> source)
        {
            var shader = GL.glCreateShader(type);
            fixed (byte* src = source)
            {
                var len = source.Length;
                GL.glShaderSource(shader, 1, &src, &len);
            }
            GL.glCompileShader(shader);
            int ok;
            GL.glGetShaderiv(shader, OpenGl.GL_COMPILE_STATUS, &ok);
            Check(ok != 0, $"{type} shader compiles");
            return shader;
        }
        var vs = Compile(GlShaderType.Vertex, vertexSource);
        var fs = Compile(GlShaderType.Fragment, fragmentSource);
        var program = GL.glCreateProgram();
        GL.glAttachShader(program, vs);
        GL.glAttachShader(program, fs);
        GL.glLinkProgram(program);
        int linked;
        GL.glGetProgramiv(program, OpenGl.GL_LINK_STATUS, &linked);
        Check(linked != 0, "program links");
        GL.glDeleteShader(vs); // the linked program keeps what it needs
        GL.glDeleteShader(fs);
        return program;
    }

    /// <summary>A minimal glTF 2.0 binary: one mesh, a square from two triangles, POSITION + COLOR_0 + ushort indices.</summary>
    static byte[] MakeTestGlb()
    {
        float3[] pos = [new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0)];
        float3[] col = [new(1, 0, 0), new(0, 1, 0), new(1, 1, 1), new(0, 0, 1)];
        ushort[] idx = [0, 1, 2, 0, 2, 3];
        var bin = new List<byte>();
        bin.AddRange(MemoryMarshal.AsBytes(pos.AsSpan()).ToArray()); // 48 bytes at 0
        bin.AddRange(MemoryMarshal.AsBytes(col.AsSpan()).ToArray()); // 48 bytes at 48
        bin.AddRange(MemoryMarshal.AsBytes(idx.AsSpan()).ToArray()); // 12 bytes at 96
        while (bin.Count % 4 != 0)
            bin.Add(0);
        var json = $$"""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"COLOR_0":1},"indices":2,"mode":4}]}],
             "buffers":[{"byteLength":{{bin.Count}}}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48,"target":34962},
                            {"buffer":0,"byteOffset":48,"byteLength":48,"target":34962},
                            {"buffer":0,"byteOffset":96,"byteLength":12,"target":34963}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3","min":[-0.5,-0.5,0],"max":[0.5,0.5,0]},
                          {"bufferView":1,"componentType":5126,"count":4,"type":"VEC3"},
                          {"bufferView":2,"componentType":5123,"count":6,"type":"SCALAR"}]}
            """;
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        while (jsonBytes.Count % 4 != 0)
            jsonBytes.Add((byte)' ');
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonBytes.Count + 8 + bin.Count));
        w.Write((uint)jsonBytes.Count);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(jsonBytes.ToArray());
        w.Write((uint)bin.Count);
        w.Write(0x004E4942u); // "BIN\0"
        w.Write(bin.ToArray());
        return ms.ToArray();
    }
}
