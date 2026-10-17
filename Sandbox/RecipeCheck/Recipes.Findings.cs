// Recipes 7.26-7.28: facts found by the iteration-2 evals (3D roller, online Pong, Vulkan cube), checked here so they
// stay true: Numerics quaternion conventions, Jolt body tuning + determinism, RakNet failure paths. All headless.
using System.Diagnostics;
using System.Text;
using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.RakNetApi;

static unsafe partial class Recipes
{
    // ---- 7.26 Numerics quaternions -----------------------------------------------------------------------------

    public static void QuaternionRecipe()
    {
        var yaw = floatQuat.CreateFromAxisAngle(new float3(0, 1, 0), MathF.PI / 2); // +90 deg about Y
        var pitch = floatQuat.CreateFromAxisAngle(new float3(1, 0, 0), MathF.PI / 2); // +90 deg about X
        var v = new float3(0, 0, 1);

        static bool Near(float3 a, float3 b) => float3.Distance(a, b) < 1e-4f;
        var yawThenPitch = float3.Transform(float3.Transform(v, yaw), pitch);
        var pitchThenYaw = float3.Transform(float3.Transform(v, pitch), yaw);
        var product = float3.Transform(v, yaw * pitch);
        Console.WriteLine($"     Transform(v, yaw*pitch) = {product}; yaw-then-pitch {yawThenPitch}; pitch-then-yaw {pitchThenYaw}");
        // System.Numerics convention: a * b applies b first, then a. Concatenate(a, b) = b * a applies a first.
        Check(Near(product, pitchThenYaw) && !Near(product, yawThenPitch), "a * b rotates by b first, then a (Hamilton order)");
        Check(Near(float3.Transform(v, floatQuat.Concatenate(yaw, pitch)), yawThenPitch), "Concatenate(a, b) rotates by a first");
        Check(Near(float3.Transform(new float3(1, 0, 0), yaw), new float3(0, 0, -1)), "right-handed: +90 deg about Y takes +X to -Z");

        // CreateFromRotationMatrix needs a proper rotation (right-handed basis, det +1). A mirrored basis gives garbage.
        var good = float4x4.CreateFromQuaternion(yaw);
        Check(Near(float3.Transform(v, floatQuat.CreateFromRotationMatrix(good)), float3.Transform(v, yaw)), "matrix -> quaternion round trip");
        var mirrored = good with { M31 = -good.M31, M32 = -good.M32, M33 = -good.M33 }; // flip one axis: det -1
        var bad = floatQuat.CreateFromRotationMatrix(mirrored);
        Console.WriteLine($"     from a left-handed basis: {bad} (length {bad.Length():F3})");
        Check(!Near(float3.Transform(v, bad), float3.Transform(v, yaw)), "a left-handed basis does not give the intended rotation");
    }

    // ---- 7.27 Jolt body tuning and determinism -----------------------------------------------------------------

    public static void JoltTuningRecipe()
    {
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create();
        Jolt.RegisterTypes();
        var temp = TempAllocatorImpl.Create(10 * 1024 * 1024);
        var jobs = JobSystemThreadPool.Create(2048, 8, Math.Clamp(Environment.ProcessorCount - 1, 1, 4)); // multi-threaded
        try
        {
            // Same inputs twice on fresh physics systems: identical results, bit for bit.
            var (p1, q1, v1, rolled) = RollBall(temp, jobs);
            var (p2, q2, v2, _) = RollBall(temp, jobs);
            Console.WriteLine($"     ball after 2 s: pos {p1}, vel {v1}");
            Check(rolled, "AddLinearAndAngularVelocity + friction makes the ball roll (rotation changed, moved forward)");
            Check(p1.Equals(p2) && q1.Equals(q2) && v1.Equals(v2), "two identical runs give bit-identical results (multi-threaded job system)");
        }
        finally
        {
            jobs.Dispose(); // one Factory/registration and one job system served both physics systems
            temp.Dispose();
            Jolt.UnregisterTypes();
            Factory.DestroyInstance();
        }
    }

    static (float3 Pos, floatQuat Rot, float3 Vel, bool Rolled) RollBall(TempAllocatorImpl temp, JobSystemThreadPool jobs)
    {
        const ushort Static = 0, Moving = 1;
        var bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
        bp.MapObjectToBroadPhaseLayer(Static, 0);
        bp.MapObjectToBroadPhaseLayer(Moving, 1);
        var pairs = ObjectLayerPairFilterTable.Create(2);
        pairs.EnableCollision(Moving, Static);
        pairs.EnableCollision(Moving, Moving);
        var objVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(bp, 2, pairs, 2);
        var physics = PhysicsSystem.Create();
        physics.Init(1024, 0, 1024, 1024, bp, objVsBp, pairs);
        physics.SetGravity(new float3(0, -9.81f, 0));
        var bodies = physics.GetBodyInterface();
        uint floor, ball;
        using (var ss = BoxShapeSettings.Create(new float3(50, 1, 50), 0.05f, default))
        using (var shape = ss.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, new float3(0, -1, 0), floatQuat.Identity, MotionType.Static, Static))
        {
            bcs.SetFriction(0.9f);
            floor = bodies.CreateAndAddBody(bcs, Activation.DontActivate);
        }
        using (var ss = SphereShapeSettings.Create(0.5f))
        using (var shape = ss.CreateShape())
        using (var bcs = BodyCreationSettings.Create(shape, new float3(0, 0.5f, 0), floatQuat.Identity, MotionType.Dynamic, Moving))
        {
            bcs.SetFriction(1.0f);
            bcs.SetRestitution(0.3f);
            bcs.SetLinearDamping(0.25f);
            bcs.SetAngularDamping(0.6f);
            bcs.SetMotionQuality(MotionQuality.LinearCast); // continuous collision: no tunnelling at speed
            bcs.SetAllowSleeping(false);
            ball = bodies.CreateAndAddBody(bcs, Activation.Activate);
        }
        physics.OptimizeBroadPhase();
        for (var i = 0; i < 120; i++)
        {
            if (i < 60) // push forward (+Z) and spin about +X so it rolls instead of sliding
                bodies.AddLinearAndAngularVelocity(ball, new float3(0, 0, 0.1f), new float3(0.2f, 0, 0));
            if (physics.Update(1 / 60f, 1, temp, jobs) != PhysicsUpdateError.None)
                throw new Exception("Jolt update error");
        }
        var pos = bodies.GetPosition(ball);
        var rot = bodies.GetRotation(ball);
        var vel = bodies.GetLinearVelocity(ball);
        var rolled = pos.Z > 1 && !rot.Equals(floatQuat.Identity);
        bodies.SetPositionRotationAndVelocity(ball, new float3(0, 0.5f, 0), floatQuat.Identity, float3.Zero, float3.Zero); // a reset
        Check(bodies.GetPosition(ball).Equals(new float3(0, 0.5f, 0)), "SetPositionRotationAndVelocity resets the ball");
        bodies.RemoveBody(ball);
        bodies.DestroyBody(ball);
        bodies.RemoveBody(floor);
        bodies.DestroyBody(floor);
        physics.Dispose();
        objVsBp.Dispose();
        pairs.Dispose();
        bp.Dispose();
        return (pos, rot, vel, rolled);
    }

    // ---- 7.28 RakNet failure paths -------------------------------------------------------------------------------

    public static void RakNetFailuresRecipe()
    {
        var full = (byte)DefaultMessageIDTypes.NoFreeIncomingConnections;
        var failed = (byte)DefaultMessageIDTypes.ConnectionAttemptFailed;
        var accepted = (byte)DefaultMessageIDTypes.ConnectionRequestAccepted;
        var bye = (byte)DefaultMessageIDTypes.DisconnectionNotification;
        var lost = (byte)DefaultMessageIDTypes.ConnectionLost;

        using var server = RakNetLibrary.CreateInstance();
        ushort port = 0;
        for (ushort p = 61250; p < 61260 && port == 0; p++)
        {
            // Bind to 127.0.0.1 only: no Windows Firewall prompt in tests. Players need the default (all interfaces).
            Span<SocketDescriptor> sd = [new SocketDescriptor("127.0.0.1", p)];
            if (server.Startup(1, sd) == StartupResult.RaknetStarted)
                port = p;
        }
        Check(port != 0, "server bound to 127.0.0.1");
        server.SetMaximumIncomingConnections(1); // room for one opponent
        // Timeout = how long an undeliverable *reliable* message may wait before ConnectionLost. Set it right after
        // Startup, to the same value on both ends (RakNet's rule). Default 10 s (30 s in debug builds of RakNet).
        server.SetTimeoutTime(1500, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);

        byte[] Host(string typed) => Encoding.UTF8.GetBytes(typed + "\0"); // a typed address: add the NUL yourself

        byte Wait(RakPeerInstance peer, RakPeerInstance other, int ms, params byte[] wanted)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                for (var p = other.Receive(); p != null; other.DeallocatePacket(p), p = other.Receive()) { } // keep the other side pumping
                for (var p = peer.Receive(); p != null; peer.DeallocatePacket(p), p = peer.Receive())
                    if (wanted.Contains(p->Data[0]))
                    {
                        var id = p->Data[0];
                        peer.DeallocatePacket(p);
                        return id;
                    }
                Thread.Sleep(5);
            }
            return 0;
        }

        using var a = RakNetLibrary.CreateInstance();
        Span<SocketDescriptor> any = [new SocketDescriptor()];
        a.Startup(1, any);
        a.Connect(Host("127.0.0.1"), port, default, null);
        Check(Wait(a, server, 3000, accepted) == accepted, "first client accepted");

        using var b = RakNetLibrary.CreateInstance();
        b.Startup(1, any);
        b.Connect(Host("127.0.0.1"), port, default, null);
        Check(Wait(b, server, 3000, full, accepted) == full, "a second client gets NoFreeIncomingConnections");

        // Nobody listening: ConnectionAttemptFailed after the retries (default 12 x 500 ms, so about 6 s).
        using var c = RakNetLibrary.CreateInstance();
        c.Startup(1, any);
        var sw = Stopwatch.StartNew();
        c.Connect(Host("127.0.0.1"), (ushort)(port + 100), default, null, 0, 4, 250); // 4 tries x 250 ms
        Check(Wait(c, server, 5000, failed) == failed, $"ConnectionAttemptFailed with nobody listening ({sw.ElapsedMilliseconds} ms with 4 x 250 ms tries)");

        // Polite leave: CloseConnection(..., true) / Shutdown(ms > 0) sends DisconnectionNotification.
        var serverSawBye = false;
        a.Shutdown(300);
        serverSawBye = Wait(server, b, 2000, bye, lost) == bye;
        Check(serverSawBye, "server gets DisconnectionNotification when a client shuts down politely");

        // Crash: Shutdown(0) sends nothing, so the other side sees ConnectionLost after its timeout.
        using var d = RakNetLibrary.CreateInstance();
        d.Startup(1, any);
        d.SetTimeoutTime(1500, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);
        d.Connect(Host("127.0.0.1"), port, default, null);
        Check(Wait(d, server, 3000, accepted) == accepted, "a new client fits after the first left");
        var dGuid = d.GetMyGUID();
        Thread.Sleep(300);
        var ping = server.GetAveragePing(AddressOrGUID.From(dGuid));
        Console.WriteLine($"     GetAveragePing to the client: {ping} ms");
        d.Shutdown(0); // like a crashed process: no goodbye
        sw.Restart();
        // A game keeps sending reliable state; only an undeliverable reliable message starts the timeout.
        byte crash = 0;
        Span<byte> msg = [(byte)DefaultMessageIDTypes.UserPacketEnum, 1, 2, 3];
        while (crash == 0 && sw.ElapsedMilliseconds < 6000)
        {
            server.Send(msg, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, AddressOrGUID.From(dGuid), false);
            crash = Wait(server, b, 50, lost, bye);
        }
        Console.WriteLine($"     after Shutdown(0): {(crash == lost ? "ConnectionLost" : crash == bye ? "DisconnectionNotification" : "nothing")} after {sw.ElapsedMilliseconds} ms (timeout 1500 ms)");
        Check(crash == lost, "server gets ConnectionLost (not DisconnectionNotification) when a client vanishes");

        b.Shutdown(0);
        c.Shutdown(0);
        server.Shutdown(0);
    }
}
