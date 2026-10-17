using System.Text;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using GL = Brutal.OpenGlApi.OpenGl.Native; // static class of typed GL function pointers (T35)

namespace BrutalGame3D.Platform;

/// <summary>Small OpenGL 4.1 core helpers: shader programs, static meshes, RGBA textures.</summary>
public static unsafe class GlUtil
{
    public static GlProgram BuildProgram(ReadOnlySpan<byte> vertexSource, ReadOnlySpan<byte> fragmentSource)
    {
        var vs = Compile(GlShaderType.Vertex, vertexSource);
        var fs = Compile(GlShaderType.Fragment, fragmentSource);
        var program = GL.glCreateProgram();
        GL.glAttachShader(program, vs);
        GL.glAttachShader(program, fs);
        GL.glLinkProgram(program);
        int linked;
        GL.glGetProgramiv(program, OpenGl.GL_LINK_STATUS, &linked);
        GL.glDeleteShader(vs); // the linked program keeps what it needs
        GL.glDeleteShader(fs);
        if (linked == 0)
        {
            var log = stackalloc byte[2048];
            int len;
            GL.glGetProgramInfoLog(program, 2048, &len, log);
            throw new InvalidOperationException("GL program link failed: " + Encoding.UTF8.GetString(log, len));
        }
        return program;
    }

    static GlShader Compile(GlShaderType type, ReadOnlySpan<byte> source)
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
        if (ok == 0)
        {
            var log = stackalloc byte[2048];
            int len;
            GL.glGetShaderInfoLog(shader, 2048, &len, log);
            throw new InvalidOperationException($"GL {type} shader compile failed: " + Encoding.UTF8.GetString(log, len));
        }
        return shader;
    }

    /// <summary>Uniform location; <paramref name="name"/> must be a u8 literal (they're null-terminated).</summary>
    public static int Uniform(GlProgram program, ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name)
            return GL.glGetUniformLocation(program, p);
    }

    public static void SetMatrix(int location, in Mat4 m)
    {
        fixed (float* p = m.M)
            GL.glUniformMatrix4fv(location, 1, false, p);
    }

    public static GlTexture CreateTexture(int w, int h, void* rgba, bool linear = true)
    {
        GlTexture tex;
        GL.glGenTextures(1, &tex);
        GL.glBindTexture(OpenGl.GL_TEXTURE_2D, tex.Id);
        GL.glPixelStorei(OpenGl.GL_UNPACK_ALIGNMENT, 1);
        GL.glTexImage2D(OpenGl.GL_TEXTURE_2D, 0, (int)OpenGl.GL_RGBA8, w, h, 0, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, rgba);
        var filter = (int)(linear ? OpenGl.GL_LINEAR : OpenGl.GL_NEAREST);
        GL.glTexParameteri(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_MIN_FILTER, filter);
        GL.glTexParameteri(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_MAG_FILTER, filter);
        GL.glTexParameteri(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_WRAP_S, (int)OpenGl.GL_CLAMP_TO_EDGE);
        GL.glTexParameteri(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_WRAP_T, (int)OpenGl.GL_CLAMP_TO_EDGE);
        return tex;
    }

    public static void DeleteTexture(GlTexture tex) => GL.glDeleteTextures(1, &tex);
}

/// <summary>An indexed triangle mesh with position + normal vertices, uploaded once.</summary>
public sealed unsafe class Mesh : IDisposable
{
    public readonly record struct Vertex(float3 Position, float3 Normal);

    GlVertexArray _vao;
    GlBuffer _vbo;
    GlBuffer _ibo;
    readonly int _count;

    public Mesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<ushort> indices)
    {
        _count = indices.Length;
        GlVertexArray vao;
        GL.glGenVertexArrays(1, &vao);
        _vao = vao;
        GL.glBindVertexArray(vao);
        var buffers = stackalloc GlBuffer[2];
        GL.glGenBuffers(2, buffers);
        _vbo = buffers[0];
        _ibo = buffers[1];
        GL.glBindBuffer(OpenGl.GL_ARRAY_BUFFER, _vbo);
        fixed (Vertex* p = vertices)
            GL.glBufferData(OpenGl.GL_ARRAY_BUFFER, vertices.Length * sizeof(Vertex), p, OpenGl.GL_STATIC_DRAW);
        GL.glVertexAttribPointer(0, 3, OpenGl.GL_FLOAT, false, sizeof(Vertex), null);
        GL.glEnableVertexAttribArray(0);
        GL.glVertexAttribPointer(1, 3, OpenGl.GL_FLOAT, false, sizeof(Vertex), (void*)sizeof(float3));
        GL.glEnableVertexAttribArray(1);
        GL.glBindBuffer(OpenGl.GL_ELEMENT_ARRAY_BUFFER, _ibo); // the element buffer binding is part of the VAO
        fixed (ushort* p = indices)
            GL.glBufferData(OpenGl.GL_ELEMENT_ARRAY_BUFFER, indices.Length * sizeof(ushort), p, OpenGl.GL_STATIC_DRAW);
        GL.glBindVertexArray(default);
    }

    public void Draw()
    {
        GL.glBindVertexArray(_vao);
        GL.glDrawElements(OpenGl.GL_TRIANGLES, _count, OpenGl.GL_UNSIGNED_SHORT, null);
    }

    public void Dispose()
    {
        var buffers = stackalloc GlBuffer[2] { _vbo, _ibo };
        GL.glDeleteBuffers(2, buffers);
        var vao = _vao;
        GL.glDeleteVertexArrays(1, &vao);
    }

    // ---- shapes ----------------------------------------------------------------------------------------------

    /// <summary>A cube from -1 to 1 with flat normals (scale it by the half extents).</summary>
    public static Mesh Cube()
    {
        var v = new List<Vertex>();
        var idx = new List<ushort>();
        float3[] normals = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        foreach (var n in normals)
        {
            // Two axes spanning the face, ordered so the winding is counter-clockwise seen from outside.
            var a = MathF.Abs(n.Y) > 0.5f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            var b = float3.Cross(n, a);
            var start = (ushort)v.Count;
            v.Add(new(n - a - b, n));
            v.Add(new(n + a - b, n));
            v.Add(new(n + a + b, n));
            v.Add(new(n - a + b, n));
            // Check orientation: (p1 - p0) x (p2 - p0) should point along n.
            var cross = float3.Cross(2 * a, 2 * a + 2 * b);
            if (float3.Dot(cross, n) > 0)
                idx.AddRange([start, (ushort)(start + 1), (ushort)(start + 2), start, (ushort)(start + 2), (ushort)(start + 3)]);
            else
                idx.AddRange([start, (ushort)(start + 2), (ushort)(start + 1), start, (ushort)(start + 3), (ushort)(start + 2)]);
        }
        return new Mesh(v.ToArray(), idx.ToArray());
    }

    /// <summary>A UV sphere of radius 1.</summary>
    public static Mesh Sphere(int slices = 32, int stacks = 20)
    {
        var v = new List<Vertex>();
        var idx = new List<ushort>();
        for (var i = 0; i <= stacks; i++)
        {
            var phi = MathF.PI * i / stacks;
            for (var j = 0; j <= slices; j++)
            {
                var theta = MathF.Tau * j / slices;
                var n = new float3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                v.Add(new(n, n));
            }
        }
        for (var i = 0; i < stacks; i++)
        for (var j = 0; j < slices; j++)
        {
            var a = (ushort)(i * (slices + 1) + j);
            var b = (ushort)(a + slices + 1);
            idx.AddRange([a, (ushort)(a + 1), b, (ushort)(a + 1), (ushort)(b + 1), b]);
        }
        return new Mesh(v.ToArray(), idx.ToArray());
    }

    /// <summary>A closed cylinder of radius 1 along Y from -1 to 1, with flat caps.</summary>
    public static Mesh Cylinder(int segments = 32)
    {
        var v = new List<Vertex>();
        var idx = new List<ushort>();
        for (var i = 0; i <= segments; i++)
        {
            var t = MathF.Tau * i / segments;
            var n = new float3(MathF.Cos(t), 0, MathF.Sin(t));
            v.Add(new(n + new float3(0, -1, 0), n));
            v.Add(new(n + new float3(0, 1, 0), n));
        }
        for (var i = 0; i < segments; i++)
        {
            var a = (ushort)(i * 2);
            idx.AddRange([a, (ushort)(a + 1), (ushort)(a + 2), (ushort)(a + 1), (ushort)(a + 3), (ushort)(a + 2)]);
        }
        foreach (var y in new[] { 1f, -1f })
        {
            var n = new float3(0, y, 0);
            var centre = (ushort)v.Count;
            v.Add(new(n, n));
            for (var i = 0; i <= segments; i++)
            {
                var t = MathF.Tau * i / segments;
                v.Add(new(new float3(MathF.Cos(t), y, MathF.Sin(t)), n));
            }
            for (var i = 0; i < segments; i++)
            {
                var a = (ushort)(centre + 1 + i);
                if (y > 0)
                    idx.AddRange([centre, (ushort)(a + 1), a]);
                else
                    idx.AddRange([centre, a, (ushort)(a + 1)]);
            }
        }
        return new Mesh(v.ToArray(), idx.ToArray());
    }

    /// <summary>A flat disc of radius 1 in the XZ plane facing +Y (for the blob shadow).</summary>
    public static Mesh Disc(int segments = 32)
    {
        var v = new List<Vertex> { new(float3.Zero, new float3(0, 1, 0)) };
        var idx = new List<ushort>();
        for (var i = 0; i <= segments; i++)
        {
            var t = MathF.Tau * i / segments;
            v.Add(new(new float3(MathF.Cos(t), 0, MathF.Sin(t)), new float3(0, 1, 0)));
        }
        for (var i = 0; i < segments; i++)
            idx.AddRange([0, (ushort)(i + 2), (ushort)(i + 1)]);
        return new Mesh(v.ToArray(), idx.ToArray());
    }
}
