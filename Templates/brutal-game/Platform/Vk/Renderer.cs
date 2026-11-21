using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Sim;

namespace BrutalGame.Platform;

/// <summary>What the renderer needs from the world for one frame, interpolated between fixed steps.</summary>
public readonly record struct View(float2 Marker)
{
    public static View Of(World w) => new(w.Marker);

    public static View Lerp(in View a, in View b, float t) => new(float2.Lerp(a.Marker, b.Marker, t));
}

[StructLayout(LayoutKind.Sequential)]
struct MeshPush
{
    public float4x4 Mvp; // row-vector Numerics matrix, uploaded unchanged: `pc.mvp * v` is right in GLSL (recipe 7.29)
    public float4 N0, N1, N2; // rows of the model's 3x3, for normals
    public float4 Color; // 128 bytes in all: the guaranteed minimum maxPushConstantsSize
}

[StructLayout(LayoutKind.Sequential)]
struct QuadPush
{
    public float4 Rect; // x0, y0, x1, y1 in NDC (Vulkan: y = -1 is the top)
    public float4 Tint; // multiplies the texture
}

/// <summary>
/// Draws the game with Vulkan into a framebuffer of the render pass it owns: the licence splash, a lit 3D scene with a
/// depth buffer, and a HUD of textured quads (text from <see cref="VkText"/>). It doesn't know about swapchains, so
/// the window and --snapshot (off-screen) share it. The scene is a placeholder (a floor and the marker as a cube):
/// replace <see cref="RecordScene"/> and <see cref="RecordHud"/> with your game; keep the splash and pause overlay.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    const int MaxTextures = 8;
    static readonly float4 Sky = new(0.62f, 0.74f, 0.88f, 1);

    readonly VkGpu _gpu;
    DeviceEx Device => _gpu.Device;
    public VkRenderPass RenderPass { get; }

    readonly VkPipelineLayout _meshLayout;
    readonly VkPipeline _meshPipeline;
    readonly BufferEx _cubeVertices, _cubeIndices;
    readonly int _cubeIndexCount;

    readonly VkDescriptorSetLayout _quadSetLayout;
    readonly VkDescriptorPool _descriptorPool;
    readonly VkSampler _sampler;
    readonly VkPipelineLayout _quadLayout;
    readonly VkPipeline _quadPipeline;
    readonly List<VkTexture> _textures = [];
    readonly Dictionary<int, VkDescriptorSet> _sets = [];
    readonly int _splash, _white, _help, _paused, _hint;

    /// <param name="finalLayout">PresentSrcKHR for a swapchain image, TransferSrcOptimal to read the pixels back.</param>
    public Renderer(VkGpu gpu, VkFormat colorFormat, VkImageLayout finalLayout)
    {
        _gpu = gpu;
        RenderPass = CreateRenderPass(colorFormat, finalLayout);

        // Meshes: one cube from -1 to 1 (scale it by the half extents), host-visible so it's easy to change.
        var (vertices, indices) = BuildCube();
        _cubeIndexCount = indices.Length;
        _cubeVertices = gpu.CreateHostBuffer("cube vertices", ByteSize.Of<float3>(vertices.Length), VkBufferUsageFlags.VertexBufferBit);
        _cubeIndices = gpu.CreateHostBuffer("cube indices", ByteSize.Of<ushort>(indices.Length), VkBufferUsageFlags.IndexBufferBit);
        using (var m = _cubeVertices.Map())
            vertices.AsSpan().CopyTo(m.AsSpan<float3>());
        using (var m = _cubeIndices.Map())
            indices.AsSpan().CopyTo(m.AsSpan<ushort>());
        _meshLayout = Device.CreatePipelineLayout([], [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = ByteSize.Of<MeshPush>() }], null);
        _meshPipeline = CreatePipeline(MeshVs, MeshFs, _meshLayout, mesh: true);

        // Quads: one combined image sampler per texture, a tint and a rectangle in push constants, alpha blending.
        var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        _quadSetLayout = Device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = MaxTextures };
        _descriptorPool = Device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = MaxTextures, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
        _sampler = Device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
            },
            null
        );
        _quadLayout = Device.CreatePipelineLayout([_quadSetLayout], [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = ByteSize.Of<QuadPush>() }], null);
        _quadPipeline = CreatePipeline(QuadVs, QuadFs, _quadLayout, mesh: false);

        _splash = AddTexture("splash", VkText.Splash());
        _white = AddTexture("white", new Pixels([255, 255, 255, 255], 1, 1));
        _help = AddTexture("help", VkText.Render("WASD / arrows move    P pause    Esc quit", 17));
        _paused = AddTexture("paused", VkText.Render("Paused", 64));
        _hint = AddTexture("hint", VkText.Render("Press P to continue", 20));
    }

    /// <summary>Uploads <paramref name="pixels"/> and returns its index for <see cref="Quad"/>. Up to <see cref="MaxTextures"/>.</summary>
    public int AddTexture(string name, Pixels pixels)
    {
        if (_textures.Count == MaxTextures)
            throw new InvalidOperationException($"raise Renderer.MaxTextures ({MaxTextures})");
        var tex = _gpu.UploadTexture(name, pixels.Rgba, pixels.Width, pixels.Height);
        var layout = _quadSetLayout;
        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1]; // an output span you own (T3)
        Device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = _descriptorPool, DescriptorSetCount = 1, SetLayouts = &layout }, sets);
        var imageInfo = new VkDescriptorImageInfo { Sampler = _sampler, ImageView = tex.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = sets[0], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo }], []);
        _textures.Add(tex);
        _sets[_textures.Count - 1] = sets[0];
        return _textures.Count - 1;
    }

    VkRenderPass CreateRenderPass(VkFormat colorFormat, VkImageLayout finalLayout)
    {
        var attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new VkAttachmentDescription
        {
            Format = colorFormat,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = finalLayout,
        };
        attachments[1] = new VkAttachmentDescription
        {
            Format = _gpu.DepthFormat,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &colorRef, DepthStencilAttachment = &depthRef };
        var stages = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        var dependencies = stackalloc VkSubpassDependency[2];
        dependencies[0] = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = stages,
            DstStageMask = stages,
            SrcAccessMask = VkAccessFlags.DepthStencilAttachmentWriteBit, // the previous frame's depth writes (one depth image)
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };
        // Off-screen read-back: make the colour writes visible to the copy that follows the pass (recipe 7.29).
        dependencies[1] = new VkSubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = VK.SUBPASS_EXTERNAL,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.TransferBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.TransferReadBit,
        };
        var count = finalLayout == VkImageLayout.TransferSrcOptimal ? 2 : 1;
        return Device.CreateRenderPass(
            new VkRenderPassCreateInfo { AttachmentCount = 2, Attachments = attachments, SubpassCount = 1, Subpasses = &subpass, DependencyCount = count, Dependencies = dependencies },
            null
        );
    }

    VkPipeline CreatePipeline(ReadOnlySpan<byte> vsSource, ReadOnlySpan<byte> fsSource, VkPipelineLayout layout, bool mesh)
    {
        var vs = _gpu.CompileShader(vsSource, ShaderKind.VertexShader, mesh ? "mesh.vert"u8 : "quad.vert"u8);
        var fs = _gpu.CompileShader(fsSource, ShaderKind.FragmentShader, mesh ? "mesh.frag"u8 : "quad.frag"u8);
        try
        {
            fixed (byte* entry = "main"u8)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
                stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = vs, Name = entry };
                stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = fs, Name = entry };
                // Mesh vertices: position + normal, interleaved as two float3s. Quads: corners from gl_VertexIndex.
                var binding = new VkVertexInputBindingDescription { Binding = 0, Stride = ByteSize.Of<float3>(2), InputRate = VkVertexInputRate.Vertex };
                var attributes = stackalloc VkVertexInputAttributeDescription[2];
                attributes[0] = new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = default };
                attributes[1] = new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float3>() };
                var vertexInput = mesh
                    ? new VkPipelineVertexInputStateCreateInfo { VertexBindingDescriptionCount = 1, VertexBindingDescriptions = &binding, VertexAttributeDescriptionCount = 2, VertexAttributeDescriptions = attributes }
                    : new VkPipelineVertexInputStateCreateInfo();
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
                var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 }; // dynamic
                // With the projection's Y flip, counter-clockwise faces are the front ones (recipe 7.29).
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = mesh ? VkCullModeFlags.BackBit : VkCullModeFlags.None,
                    FrontFace = VkFrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var depthState = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = mesh, DepthWriteEnable = mesh, DepthCompareOp = VkCompareOp.Less };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    BlendEnable = !mesh,
                    SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                    DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = VkBlendOp.Add,
                    SrcAlphaBlendFactor = VkBlendFactor.One,
                    DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = VkBlendOp.Add,
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
                var dynamics = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamics };
                return Device.CreateGraphicsPipeline(
                    default,
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &inputAssembly,
                        ViewportState = &viewportState,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        DepthStencilState = &depthState,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = layout,
                        RenderPass = RenderPass,
                        Subpass = 0,
                    },
                    null
                );
            }
        }
        finally
        {
            Device.DestroyShaderModule(vs, null);
            Device.DestroyShaderModule(fs, null);
        }
    }

    /// <summary>Records one frame's render pass into <paramref name="cmd"/>, drawing into <paramref name="framebuffer"/>.</summary>
    public void Record(CommandBuffer cmd, VkFramebuffer framebuffer, VkExtent2D extent, ScreenFlow flow, in View view, bool paused)
    {
        var clears = stackalloc VkClearValue[2];
        clears[0] = new VkClearValue { Color = new VkClearColorValue { Float32 = flow.Current == Screen.Splash ? new float4(0, 0, 0, 1) : Sky } };
        clears[1] = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1 } };
        cmd.BeginRenderPass(
            new VkRenderPassBeginInfo { RenderPass = RenderPass, Framebuffer = framebuffer, RenderArea = new VkRect2D { Extent = extent }, ClearValueCount = 2, ClearValues = clears },
            VkSubpassContents.Inline
        );
        cmd.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D { Extent = extent }]);
        if (flow.Current == Screen.Splash)
            RecordSplash(cmd, extent, flow.SplashAlpha);
        else
        {
            RecordScene(cmd, extent, view);
            RecordHud(cmd, extent, paused);
        }
        cmd.EndRenderPass();
    }

    // ---- 3D --------------------------------------------------------------------------------------------------

    /// <summary>World X/Y map to 3D X/Z, centred on the origin; the camera looks down at the field from the front.</summary>
    static float3 ToWorld(float2 p, float height) => new(p.X - World.FieldSize.X / 2, height, p.Y - World.FieldSize.Y / 2);

    void RecordScene(CommandBuffer cmd, VkExtent2D extent, in View view)
    {
        // Numerics is row-vector: mvp = model * view * proj. Vulkan clip depth is 0..1 already; flip Y so +Y is up (7.29).
        var proj = float4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180f, extent.Width / (float)Math.Max(1, extent.Height), 0.1f, 100f);
        proj.M22 = -proj.M22;
        var viewProj = float4x4.CreateLookAt(new float3(0, 11, 12), new float3(0, 0, 0.8f), new float3(0, 1, 0)) * proj;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _meshPipeline);
        cmd.BindVertexBuffer(0, _cubeVertices.VkBuffer);
        cmd.BindIndexBuffer(_cubeIndices.VkBuffer, default, VkIndexType.UInt16);
        var half = World.FieldSize / 2;
        DrawCube(cmd, float4x4.CreateScale(half.X, 0.1f, half.Y) * float4x4.CreateTranslation(0, -0.1f, 0), viewProj, new float4(0.42f, 0.50f, 0.62f, 1));
        var s = World.MarkerHalfSize;
        DrawCube(cmd, float4x4.CreateScale(s) * float4x4.CreateTranslation(ToWorld(view.Marker, s)), viewProj, new float4(0.31f, 0.78f, 1f, 1));
    }

    void DrawCube(CommandBuffer cmd, in float4x4 model, in float4x4 viewProj, float4 color)
    {
        var push = new MeshPush
        {
            Mvp = model * viewProj,
            N0 = new float4(model.M11, model.M12, model.M13, 0),
            N1 = new float4(model.M21, model.M22, model.M23, 0),
            N2 = new float4(model.M31, model.M32, model.M33, 0),
            Color = color,
        };
        cmd.PushConstants(_meshLayout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);
        cmd.DrawIndexed(_cubeIndexCount, 1, 0, 0, 0);
    }

    // ---- 2D --------------------------------------------------------------------------------------------------

    /// <summary>Textured quad in output pixels (y down), tinted. <paramref name="texture"/> comes from <see cref="AddTexture"/>.</summary>
    void Quad(CommandBuffer cmd, VkExtent2D extent, int texture, float x, float y, float w, float h, float4 tint)
    {
        Span<VkDescriptorSet> set = [_sets[texture]];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _quadLayout, 0, set, []);
        float ew = extent.Width, eh = extent.Height;
        var rect = new float4(x / ew * 2 - 1, y / eh * 2 - 1, (x + w) / ew * 2 - 1, (y + h) / eh * 2 - 1);
        cmd.PushConstants(_quadLayout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, new QuadPush { Rect = rect, Tint = tint });
        cmd.Draw(6, 1, 0, 0);
    }

    /// <summary>A text texture at its rasterised size times <paramref name="ui"/> (output height / 720), x aligned by <paramref name="anchor"/> (0 left, 0.5 centre, 1 right).</summary>
    void Text(CommandBuffer cmd, VkExtent2D extent, int texture, float x, float y, float ui, float4 tint, float anchor = 0)
    {
        var t = _textures[texture];
        float w = t.Width * ui, h = t.Height * ui;
        Quad(cmd, extent, texture, MathF.Round(x - w * anchor), MathF.Round(y), w, h, tint);
    }

    void RecordSplash(CommandBuffer cmd, VkExtent2D extent, float alpha)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _quadPipeline);
        // Letterbox (7.31): at most 80 % of the output and 1.5x the canvas, aspect ratio kept.
        var tex = _textures[_splash];
        float w = extent.Width, h = extent.Height;
        var scale = MathF.Min(MathF.Min(0.8f * w / tex.Width, 0.8f * h / tex.Height), 1.5f);
        float sw = tex.Width * scale, sh = tex.Height * scale;
        Quad(cmd, extent, _splash, (w - sw) / 2, (h - sh) / 2, sw, sh, new float4(alpha, alpha, alpha, 1)); // fades through black
    }

    void RecordHud(CommandBuffer cmd, VkExtent2D extent, bool paused)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _quadPipeline);
        var ui = extent.Height / 720f;
        if (!paused)
            Text(cmd, extent, _help, 20 * ui, 18 * ui, ui, new float4(0.1f, 0.12f, 0.18f, 0.8f));
        else
        {
            Quad(cmd, extent, _white, 0, 0, extent.Width, extent.Height, new float4(0, 0, 0, 0.5f));
            Text(cmd, extent, _paused, extent.Width / 2f, 260 * ui, ui, new float4(0.95f, 0.95f, 0.95f, 1), 0.5f);
            Text(cmd, extent, _hint, extent.Width / 2f, 360 * ui, ui, new float4(0.75f, 0.75f, 0.8f, 1), 0.5f);
        }
    }

    /// <summary>24 vertices (position, normal) and 36 indices; counter-clockwise seen from outside.</summary>
    static (float3[] Vertices, ushort[] Indices) BuildCube()
    {
        var vertices = new List<float3>();
        var indices = new List<ushort>();
        float3[] normals = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        foreach (var n in normals)
        {
            // u x v = n, so corners (-u-v, +u-v, +u+v, -u+v) run counter-clockwise seen from the +n side.
            var u = MathF.Abs(n.X) > 0.5f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            var v = float3.Cross(n, u);
            var b = (ushort)(vertices.Count / 2);
            foreach (var c in new[] { n - u - v, n + u - v, n + u + v, n - u + v })
            {
                vertices.Add(c);
                vertices.Add(n);
            }
            indices.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), (ushort)(b + 2), (ushort)(b + 3), b]);
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    public void Dispose()
    {
        Device.WaitIdle();
        foreach (var t in _textures)
            t.Destroy(Device);
        Device.DestroyPipeline(_quadPipeline, null);
        Device.DestroyPipelineLayout(_quadLayout, null);
        Device.DestroySampler(_sampler, null);
        Device.DestroyDescriptorPool(_descriptorPool, null); // frees the sets
        Device.DestroyDescriptorSetLayout(_quadSetLayout, null);
        Device.DestroyPipeline(_meshPipeline, null);
        Device.DestroyPipelineLayout(_meshLayout, null);
        _cubeIndices.Dispose();
        _cubeVertices.Dispose();
        Device.DestroyRenderPass(RenderPass, null);
    }

    // ---- shaders (GLSL, compiled to SPIR-V at start-up with ShaderC, recipe 7.7) -------------------------------

    static ReadOnlySpan<byte> MeshVs =>
        """
        #version 450
        layout(push_constant) uniform Push { mat4 mvp; vec4 n0; vec4 n1; vec4 n2; vec4 color; } pc;
        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec3 inNormal;
        layout(location = 0) out vec3 vNormal;
        void main() {
            gl_Position = pc.mvp * vec4(inPos, 1.0);
            vNormal = mat3(pc.n0.xyz, pc.n1.xyz, pc.n2.xyz) * inNormal; // row-vector n * M3
        }
        """u8;

    // Directional sun plus a sky/ground ambient: enough to read 3D shapes.
    static ReadOnlySpan<byte> MeshFs =>
        """
        #version 450
        layout(push_constant) uniform Push { mat4 mvp; vec4 n0; vec4 n1; vec4 n2; vec4 color; } pc;
        layout(location = 0) in vec3 vNormal;
        layout(location = 0) out vec4 outColor;
        const vec3 TO_LIGHT = normalize(vec3(0.45, 1.0, 0.3));
        void main() {
            vec3 n = normalize(vNormal);
            float diff = max(dot(n, TO_LIGHT), 0.0);
            vec3 ambient = mix(vec3(0.32, 0.28, 0.25), vec3(0.62, 0.72, 0.88), n.y * 0.5 + 0.5);
            outColor = vec4(pc.color.rgb * (ambient * 0.55 + diff * vec3(1.0, 0.95, 0.85) * 0.8), pc.color.a);
        }
        """u8;

    static ReadOnlySpan<byte> QuadVs =>
        """
        #version 450
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(location = 0) out vec2 vUv;
        const vec2 CORNERS[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
        void main() {
            vUv = CORNERS[gl_VertexIndex];
            gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, vUv), 0.0, 1.0);
        }
        """u8;

    static ReadOnlySpan<byte> QuadFs =>
        """
        #version 450
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(set = 0, binding = 0) uniform sampler2D tex;
        layout(location = 0) in vec2 vUv;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = texture(tex, vUv) * pc.tint; }
        """u8;
}
