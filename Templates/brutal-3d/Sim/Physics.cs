using Brutal.JoltApi;
using Brutal.Numerics;

namespace BrutalGame3D.Sim;

/// <summary>
/// Process-wide Jolt state: allocator, factory, type registry, the temp allocator and job system every scene shares.
/// Initialise once; <see cref="Shutdown"/> once at exit after every <see cref="PhysicsScene"/> is disposed (T18).
/// </summary>
public static class JoltRuntime
{
    static bool _initialized;
    static TempAllocatorImpl _temp;
    static JobSystemThreadPool _jobs;

    public static TempAllocatorImpl Temp => _temp;
    public static JobSystemThreadPool Jobs => _jobs;

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create(); // the global owns it; never Dispose it (T17)
        Jolt.RegisterTypes();
        _temp = TempAllocatorImpl.Create(16 * 1024 * 1024);
        _jobs = JobSystemThreadPool.Create(2048, 8, Math.Clamp(Environment.ProcessorCount - 1, 1, 4));
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;
        _jobs.Dispose();
        _temp.Dispose();
        Jolt.UnregisterTypes();
        Factory.DestroyInstance();
        _initialized = false;
    }
}

/// <summary>
/// One Jolt physics system holding the static level and the ball. Owns its layer tables and bodies and frees them
/// in the order T18 requires. Only primitive shapes, created through XxxShapeSettings.
/// </summary>
public sealed class PhysicsScene : IDisposable
{
    const ushort StaticLayer = 0;
    const ushort MovingLayer = 1;

    public const float BallRadius = 0.5f;

    readonly BroadPhaseLayerInterfaceTable _bp;
    readonly ObjectLayerPairFilterTable _pairs;
    readonly ObjectVsBroadPhaseLayerFilterTable _objVsBp;
    readonly PhysicsSystem _physics;
    readonly BodyInterface _bodies; // borrowed from _physics: never dispose it (T17)
    readonly List<uint> _static = [];
    readonly uint _ball;
    bool _disposed;

    public PhysicsScene(IReadOnlyList<Block> blocks, float3 ballStart, float gravity)
    {
        JoltRuntime.EnsureInitialized();
        _bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
        _bp.MapObjectToBroadPhaseLayer(StaticLayer, 0);
        _bp.MapObjectToBroadPhaseLayer(MovingLayer, 1);
        _pairs = ObjectLayerPairFilterTable.Create(2);
        _pairs.EnableCollision(MovingLayer, StaticLayer);
        _pairs.EnableCollision(MovingLayer, MovingLayer);
        _objVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(_bp, 2, _pairs, 2);
        _physics = PhysicsSystem.Create();
        _physics.Init(256, 0, 1024, 1024, _bp, _objVsBp, _pairs);
        _physics.SetGravity(new float3(0, gravity, 0));
        _bodies = _physics.GetBodyInterface();

        foreach (var block in blocks)
        {
            using var ss = BoxShapeSettings.Create(block.HalfExtents, 0.05f, default);
            using var shape = ss.CreateShape(); // ref-counted: the body keeps its own reference
            using var bcs = BodyCreationSettings.Create(shape, block.Center, block.Rotation, MotionType.Static, StaticLayer);
            bcs.SetFriction(block.Kind == BlockKind.Wall ? 0.3f : 0.9f);
            bcs.SetRestitution(block.Kind == BlockKind.Wall ? 0.45f : 0.05f);
            _static.Add(_bodies.CreateAndAddBody(bcs, Activation.DontActivate));
        }

        using (var ss = SphereShapeSettings.Create(BallRadius))
        using (var shape = ss.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, ballStart, floatQuat.Identity, MotionType.Dynamic, MovingLayer))
        {
            bcs.SetFriction(1.0f);
            bcs.SetRestitution(0.3f);
            bcs.SetLinearDamping(0.25f);
            bcs.SetAngularDamping(0.6f);
            bcs.SetMotionQuality(MotionQuality.LinearCast); // no tunnelling through thin walls at speed
            bcs.SetAllowSleeping(false); // the player can push it at any time
            _ball = _bodies.CreateAndAddBody(bcs, Activation.Activate);
        }
        _physics.OptimizeBroadPhase();
    }

    public void Update(float dt)
    {
        var error = _physics.Update(dt, 1, JoltRuntime.Temp, JoltRuntime.Jobs);
        if (error != PhysicsUpdateError.None)
            throw new InvalidOperationException($"Jolt update failed: {error}");
    }

    public float3 BallPosition => _bodies.GetPosition(_ball);
    public floatQuat BallRotation => _bodies.GetRotation(_ball);
    public float3 BallVelocity => _bodies.GetLinearVelocity(_ball);
    public float3 BallAngularVelocity => _bodies.GetAngularVelocity(_ball);

    /// <summary>Adds a velocity change directly: mass-independent, so tuning doesn't depend on the ball's density.</summary>
    public void Push(float3 linear, float3 angular) => _bodies.AddLinearAndAngularVelocity(_ball, linear, angular);

    public void PlaceBall(float3 position) =>
        _bodies.SetPositionRotationAndVelocity(_ball, position, floatQuat.Identity, float3.Zero, float3.Zero);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // T18: bodies -> physics -> filter tables. The shared jobs/temp allocator belong to JoltRuntime.
        _bodies.RemoveBody(_ball);
        _bodies.DestroyBody(_ball);
        foreach (var id in _static)
        {
            _bodies.RemoveBody(id);
            _bodies.DestroyBody(id);
        }
        _physics.Dispose();
        _objVsBp.Dispose();
        _pairs.Dispose();
        _bp.Dispose();
    }
}
