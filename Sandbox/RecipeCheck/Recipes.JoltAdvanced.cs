// Recipe 7.34: Jolt constraints (hinge, point, distance, fixed, slider, cone, swing-twist), compound shapes, and both
// character controllers (CharacterVirtual and the body-based Character), all headless. Also checks that the gaps the
// skill documents (T46) are still gaps: when upstream exports them, a check here fails so the skill gets updated.
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using Brutal.Numerics;

static unsafe partial class Recipes
{
    const ushort JStatic = 0, JMoving = 1;

    sealed class JoltScene : IDisposable
    {
        public BroadPhaseLayerInterfaceTable Bp;
        public ObjectLayerPairFilterTable Pairs;
        public ObjectVsBroadPhaseLayerFilterTable ObjVsBp;
        public PhysicsSystem Physics;
        public BodyInterface Bodies;
        public TempAllocatorImpl Temp;
        public JobSystemThreadPool Jobs;
        public readonly List<uint> Ids = [];

        public JoltScene(float gravity = -9.81f)
        {
            Bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
            Bp.MapObjectToBroadPhaseLayer(JStatic, 0);
            Bp.MapObjectToBroadPhaseLayer(JMoving, 1);
            Pairs = ObjectLayerPairFilterTable.Create(2);
            Pairs.EnableCollision(JMoving, JStatic);
            Pairs.EnableCollision(JMoving, JMoving);
            ObjVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(Bp, 2, Pairs, 2);
            Temp = TempAllocatorImpl.Create(10 * 1024 * 1024);
            Jobs = JobSystemThreadPool.Create(2048, 8, 1);
            Physics = PhysicsSystem.Create();
            Physics.Init(1024, 0, 1024, 1024, Bp, ObjVsBp, Pairs);
            Physics.SetGravity(new float3(0, gravity, 0));
            Bodies = Physics.GetBodyInterface();
        }

        public uint Add(Shape shape, float3 pos, MotionType type)
        {
            using var bcs = BodyCreationSettings.Create(shape, pos, floatQuat.Identity, type, type == MotionType.Static ? JStatic : JMoving);
            var id = Bodies.CreateAndAddBody(bcs, type == MotionType.Static ? Activation.DontActivate : Activation.Activate);
            Ids.Add(id);
            return id;
        }

        public uint AddBox(float3 half, float3 pos, MotionType type)
        {
            using var s = BoxShapeSettings.Create(half, 0.05f, default);
            using var shape = s.CreateShape();
            return Add(shape, pos, type);
        }

        public uint AddSphere(float r, float3 pos, MotionType type)
        {
            using var s = SphereShapeSettings.Create(r);
            using var shape = s.CreateShape();
            return Add(shape, pos, type);
        }

        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
                if (Physics.Update(1 / 60f, 1, Temp, Jobs) != PhysicsUpdateError.None)
                    throw new Exception("Jolt update error");
        }

        public void Dispose()
        {
            foreach (var id in Ids)
            {
                Bodies.RemoveBody(id);
                Bodies.DestroyBody(id);
            }
            Physics.Dispose();
            Jobs.Dispose();
            Temp.Dispose();
            ObjVsBp.Dispose();
            Pairs.Dispose();
            Bp.Dispose();
        }
    }

    /// <summary>The constraint casts (<c>Constraint.AsHingeConstraint</c> and friends) aren't exported (T46). The handles share one layout, which is how the library itself down-casts, so this reaches the hinge-only calls.</summary>
    static HingeConstraint AsHinge(TwoBodyConstraint c) => Unsafe.BitCast<TwoBodyConstraint, HingeConstraint>(c);

    static HingeConstraintSettings WorldHinge(float3 pivot)
    {
        var hs = HingeConstraintSettings.Create();
        hs.SetSpace(ConstraintSpace.WorldSpace);
        hs.SetPoint1(pivot);
        hs.SetPoint2(pivot);
        hs.SetHingeAxis1(new float3(0, 0, 1));
        hs.SetHingeAxis2(new float3(0, 0, 1));
        hs.SetNormalAxis1(new float3(1, 0, 0));
        hs.SetNormalAxis2(new float3(1, 0, 0));
        return hs;
    }

    /// <summary>True when <paramref name="call"/> still throws EntryPointNotFoundException (an export gap).</summary>
    static bool StillMissing(string what, Action call)
    {
        try
        {
            call();
            Console.WriteLine($"     GAP CLOSED: {what} now works. Update T46 in jolt.md and this recipe.");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    sealed class NoOpCharacterListener : ManagedCharacterContactListener { }

    public static void JoltAdvancedRecipe()
    {
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create();
        Jolt.RegisterTypes();
        try
        {
            var pivot = new float3(0, 10, 0);

            // ---- constraints: create from settings with body IDs, AddConstraint, RemoveConstraint, Dispose ----
            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var bob = w.AddBox(new float3(0.2f), new float3(2, 10, 0), MotionType.Dynamic);
                using var hs = WorldHinge(pivot);
                var c = w.Bodies.CreateConstraint(hs, anchor, bob); // the handle carries its own reference
                w.Physics.AddConstraint(c);
                w.Run(60);
                var p = w.Bodies.GetPosition(bob);
                Check(Math.Abs(float3.Distance(p, pivot) - 2) < 0.05f && p.Y < 9.5f && Math.Abs(p.Z) < 0.01f, $"hinge: the box swings on a 2 m arm in one plane (at {p})");
                Check(Math.Abs(AsHinge(c).GetCurrentAngle()) > 1, $"hinge: GetCurrentAngle reads the swing ({AsHinge(c).GetCurrentAngle():F2} rad)");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), new float3(0, 12, 0), MotionType.Static); // away from the wheel: overlapping bodies fight the motor
                var wheel = w.AddBox(new float3(0.5f), pivot, MotionType.Dynamic);
                using var hs = WorldHinge(pivot);
                var c = w.Bodies.CreateConstraint(hs, anchor, wheel);
                w.Physics.AddConstraint(c);
                var hinge = AsHinge(c);
                hinge.SetMotorState(MotorState.Velocity);
                hinge.SetTargetAngularVelocity(2f);
                w.Run(60);
                var av = w.Bodies.GetAngularVelocity(wheel);
                Check(Math.Abs(hinge.GetCurrentAngle() - 2f) < 0.1f && Math.Abs(av.Z - 2f) < 0.05f, $"hinge motor: SetMotorState(Velocity) + SetTargetAngularVelocity(2) turns the wheel 2 rad in 1 s (angle {hinge.GetCurrentAngle():F2}, angular velocity {av})");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            // ---- hinge limits: from the settings, read back, and changed at run time against a motor ----
            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var bob = w.AddBox(new float3(0.2f), new float3(2, 10, 0), MotionType.Dynamic); // falls toward a negative angle about +Z
                using var hs = WorldHinge(pivot);
                hs.SetLimitsMin(-0.5f);
                hs.SetLimitsMax(0.5f);
                var c = w.Bodies.CreateConstraint(hs, anchor, bob);
                w.Physics.AddConstraint(c);
                var hinge = AsHinge(c);
                Check(hinge.HasLimits() && hinge.GetLimitsMin() == -0.5f && hinge.GetLimitsMax() == 0.5f, $"hinge limits: settings limits read back on the constraint (HasLimits {hinge.HasLimits()}, {hinge.GetLimitsMin()}..{hinge.GetLimitsMax()})");
                var lowest = 0f;
                for (var i = 0; i < 120; i++)
                {
                    w.Run(1);
                    lowest = Math.Min(lowest, hinge.GetCurrentAngle());
                }
                var p = w.Bodies.GetPosition(bob);
                var rest = hinge.GetCurrentAngle();
                Console.WriteLine($"     hinge limit [-0.5, 0.5]: lowest angle {lowest:F3} rad, at rest {rest:F3}, bob at {p}");
                Check(lowest > -0.6f && Math.Abs(rest + 0.5f) < 0.03f && p.Y > 8.9f, "hinge limits: SetLimitsMin/Max stop a falling arm at the limit (a few degrees of overshoot and sag)");

                // A motor set on a sleeping body does nothing until the body is woken.
                var steps = 0;
                while (w.Bodies.IsActive(bob) && steps < 600) { w.Run(1); steps++; }
                Check(!w.Bodies.IsActive(bob), "the arm falls asleep at rest on its limit");
                var asleep = hinge.GetCurrentAngle();
                hinge.SetMotorState(MotorState.Velocity);
                hinge.SetTargetAngularVelocity(2f);
                w.Run(30);
                Console.WriteLine($"     asleep after {steps} more steps; motor on, 30 steps: angle {asleep:F3} -> {hinge.GetCurrentAngle():F3}, active {w.Bodies.IsActive(bob)}");
                Check(Math.Abs(hinge.GetCurrentAngle() - asleep) < 1e-4f, "hinge motor: SetMotorState doesn't wake a sleeping body");
                w.Bodies.ActivateBody(bob);
                w.Run(30);
                Check(hinge.GetCurrentAngle() > asleep + 0.3f, $"hinge motor: ActivateBody starts it (angle {hinge.GetCurrentAngle():F3})");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            // Limits changed at run time hold against a motor when the body sits on the hinge axis.
            using (var w = new JoltScene(0f))
            {
                var anchor = w.AddBox(new float3(0.2f), new float3(0, 12, 0), MotionType.Static);
                var wheel = w.AddBox(new float3(0.5f), pivot, MotionType.Dynamic);
                using var hs = WorldHinge(pivot);
                var c = w.Bodies.CreateConstraint(hs, anchor, wheel);
                w.Physics.AddConstraint(c);
                var hinge = AsHinge(c);
                Check(!hinge.HasLimits(), "hinge limits: none by default");
                hinge.SetLimits(-0.3f, 0.8f);
                hinge.SetMotorState(MotorState.Velocity);
                hinge.SetTargetAngularVelocity(2f);
                w.Run(90);
                Console.WriteLine($"     wheel, SetLimits(-0.3, 0.8) + motor 2 rad/s: angle {hinge.GetCurrentAngle():F3} rad");
                Check(Math.Abs(hinge.GetCurrentAngle() - 0.8f) < 0.01f, "hinge limits: SetLimits at run time; a velocity motor stops at the upper limit");
                hinge.SetLimits(-MathF.PI, MathF.PI);
                Check(!hinge.HasLimits(), "hinge limits: SetLimits(-pi, pi) turns limits off (HasLimits false)");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            // T51: the motor's default torque limit is float.MaxValue. With the body on a 2 m arm it drives straight
            // through the limit (inverted check); capping the torque through the constraint's own GetMotorSettings() fixes it.
            foreach (var cap in new[] { 0f, 1000f })
            using (var w = new JoltScene(0f))
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var bob = w.AddBox(new float3(0.2f), new float3(2, 10, 0), MotionType.Dynamic);
                using var hs = WorldHinge(pivot);
                var c = w.Bodies.CreateConstraint(hs, anchor, bob);
                w.Physics.AddConstraint(c);
                var hinge = AsHinge(c);
                hinge.SetLimits(-0.3f, 0.8f);
                var motor = hinge.GetMotorSettings(); // a live view of the constraint's motor, not a copy
                if (cap == 0)
                    Check(motor.GetMaxTorqueLimit() == float.MaxValue, $"T51: default motor torque limit is float.MaxValue ({motor.GetMaxTorqueLimit()})");
                else
                    motor.SetTorqueLimit(cap);
                hinge.SetMotorState(MotorState.Velocity);
                hinge.SetTargetAngularVelocity(2f);
                var highest = 0f;
                for (var i = 0; i < 120; i++)
                {
                    w.Run(1);
                    highest = Math.Max(highest, hinge.GetCurrentAngle());
                }
                Console.WriteLine($"     arm, limit 0.8, motor 2 rad/s, torque cap {(cap == 0 ? "none" : cap)}: highest angle {highest:F2} rad");
                if (cap == 0)
                    Check(highest > 1.5f, "T51 still present: an uncapped hinge motor drives an arm through its limit");
                else
                    Check(hinge.GetMotorSettings().GetMaxTorqueLimit() == cap && highest < 0.9f, "T51 fix: GetMotorSettings().SetTorqueLimit on the constraint keeps the arm at its limit");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var bob = w.AddSphere(0.3f, new float3(2, 10, 0), MotionType.Dynamic);
                using var ps = PointConstraintSettings.Create();
                ps.SetSpace(ConstraintSpace.WorldSpace);
                ps.SetPoint1(pivot);
                ps.SetPoint2(pivot);
                var c = w.Bodies.CreateConstraint(ps, anchor, bob);
                w.Physics.AddConstraint(c);
                w.Run(60);
                Check(Math.Abs(float3.Distance(w.Bodies.GetPosition(bob), pivot) - 2) < 0.05f, "point (ball joint): the sphere stays 2 m from the pivot");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var ball = w.AddSphere(0.3f, new float3(0, 9, 0), MotionType.Dynamic);
                using var ds = DistanceConstraintSettings.Create();
                ds.SetSpace(ConstraintSpace.WorldSpace);
                ds.SetPoint1(pivot);
                ds.SetPoint2(new float3(0, 9, 0));
                ds.SetMinDistance(0);
                ds.SetMaxDistance(3);
                var c = w.Bodies.CreateConstraint(ds, anchor, ball);
                w.Physics.AddConstraint(c);
                w.Run(120);
                var p = w.Bodies.GetPosition(ball);
                Check(Math.Abs(float3.Distance(p, pivot) - 3) < 0.05f, $"distance (rope): the ball falls until the 3 m rope is taut (at {p})");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            using (var w = new JoltScene())
            {
                var a = w.AddBox(new float3(0.5f), pivot, MotionType.Dynamic);
                var b = w.AddBox(new float3(0.5f), new float3(1.2f, 10, 0), MotionType.Dynamic);
                using var fs = FixedConstraintSettings.Create();
                fs.SetAutoDetectPoint(true);
                var c = w.Bodies.CreateConstraint(fs, a, b);
                w.Physics.AddConstraint(c);
                w.Run(60);
                var d = w.Bodies.GetPosition(b) - w.Bodies.GetPosition(a);
                Check(float3.Distance(d, new float3(1.2f, 0, 0)) < 0.02f && w.Bodies.GetPosition(a).Y < 6, $"fixed: two boxes fall as one, offset stays {d}");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var car = w.AddBox(new float3(0.3f), pivot, MotionType.Dynamic);
                using var ss = SliderConstraintSettings.Create();
                ss.SetSpace(ConstraintSpace.WorldSpace);
                ss.SetAutoDetectPoint(true);
                ss.SetSliderAxis(new float3(1, 0, 0));
                ss.SetLimitsMin(-1f);
                ss.SetLimitsMax(1.5f);
                var c = w.Bodies.CreateConstraint(ss, anchor, car);
                w.Physics.AddConstraint(c);
                w.Bodies.SetLinearVelocity(car, new float3(6, 0, 0));
                w.Run(90);
                var p = w.Bodies.GetPosition(car);
                Check(Math.Abs(p.X - 1.5f) < 0.05f && Math.Abs(p.Y - 10) < 0.02f, $"slider: moves along X, stops at the 1.5 limit, gravity can't pull it off the axis (at {p})");
                w.Physics.RemoveConstraint(c);
                c.Dispose();
            }

            foreach (var swingType in new[] { "cone", "swing-twist" }) // the limits ragdoll joints use
                using (var w = new JoltScene())
                {
                    var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                    var bob = w.AddSphere(0.3f, new float3(0, 8, 0), MotionType.Dynamic); // hangs 2 m below the pivot
                    TwoBodyConstraint c;
                    if (swingType == "cone")
                    {
                        using var cs = ConeConstraintSettings.Create();
                        cs.SetSpace(ConstraintSpace.WorldSpace);
                        cs.SetPoint1(pivot);
                        cs.SetPoint2(pivot);
                        cs.SetTwistAxis1(new float3(0, -1, 0));
                        cs.SetTwistAxis2(new float3(0, -1, 0));
                        cs.SetHalfConeAngle(MathF.PI / 6);
                        c = w.Bodies.CreateConstraint(cs, anchor, bob);
                    }
                    else
                    {
                        using var ss = SwingTwistConstraintSettings.Create();
                        ss.SetSpace(ConstraintSpace.WorldSpace);
                        ss.SetPosition1(pivot);
                        ss.SetPosition2(pivot);
                        ss.SetTwistAxis1(new float3(0, -1, 0));
                        ss.SetTwistAxis2(new float3(0, -1, 0));
                        ss.SetPlaneAxis1(new float3(1, 0, 0));
                        ss.SetPlaneAxis2(new float3(1, 0, 0));
                        ss.SetNormalHalfConeAngle(MathF.PI / 6);
                        ss.SetPlaneHalfConeAngle(MathF.PI / 6);
                        ss.SetTwistMinAngle(-0.5f);
                        ss.SetTwistMaxAngle(0.5f);
                        c = w.Bodies.CreateConstraint(ss, anchor, bob);
                    }
                    w.Physics.AddConstraint(c);
                    w.Bodies.SetLinearVelocity(bob, new float3(3, 0, 0)); // a free pendulum would swing to about 40 degrees
                    var maxAngle = 0f;
                    for (var i = 0; i < 90; i++)
                    {
                        w.Run(1);
                        var d = w.Bodies.GetPosition(bob) - pivot;
                        maxAngle = Math.Max(maxAngle, MathF.Atan2(MathF.Sqrt(d.X * d.X + d.Z * d.Z), -d.Y) * 180 / MathF.PI);
                    }
                    Check(maxAngle is > 25 and < 36, $"{swingType}: a 30 degree limit holds the swing to {maxAngle:F1} degrees (a few degrees of soft slack)");
                    w.Physics.RemoveConstraint(c);
                    c.Dispose();
                }

            // ---- constraint lifetime: references, and removing a constraint does not wake sleeping bodies ----
            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var ball = w.AddSphere(0.3f, new float3(0, 9, 0), MotionType.Dynamic);
                using var ds = DistanceConstraintSettings.Create();
                ds.SetSpace(ConstraintSpace.WorldSpace);
                ds.SetPoint1(pivot);
                ds.SetPoint2(new float3(0, 9, 0));
                ds.SetMaxDistance(1);
                var c = w.Bodies.CreateConstraint(ds, anchor, ball);
                var created = c.GetRefCount();
                w.Physics.AddConstraint(c);
                var added = c.GetRefCount();
                w.Run(20);
                var awake = w.Bodies.IsActive(ball);
                w.Run(100); // hanging still on the rope: the body falls asleep (Jolt's default)
                var asleep = !w.Bodies.IsActive(ball);
                w.Physics.RemoveConstraint(c);
                var removed = c.GetRefCount();
                w.Run(60);
                var yAfterRemoveAsleep = w.Bodies.GetPosition(ball).Y;
                w.Bodies.ActivateBody(ball);
                w.Run(30);
                var yAfterActivate = w.Bodies.GetPosition(ball).Y;
                Console.WriteLine($"     reference counts: created {created}, added {added}, removed {removed}; asleep={asleep}");
                Check(created == 1 && added == 2 && removed == 1, $"CreateConstraint hands you one reference, AddConstraint adds one, RemoveConstraint drops it ({created}, {added}, {removed}): Dispose yours once");
                Check(awake && asleep, "a body hanging still on a constraint falls asleep after about a second");
                Check(yAfterRemoveAsleep > 8.99f, $"RemoveConstraint does not wake a sleeping body: it hangs on (y {yAfterRemoveAsleep:F2})");
                Check(yAfterActivate < 8.9f, $"ActivateBody after RemoveConstraint lets it fall (y {yAfterActivate:F2})");
                c.Dispose();
            }

            // ---- compound shapes: a dumbbell of two spheres lies flat on the floor ----
            foreach (var mutable in new[] { false, true })
                using (var w = new JoltScene())
                {
                    w.AddBox(new float3(20, 1, 20), new float3(0, -1, 0), MotionType.Static);
                    using var sph = SphereShapeSettings.Create(0.5f);
                    Shape shape;
                    if (mutable)
                    {
                        using var comp = MutableCompoundShapeSettings.Create();
                        comp.AddShape(new float3(-1, 0, 0), floatQuat.Identity, sph);
                        comp.AddShape(new float3(1, 0, 0), floatQuat.Identity, sph);
                        shape = comp.CreateShape();
                    }
                    else
                    {
                        using var comp = StaticCompoundShapeSettings.Create();
                        comp.AddShape(new float3(-1, 0, 0), floatQuat.Identity, sph);
                        comp.AddShape(new float3(1, 0, 0), floatQuat.Identity, sph);
                        shape = comp.CreateShape();
                    }
                    using (shape)
                    {
                        var id = w.Add(shape, new float3(0, 3, 0), MotionType.Dynamic);
                        w.Run(240);
                        var p = w.Bodies.GetPosition(id);
                        Check(Math.Abs(p.Y - 0.5f) < 0.05f && Math.Abs(p.X) < 0.05f, $"{(mutable ? "mutable" : "static")} compound: both spheres rest on the floor (body at {p})");
                    }
                }

            // ---- CharacterVirtual: a capsule you move yourself; the world pushes back only through collision ----
            using (var w = new JoltScene())
            {
                w.AddBox(new float3(30, 1, 30), new float3(0, -1, 0), MotionType.Static); // floor, top at y = 0
                w.AddBox(new float3(0.5f, 2, 5), new float3(5.5f, 2, 0), MotionType.Static); // wall, face at x = 5
                w.AddBox(new float3(0.5f, 0.15f, 5), new float3(2.5f, 0.15f, 0), MotionType.Static); // step 1 m long, top at y = 0.3
                w.Physics.OptimizeBroadPhase();
                using var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.3f); // half height of the cylinder part, radius
                using var shape = capsuleSettings.CreateShape();
                using var cs = CharacterVirtualSettings.Create();
                cs.SetShape(shape);
                cs.SetUp(new float3(0, 1, 0));
                cs.SetMaxSlopeAngle(MathF.PI * 50 / 180);
                var ch = CharacterVirtual.Create(cs, new float3(0, 2, 0), floatQuat.Identity, w.Physics);
                var bpf = BroadPhaseLayerFilter.Create(); // allow-all filters (the default-filter getters are unexported)
                var olf = ObjectLayerFilter.Create();
                var bf = BodyFilter.Create();
                var sf = ShapeFilter.Create();
                var gravity = new float3(0, -9.81f, 0);
                var stairs = new CharacterVirtual.ExtendedUpdateSettings
                {
                    StickToFloorStepDown = new float3(0, -0.5f, 0),
                    WalkStairsStepUp = new float3(0, 0.4f, 0),
                    WalkStairsMinStepForward = 0.02f,
                    WalkStairsStepForwardTest = 0.15f,
                    WalkStairsCosAngleForwardContact = MathF.Cos(MathF.PI * 75 / 180),
                    WalkStairsStepDownExtra = float3.Zero,
                };
                // You own the velocity: gravity while airborne, 0 on the ground, a jump speed on demand.
                void Tick(float vx, bool jump, bool extended)
                {
                    var ground = ch.GetGroundState() == CharacterBase.GroundState.OnGround;
                    var vy = ground ? (jump ? 6f : 0f) : ch.GetLinearVelocity().Y + gravity.Y / 60f;
                    ch.SetLinearVelocity(new float3(vx, vy, 0));
                    if (extended)
                        ch.ExtendedUpdate(1 / 60f, gravity, in stairs, bpf, olf, bf, sf, w.Temp); // + walk stairs + stick to floor
                    else
                        ch.Update(1 / 60f, gravity, bpf, olf, bf, sf, w.Temp);
                }
                for (var i = 0; i < 120; i++) Tick(0, false, false);
                Check(ch.GetGroundState() == CharacterBase.GroundState.OnGround && Math.Abs(ch.GetPosition().Y - 0.8f) < 0.02f, $"CharacterVirtual: falls and stands on the floor (at {ch.GetPosition()}, {ch.GetGroundState()})");

                ch.SetPosition(new float3(-10, 0.8f, 0));
                for (var i = 0; i < 60; i++) Tick(3, false, false);
                Check(Math.Abs(ch.GetPosition().X + 7) < 0.05f, $"CharacterVirtual: SetLinearVelocity(3, ...) walks 3 m in 1 s (x {ch.GetPosition().X:F2})");

                ch.SetPosition(new float3(0, 0.8f, 0));
                for (var i = 0; i < 120; i++) Tick(3, false, false);
                Check(Math.Abs(ch.GetPosition().X - 1.7f) < 0.05f && ch.GetPosition().Y < 0.85f, $"CharacterVirtual: Update alone is stopped by a 0.3 m step (at {ch.GetPosition()})");

                ch.SetPosition(new float3(0, 0.8f, 0));
                var peak = 0f;
                for (var i = 0; i < 120; i++) { Tick(3, false, true); peak = Math.Max(peak, ch.GetPosition().Y); }
                Check(Math.Abs(peak - 1.1f) < 0.05f && ch.GetPosition().X > 4, $"CharacterVirtual: ExtendedUpdate walks up the step (top at y {peak:F2}) and on");

                ch.SetPosition(new float3(3.5f, 0.8f, 0));
                for (var i = 0; i < 240; i++) Tick(3, false, true);
                Check(Math.Abs(ch.GetPosition().X - 4.68f) < 0.05f, $"CharacterVirtual: a wall stops it 0.02 m (the padding) short of the capsule radius (x {ch.GetPosition().X:F2})");

                ch.SetPosition(new float3(-10, 0.8f, 0));
                for (var i = 0; i < 30; i++) Tick(0, false, false);
                Tick(0, true, false);
                var jumpPeak = 0f;
                for (var i = 0; i < 120; i++) { Tick(0, false, false); jumpPeak = Math.Max(jumpPeak, ch.GetPosition().Y); }
                Check(jumpPeak is > 2.5f and < 2.8f && ch.GetGroundState() == CharacterBase.GroundState.OnGround, $"CharacterVirtual: a jump at 6 m/s peaks at y {jumpPeak:F2} (0.8 + v^2/2g = 2.63) and lands ({ch.GetGroundState()})");

                // Contact callbacks need a managed listener whose native proxy isn't exported (T46).
                Check(StillMissing("a ManagedCharacterContactListener", () => { using var l = new NoOpCharacterListener(); }), "gap: CharacterContactListener can't be created (no push-back or contact events)");
                bpf.Dispose();
                olf.Dispose();
                bf.Dispose();
                sf.Dispose();
                ch.Dispose();
            }

            // ---- Character: a dynamic body with character settings (collides like a body, you set its velocity) ----
            using (var w = new JoltScene())
            {
                w.AddBox(new float3(30, 1, 30), new float3(0, -1, 0), MotionType.Static);
                using var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.3f);
                using var shape = capsuleSettings.CreateShape();
                using var cs = CharacterSettings.Create();
                cs.SetShape(shape);
                cs.SetLayer(JMoving);
                cs.SetUp(new float3(0, 1, 0));
                cs.SetMass(70);
                cs.SetFriction(0.5f);
                var ch = Character.Create(cs, new float3(0, 3, 0), floatQuat.Identity, 0, w.Physics);
                ch.AddToPhysicsSystem(Activation.Activate);
                w.Physics.OptimizeBroadPhase();
                for (var i = 0; i < 120; i++)
                {
                    w.Run(1);
                    ch.PostSimulation(0.05f); // after every physics update: refreshes the ground state
                }
                Check(ch.GetGroundState() == CharacterBase.GroundState.OnGround && Math.Abs(ch.GetPosition().Y - 0.8f) < 0.05f, $"Character: falls and stands (at {ch.GetPosition()}, {ch.GetGroundState()})");
                ch.SetLinearVelocity(new float3(2, 0, 0));
                w.Run(60);
                ch.PostSimulation(0.05f);
                Check(ch.GetPosition().X > 0.3f, $"Character: SetLinearVelocity moves it (x {ch.GetPosition().X:F2}; friction and damping slow it)");
                ch.RemoveFromPhysicsSystem();
                ch.Dispose();
            }

            // ---- the documented gaps (T46) must still be gaps ----
            using (var w = new JoltScene())
            {
                var anchor = w.AddBox(new float3(0.2f), pivot, MotionType.Static);
                var bob = w.AddBox(new float3(0.2f), new float3(2, 10, 0), MotionType.Dynamic);
                using var hs = WorldHinge(pivot);
                var c = w.Bodies.CreateConstraint(hs, anchor, bob);
                var gapsHold =
                    StillMissing("Constraint.AsHingeConstraint", () => ((Constraint)c).AsHingeConstraint())
                    & StillMissing("MotorSettings.GetSpringSettings", () => { using var m = MotorSettings.Create(); m.GetSpringSettings(); })
                    & StillMissing("BodyLockRead.Create", () => { using var l = BodyLockRead.Create(w.Physics.GetBodyLockInterface(), anchor); });
                Check(gapsHold, "gaps: Constraint casts, the MotorSettings spring getter and body locks are still unexported");
                c.Dispose();
            }
        }
        finally
        {
            Jolt.UnregisterTypes();
            Factory.DestroyInstance();
        }
    }
}
