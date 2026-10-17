// RecipeCheck: executes the brutal skill's recipes and checks their results.
// Usage: dotnet run -c Release --project Sandbox/RecipeCheck [-- <recipe filter...>] [--no-window] [--no-vulkan]
// Exit code = number of failed recipes (0 = all pass).
using System.Diagnostics;
using Brutal;
using Brutal.Box2DApi;
using Brutal.Collections;
using Brutal.Concurrency.Jobs;
using Brutal.GlfwApi;
using Brutal.JoltApi;
using Brutal.Logging;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Microsoft.Extensions.Logging;

var noWindow = args.Contains("--no-window");
var noVulkan = args.Contains("--no-vulkan");
var filters = args.Where(a => !a.StartsWith("--")).ToArray();

var recipes = new (string Id, string Name, Action Run)[]
{
    ("7.1", "Logging", Recipes.Logging),
    ("7.2", "Numerics", Recipes.Numerics),
    ("7.3", "NativeList", Recipes.NativeListRecipe),
    ("7.4", "Jobs", Recipes.Jobs),
    ("7.5", "Box2D", Recipes.Box2DRecipe),
    ("7.6", "Jolt", Recipes.JoltRecipe),
    ("T36", "Jolt mesh/hull trap + fallbacks", Recipes.JoltMeshHullTrap),
    ("7.7", "ShaderC", Recipes.ShaderCRecipe),
    ("7.8", "GLFW", noWindow ? Recipes.GlfwNullPlatform : Recipes.GlfwWindow),
    ("7.10", "Stb round trip + T1", Recipes.StbRoundTrip),
    ("7.11", "Vulkan instance", noVulkan ? () => throw new SkipException("--no-vulkan") : Recipes.VulkanInstance),
    ("7.18", "SDL_image", Recipes.SdlImageRecipe),
    ("7.19", "SDL_ttf", Recipes.SdlTtfRecipe),
    ("7.20", "SDL_mixer", Recipes.SdlMixerRecipe),
};

var failed = 0;
foreach (var (id, name, run) in recipes)
{
    if (filters.Length > 0 && !filters.Any(f => id == f || name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        continue;
    var sw = Stopwatch.StartNew();
    try
    {
        run();
        Console.WriteLine($"PASS {id,-5} {name} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (SkipException e)
    {
        Console.WriteLine($"SKIP {id,-5} {name}: {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL {id,-5} {name}: {e.GetType().Name}: {e.Message}");
    }
}
Console.WriteLine(failed == 0 ? "All recipes passed." : $"{failed} recipe(s) failed.");
return failed;

sealed class SkipException(string message) : Exception(message);

sealed class FillJob(int[] d, int s, int e) : IJob
{
    public void Run()
    {
        for (var i = s; i < e; i++)
            d[i] = 1;
    }
}

static unsafe partial class Recipes
{
    internal static void Check(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"check failed: {what}");
    }

    public static void Logging()
    {
        LogSystem.Initialize(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        var log = LogCategory.Make("RecipeCheck");
        log.Info($"Started on {Environment.ProcessorCount} cores");
    }

    public static void Numerics()
    {
        var p = new double3(1e9, 0, 0) + new double3(0.001, 0, 0);
        Check(p.X == 1e9 + 0.001, "double3 keeps precision");
        var v = new float3(3, 4, 0);
        Check(v.Length() == 5, "float3.Length() == 5");
        var yx = v.YX;
        Check(yx.X == 4 && yx.Y == 3, "swizzle YX");
        Check(float3.NormalizeOrZero(float3.Zero).Equals(float3.Zero), "NormalizeOrZero(zero)");
        var moved = float3.Transform(float3.Zero, float4x4.CreateTranslation(new float3(1, 2, 3)));
        Check(moved.Equals(new float3(1, 2, 3)), "Transform by translation");
        var threw = false;
        try
        {
            _ = float3.Normalize(float3.Zero);
        }
        catch (Exception)
        {
            threw = true;
        }
        Check(threw, "Normalize(zero) throws (T19)");
    }

    public static void NativeListRecipe()
    {
        using var list = new NativeList<float2>();
        for (var i = 0; i < 10; i++)
            list.Add(new float2(i, 2 * i));
        var sum = 0f;
        foreach (var item in list.AsSpan())
            sum += item.Y;
        Check(list.Count.Value == 10, "Count.Value == 10");
        Check(sum == 90, "sum over AsSpan()");
    }

    public static void Jobs()
    {
        var data = new int[100_000];
        var scheduler = new JobScheduler(4, "Workers");
        for (var s = 0; s < data.Length; s += 10_000)
            scheduler.Queue(new FillJob(data, s, Math.Min(s + 10_000, data.Length)), timeCostEstimate: 1);
        scheduler.ExecuteJobs();
        scheduler.Wait();
        Check(data.Sum() == data.Length, "every element filled");
    }

    public static void Box2DRecipe()
    {
        var world = Box2D.CreateWorld(B2WorldDef.Default with { Gravity = new float2(0, -10) });
        var ground = world.CreateBody(B2BodyDef.Default with { Position = new float2(0, -1) });
        ground.CreatePolygonShape(B2ShapeDef.Default, Box2D.MakeBox(50, 1));
        var body = world.CreateBody(B2BodyDef.Default with { Type = B2BodyType.Dynamic, Position = new float2(0, 10) });
        body.CreatePolygonShape(B2ShapeDef.Default, Box2D.MakeBox(0.5f, 0.5f));
        for (var i = 0; i < 240; i++)
            world.Step(1 / 60f, 4);
        float2 pos = body.GetPosition();
        world.Dispose();
        Check(Math.Abs(pos.Y - 0.5f) < 0.05f, $"box rests on the ground (y={pos.Y})");
    }

    public static void JoltRecipe()
    {
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create();
        Jolt.RegisterTypes();
        const ushort Static = 0, Moving = 1;
        var bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
        bp.MapObjectToBroadPhaseLayer(Static, 0);
        bp.MapObjectToBroadPhaseLayer(Moving, 1);
        var pairs = ObjectLayerPairFilterTable.Create(2);
        pairs.EnableCollision(Moving, Static);
        pairs.EnableCollision(Moving, Moving);
        var objVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(bp, 2, pairs, 2);
        var temp = TempAllocatorImpl.Create(10 * 1024 * 1024);
        var jobs = JobSystemThreadPool.Create(2048, 8, Environment.ProcessorCount - 1);
        var physics = PhysicsSystem.Create();
        physics.Init(1024, 0, 1024, 1024, bp, objVsBp, pairs);
        physics.SetGravity(new float3(0, -9.81f, 0));
        var bodies = physics.GetBodyInterface();

        uint floor;
        using (var fs = BoxShapeSettings.Create(new float3(50, 1, 50), 0.05f, default))
        using (var shape = fs.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, new float3(0, -1, 0), floatQuat.Identity, MotionType.Static, Static))
            floor = bodies.CreateAndAddBody(bcs, Activation.DontActivate);

        uint ball;
        using (var ss = SphereShapeSettings.Create(0.5f))
        using (var shape = ss.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, new float3(0, 5, 0), floatQuat.Identity, MotionType.Dynamic, Moving))
            ball = bodies.CreateAndAddBody(bcs, Activation.Activate);

        physics.OptimizeBroadPhase();
        for (var i = 0; i < 120; i++)
            if (physics.Update(1 / 60f, 1, temp, jobs) != PhysicsUpdateError.None)
                throw new Exception("Jolt update error");
        float3 p = bodies.GetPosition(ball);

        bodies.RemoveBody(ball);
        bodies.DestroyBody(ball);
        bodies.RemoveBody(floor);
        bodies.DestroyBody(floor);
        physics.Dispose();
        jobs.Dispose();
        temp.Dispose();
        objVsBp.Dispose();
        pairs.Dispose();
        bp.Dispose();
        Jolt.UnregisterTypes();
        Factory.DestroyInstance();
        Check(Math.Abs(p.Y - 0.5f) < 0.1f, $"ball rests on the floor (y={p.Y})");
    }

    // T36 (skill references/physics.md; ahwootech/BRUTAL issue #13): in Jolt 2026.10.2 the data-taking
    // MeshShapeSettings / ConvexHullShapeSettings / HeightFieldShapeSettings.Create overloads resolve but the data
    // never reaches Jolt, and every other overload/setter is unexported. Passes while that holds; fails once it's
    // fixed upstream, so the skill gets updated. Also checks the static-compound fallback by dropping a ball on it.
    public static void JoltMeshHullTrap()
    {
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create();
        Jolt.RegisterTypes();
        try
        {
            var meshError = CreateShapeError(MeshFromQuad);
            var hullError = CreateShapeError(HullFromCube);
            Console.WriteLine($"     MeshShapeSettings.Create(triangles): {meshError ?? "works"}");
            Console.WriteLine($"     ConvexHullShapeSettings.Create(points): {hullError ?? "works"}");

            // Height field: 64x64 samples of height 0. Upstream Jolt only needs sampleCount >= blockSize (2).
            var heightError = CreateShapeError(FlatHeightField);
            Console.WriteLine($"     HeightFieldShapeSettings.Create(samples, ...): {heightError ?? "works"}");

            // Fallback that works: a static compound of boxes (top face at y=0).

            var compoundY = DropBallOnto(() =>
            {
                using var compound = StaticCompoundShapeSettings.Create();
                using var box = BoxShapeSettings.Create(new float3(2.5f, 0.5f, 5), 0.05f, default);
                compound.AddShape(new float3(-2.5f, -0.5f, 0), floatQuat.Identity, box);
                compound.AddShape(new float3(2.5f, -0.5f, 0), floatQuat.Identity, box);
                return compound.CreateShape();
            });
            Console.WriteLine($"     StaticCompoundShapeSettings (2 boxes): ball rests at y={compoundY:F2} (0.50 = collides)");

            Check(Math.Abs(compoundY - 0.5f) < 0.1f, $"ball rests on the compound fallback (y={compoundY})");
            if (meshError == null || hullError == null || heightError == null)
                throw new InvalidOperationException(
                    "T36 no longer reproduces: mesh, hull or height-field creation works. Fixed upstream? "
                        + "Update references/physics.md and this check."
                );
        }
        finally
        {
            Jolt.UnregisterTypes();
            Factory.DestroyInstance();
        }
    }

    static Shape MeshFromQuad()
    {
        ReadOnlySpan<Triangle> quad =
        [
            Triangle.Create(new float3(-5, 0, -5), new float3(-5, 0, 5), new float3(5, 0, 5)),
            Triangle.Create(new float3(-5, 0, -5), new float3(5, 0, 5), new float3(5, 0, -5)),
        ];
        using var settings = MeshShapeSettings.Create(quad, ReadOnlySpan<PhysicsMaterial>.Empty);
        return settings.CreateShape();
    }

    static Shape HullFromCube()
    {
        ReadOnlySpan<float3> cube =
        [
            new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1),
        ];
        using var settings = ConvexHullShapeSettings.Create(cube);
        return settings.CreateShape();
    }

    static Shape FlatHeightField()
    {
        const int n = 64;
        var samples = (float*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(n * n, sizeof(float));
        try
        {
            using var settings = HeightFieldShapeSettings.Create(
                samples, new float3(-31.5f, 0, -31.5f), new float3(1, 1, 1), n, default, ReadOnlySpan<PhysicsMaterial>.Empty);
            return settings.CreateShape();
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(samples);
        }
    }

    static string? CreateShapeError(Func<Shape> createShape)
    {
        try
        {
            createShape().Dispose();
            return null;
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }
    }

    // Drops a 0.5 m sphere from y=5 onto a static body with the given shape; returns where it ends up.
    static float DropBallOnto(Func<Shape> createShape)
    {
        using var groundShape = createShape();
        const ushort Static = 0, Moving = 1;
        var bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
        bp.MapObjectToBroadPhaseLayer(Static, 0);
        bp.MapObjectToBroadPhaseLayer(Moving, 1);
        var pairs = ObjectLayerPairFilterTable.Create(2);
        pairs.EnableCollision(Moving, Static);
        var objVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(bp, 2, pairs, 2);
        var temp = TempAllocatorImpl.Create(10 * 1024 * 1024);
        var jobs = JobSystemThreadPool.Create(2048, 8, Environment.ProcessorCount - 1);
        var physics = PhysicsSystem.Create();
        physics.Init(1024, 0, 1024, 1024, bp, objVsBp, pairs);
        physics.SetGravity(new float3(0, -9.81f, 0));
        var bodies = physics.GetBodyInterface();
        uint ground;
        using (var bcs = BodyCreationSettings.Create(groundShape, float3.Zero, floatQuat.Identity, MotionType.Static, Static))
            ground = bodies.CreateAndAddBody(bcs, Activation.DontActivate);
        uint ball;
        using (var ss = SphereShapeSettings.Create(0.5f))
        using (var shape = ss.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, new float3(0, 5, 0), floatQuat.Identity, MotionType.Dynamic, Moving))
            ball = bodies.CreateAndAddBody(bcs, Activation.Activate);
        physics.OptimizeBroadPhase();
        for (var i = 0; i < 120; i++)
            physics.Update(1 / 60f, 1, temp, jobs);
        var y = bodies.GetPosition(ball).Y;
        bodies.RemoveBody(ball);
        bodies.DestroyBody(ball);
        bodies.RemoveBody(ground);
        bodies.DestroyBody(ground);
        physics.Dispose();
        jobs.Dispose();
        temp.Dispose();
        objVsBp.Dispose();
        pairs.Dispose();
        bp.Dispose();
        return y;
    }

    public static void ShaderCRecipe()
    {
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
        options.SetOptimizationLevel(OptimizationLevel.Performance);
        var glsl = """
            #version 450
            layout(location = 0) in vec2 inPos;
            void main() { gl_Position = vec4(inPos, 0.0, 1.0); }
            """u8;
        using var result = compiler.Compile(glsl, ShaderKind.VertexShader, "tri.vert"u8, "main"u8, options);
        var spirv = new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray();
        Check(spirv.Length > 20 && BitConverter.ToUInt32(spirv, 0) == 0x07230203, "SPIR-V magic");
    }

    public static void GlfwWindow()
    {
        if (!Glfw.Init())
            throw new Exception("GLFW init failed");
        try
        {
            Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);
            var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "RecipeCheck", Size = new int2(320, 200) });
            for (var i = 0; i < 5 && !window.ShouldClose; i++)
                Glfw.PollEvents();
            window.Dispose();
            var version = Glfw.PInvoke.GetVersionString();
            Console.WriteLine($"     GLFW {version}; Vulkan supported: {Glfw.VulkanSupported()}");
        }
        finally
        {
            Glfw.Terminate();
        }
    }

    public static void GlfwNullPlatform()
    {
        Glfw.SetInitHint(GlfwInitHint.Platform, (int)GlfwPlatform.Null);
        if (!Glfw.Init())
            throw new Exception("GLFW init (Null platform) failed");
        try
        {
            Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
            var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "RecipeCheck", Size = new int2(320, 200) });
            Glfw.PollEvents();
            window.Dispose();
        }
        finally
        {
            Glfw.Terminate();
        }
    }

    public static void StbRoundTrip()
    {
        var missing = Stb.Load(Path.Combine(AppContext.BaseDirectory, "does-not-exist.png"), 4);
        Check(missing.Data == null, "T1: missing file returns Data == null without throwing");
        missing.Dispose();

        const int w = 4, h = 2;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 7);
        var path = Path.Combine(Path.GetTempPath(), $"recipecheck-{Environment.ProcessId}.png");
        try
        {
            Stb.WritePng(path, w, h, 4, rgba, w * 4);
            var png = File.ReadAllBytes(path);
            var image = Stb.LoadFromMemory(png, 4);
            try
            {
                Check(image.Data != null, "decode succeeded");
                Check(image.Width == w && image.Height == h, "dimensions survive");
                Check(image.AsSpan().SequenceEqual(rgba), "pixels survive");
            }
            finally
            {
                image.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static void VulkanInstance()
    {
        using var instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = (1 << 22) | (3 << 12) },
                EnabledExtensions = [],
                EnabledLayers = [],
            }
        );
        var count = 0;
        foreach (var pd in instance.PhysicalDevices)
        {
            var props = pd.GetProperties();
            var name = System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0');
            Console.WriteLine($"     GPU: {name} ({props.DeviceType})");
            count++;
        }
        Check(count > 0, "at least one physical device");
    }
}
