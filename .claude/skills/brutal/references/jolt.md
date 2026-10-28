# Physics 3D: Jolt

Read this for 3D physics (Jolt). For 2D physics read `box2d.md`. **Use Jolt 2026.10.4 or later.** 2026.10.2 left 774 declared functions unexported and dropped mesh, hull and height-field data (T6, T36, T46). 2026.10.4 exports every declared function, but only the calls in the recipes below have been run.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Package

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Jolt | `Brutal.JoltApi` | jolt.dll (custom C shim), in `Ahwoo.BRUTAL.Jolt.Native` | **yes** | Gen | "Highly experimental"; 0 export gaps from 2026.10.4 (774 in 2026.10.2) |

## Name translation

- `JPH::PhysicsSystem::Update` → `physics.Update(…)`. It goes through the C shim `PhysicsSystem_Update`; C++ overloads become shim names `_2`, `_3`.
- The `E` enum prefix is dropped (`EMotionType` → `MotionType`, `EActivation` → `Activation`).
- `BodyID` → `uint`, `ObjectLayer` → `ushort`, `BroadPhaseLayer` → `byte`.

## Key source locations

| What | File:line |
|---|---|
| Jolt `Factory.Instance` | `Brutal.Jolt/Manual/Handles.cs:14` |

## Recipes

### 7.6 Jolt [RUN]
```csharp
Jolt.RegisterDefaultAllocator();
Factory.Instance = Factory.Create();          // the global owns it; never Dispose it (T17)
Jolt.RegisterTypes();
const ushort Static = 0, Moving = 1;
var bp = BroadPhaseLayerInterfaceTable.Create(2, 2);
bp.MapObjectToBroadPhaseLayer(Static, 0); bp.MapObjectToBroadPhaseLayer(Moving, 1);
var pairs = ObjectLayerPairFilterTable.Create(2);
pairs.EnableCollision(Moving, Static); pairs.EnableCollision(Moving, Moving);
var objVsBp = ObjectVsBroadPhaseLayerFilterTable.Create(bp, 2, pairs, 2);
var temp = TempAllocatorImpl.Create(10 * 1024 * 1024);
var jobs = JobSystemThreadPool.Create(2048, 8, Environment.ProcessorCount - 1);
var physics = PhysicsSystem.Create();
physics.Init(1024, 0, 1024, 1024, bp, objVsBp, pairs);
physics.SetGravity(new float3(0, -9.81f, 0));
var bodies = physics.GetBodyInterface();      // borrowed
uint ball;                                    // shapes via XxxShapeSettings.Create(...).CreateShape(); mesh/hull/height field: 7.44
using (var ss = SphereShapeSettings.Create(0.5f))
using (var shape = ss.CreateShape())          // ref-counted: safe to dispose once the body exists
using (var bcs = BodyCreationSettings.Create(shape, new float3(0, 5, 0), floatQuat.Identity, MotionType.Dynamic, Moving))
    ball = bodies.CreateAndAddBody(bcs, Activation.Activate);
// floor: BoxShapeSettings.Create(new float3(50, 1, 50), 0.05f, default), MotionType.Static, Activation.DontActivate
physics.OptimizeBroadPhase();
for (var i = 0; i < 120; i++)
    if (physics.Update(1 / 60f, 1, temp, jobs) != PhysicsUpdateError.None) throw new Exception("Jolt");
float3 p = bodies.GetPosition(ball);          // ~0.48 on a floor at y=0
bodies.RemoveBody(ball); bodies.DestroyBody(ball);          // teardown order matters (T18)
physics.Dispose(); jobs.Dispose(); temp.Dispose();
objVsBp.Dispose(); pairs.Dispose(); bp.Dispose();
Jolt.UnregisterTypes(); Factory.DestroyInstance();
```

### 7.27 Jolt body tuning, driving a body, determinism [RUN]
Checked by `RecipeCheck 7.27`, and used by the `brutal-3d` template.
```csharp
using (var bcs = BodyCreationSettings.Create(shape, pos, floatQuat.Identity, MotionType.Dynamic, Moving))
{
    bcs.SetFriction(1.0f); bcs.SetRestitution(0.3f);
    bcs.SetLinearDamping(0.25f); bcs.SetAngularDamping(0.6f);
    bcs.SetMotionQuality(MotionQuality.LinearCast);   // continuous collision: no tunnelling through thin walls at speed
    bcs.SetAllowSleeping(false);                       // for a body the player can push at any time
    ball = bodies.CreateAndAddBody(bcs, Activation.Activate);
}
bodies.AddLinearAndAngularVelocity(ball, linear, angular);   // drive it; add matching spin so a ball rolls, not slides
float3 p = bodies.GetPosition(ball); floatQuat q = bodies.GetRotation(ball);
float3 v = bodies.GetLinearVelocity(ball); float3 w = bodies.GetAngularVelocity(ball);
bodies.SetPositionRotationAndVelocity(ball, start, floatQuat.Identity, float3.Zero, float3.Zero); // reset
```
- **Deterministic on one machine and build** (bit-identical after 120 steps, multi-threaded) [RUN]; don't assume it across CPUs for lockstep networking.
- One `Factory`/`RegisterTypes`, one job system and one temp allocator can serve several `PhysicsSystem`s in one process, one after another.
- Pickups and triggers: distance checks in `Sim/` each step (coin within ball radius + coin radius) are simple and deterministic [RUN]. Body contact listeners (`ContactListener`) and ray casts (`NarrowPhaseQuery`, `Shape.CastRay`) are exported from 2026.10.4 but haven't been run; run one before you rely on it (R2).

### 7.34 Jolt constraints, compound shapes and character controllers [RUN]
Runnable copy: `RecipeCheck 7.34` (`Sandbox/RecipeCheck/Recipes.JoltAdvanced.cs`). Same world setup as 7.6.
```csharp
// Constraints: settings -> BodyInterface.CreateConstraint(settings, bodyId1, bodyId2) -> physics.AddConstraint
using var hs = HingeConstraintSettings.Create();
hs.SetSpace(ConstraintSpace.WorldSpace);                     // then every point and axis below is in world space
hs.SetPoint1(pivot); hs.SetPoint2(pivot);
hs.SetHingeAxis1(new float3(0, 0, 1)); hs.SetHingeAxis2(new float3(0, 0, 1));
hs.SetNormalAxis1(new float3(1, 0, 0)); hs.SetNormalAxis2(new float3(1, 0, 0));
var c = bodies.CreateConstraint(hs, anchorId, bobId);        // TwoBodyConstraint; carries ONE reference of its own
physics.AddConstraint(c);                                    // the system takes another
var hinge = ((Constraint)c).AsHingeConstraint();             // 2026.10.4+; on 2026.10.2: Unsafe.BitCast<TwoBodyConstraint, HingeConstraint>(c) (T46)
hinge.SetMotorState(MotorState.Velocity); hinge.SetTargetAngularVelocity(2f);
float angle = hinge.GetCurrentAngle();
hinge.SetLimits(-0.3f, 0.8f);                                // or hs.SetLimitsMin/Max before creating; SetLimits(-pi, pi) = none
hinge.GetMotorSettings().SetTorqueLimit(1000f);              // a live view of the motor; the default is float.MaxValue (T51)
physics.RemoveConstraint(c); c.Dispose();                    // reference counts 1 -> 2 -> 1 -> 0 [RUN]: Dispose yours once
```
- **Which settings work** (create, simulate, limits held): `HingeConstraintSettings` (motor through the constraint's `SetMotorState`; limits through `SetLimitsMin/Max` or the constraint's `SetLimits`, read back with `HasLimits`/`GetLimitsMin/Max`), `PointConstraintSettings` (`SetPoint1/2`), `DistanceConstraintSettings` (`SetMinDistance/SetMaxDistance`: a rope), `FixedConstraintSettings` (`SetAutoDetectPoint(true)`), `SliderConstraintSettings` (`SetSliderAxis`, `SetLimitsMin/Max`, `SetAutoDetectPoint(true)`), `ConeConstraintSettings` (`SetTwistAxis1/2`, `SetHalfConeAngle`), `SwingTwistConstraintSettings` (`SetNormalHalfConeAngle`, `SetPlaneHalfConeAngle`, `SetTwistMin/MaxAngle`). Limits are soft: expect a few degrees or millimetres of give. Gear, pulley, rack-and-pinion, path, six-DOF and vehicle constraints weren't run.
- **Create constraints from body IDs** with `BodyInterface.CreateConstraint`. To get a `Body`: `BodyLockRead.Create(physics.GetBodyLockInterface(), id)`, then `Succeeded()`, `GetBody()`, `ReleaseLock()`, `Dispose()` [RUN, 2026.10.4]. `settings.Create(Body, Body)` wasn't run.
- **Spring settings are copies** (T56): `MotorSettings.GetSpringSettings()` returns an owned copy. Edit it, write it back with `SetSpringSettings(copy)`, and dispose both [RUN].
- **Hinge limits** [RUN] hold, including ones set at run time, except against an uncapped motor driving a body off the pivot (T51).
- **Neither a motor nor `RemoveConstraint` wakes a sleeping body:** call `bodies.ActivateBody(id)` after either [RUN].
- **Don't overlap constrained bodies:** contact fights the constraint (a hinge motor reached 0.8 of 2.0 rad). Constraint points needn't sit on a body.
- **Compound shapes:** `StaticCompoundShapeSettings.Create()` / `MutableCompoundShapeSettings.Create()`, then `AddShape(offset, rotation, childSettings)` per child and `CreateShape()`. Both work [RUN].
```csharp
// CharacterVirtual: a capsule you move yourself; Jolt handles collision, slopes, steps and ground state
using var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.3f);      // (half height of the cylinder part, radius)
using var shape = capsuleSettings.CreateShape();
using var cs = CharacterVirtualSettings.Create();
cs.SetShape(shape); cs.SetUp(new float3(0, 1, 0)); cs.SetMaxSlopeAngle(MathF.PI * 50 / 180);
var ch = CharacterVirtual.Create(cs, startPos, floatQuat.Identity, physics);
var bpf = BroadPhaseLayerFilter.Create(); var olf = ObjectLayerFilter.Create(); var bf = BodyFilter.Create(); var sf = ShapeFilter.Create(); // allow-all
// every fixed step:
var onGround = ch.GetGroundState() == CharacterBase.GroundState.OnGround;
ch.SetLinearVelocity(new float3(vx, onGround ? (jump ? 6f : 0f) : ch.GetLinearVelocity().Y - 9.81f / 60, 0)); // YOU apply gravity and jumps
ch.ExtendedUpdate(1 / 60f, gravity, in stairs, bpf, olf, bf, sf, temp);   // or ch.Update(...) without stairs / stick-to-floor
// ch.GetPosition(), GetGroundState(), GetGroundNormal(), GetGroundVelocity(); dispose filters and ch before the physics system
```
- **`stairs`** is `new CharacterVirtual.ExtendedUpdateSettings { StickToFloorStepDown = (0, -0.5, 0), WalkStairsStepUp = (0, 0.4, 0), WalkStairsMinStepForward = 0.02f, WalkStairsStepForwardTest = 0.15f, WalkStairsCosAngleForwardContact = cos(75 deg), WalkStairsStepDownExtra = float3.Zero }` (Jolt's C++ defaults). `Update` alone is stopped by a 0.3 m step; `ExtendedUpdate` climbs it [RUN].
- **Filters:** `BroadPhaseLayerFilter.Create()`, `ObjectLayerFilter.Create()`, `BodyFilter.Create()` and `ShapeFilter.Create()` give allow-everything filters that work [RUN]. `PhysicsSystem.GetDefaultBroadPhaseLayerFilter` / `GetDefaultLayerFilter` are exported from 2026.10.4 but not run.
- **`Character`** (the body-based controller) also works: `CharacterSettings.Create()` (`SetShape`, `SetLayer`, `SetUp`, `SetMass`, `SetFriction`), `Character.Create(settings, pos, rot, userData, physics)`, `AddToPhysicsSystem(Activation.Activate)`, call `PostSimulation(0.05f)` after every `physics.Update`, `SetLinearVelocity`, `GetGroundState()`, `RemoveFromPhysicsSystem()`, `Dispose()`. [RUN]. Prefer `CharacterVirtual` for player movement; use `Character` when the character should be an ordinary body that other bodies push.
- **Contact callbacks** (2026.10.4+) [RUN]: subclass `ManagedCharacterContactListener`, override `OnContactAdded(CharacterVirtual, in CharacterContact, ref CharacterContactSettings)` or `OnContactValidate(...)` (return false to pass through that body; `contact.BodyB` is its ID), then call `ch.SetListener(listener)`. Callbacks run inside `Update`/`ExtendedUpdate` and may be on worker threads, so use `Interlocked` or `volatile`. A throwing callback is swallowed, so check `listener.LastCallbackException`. Detach with `ch.SetListener(default)`, then `Dispose()` the listener: it pins itself and has no finalizer.
```csharp
sealed class WallListener(uint wall) : ManagedCharacterContactListener
{
    public int Hits; public volatile bool PassWall;
    protected override void OnContactAdded(CharacterVirtual c, in CharacterContact contact, ref CharacterContactSettings s)
    { if (contact.BodyB == wall) Interlocked.Increment(ref Hits); }
    protected override bool OnContactValidate(CharacterVirtual c, in CharacterContact contact) => !(PassWall && contact.BodyB == wall);
}
```

### 7.44 Jolt mesh, convex-hull and height-field shapes [RUN, 2026.10.4]
Runnable copy: `RecipeCheck 7.44` (`JoltMeshHull` in `Sandbox/RecipeCheck/Program.cs`). A ball dropped on the mesh or height-field floor rests at y≈0.48.
```csharp
ReadOnlySpan<Triangle> quad = [Triangle.Create(a, b, c), Triangle.Create(a, c, d)];
using (var ms = MeshShapeSettings.Create(quad, ReadOnlySpan<PhysicsMaterial>.Empty)) mesh = ms.CreateShape();  // for static bodies
ReadOnlySpan<float3> corners = [/* the 8 corners of a 2 m cube */];
using (var hs = ConvexHullShapeSettings.Create(corners)) hull = hs.CreateShape();           // hull.GetLocalBounds().Max.Y == 1
var samples = (float*)NativeMemory.AllocZeroed(64 * 64, sizeof(float));                      // n*n heights, row-major
using (var hf = HeightFieldShapeSettings.Create(samples, new float3(-31.5f, 0, -31.5f), new float3(1, 1, 1), 64, default, ReadOnlySpan<PhysicsMaterial>.Empty))
    terrain = hf.CreateShape();
NativeMemory.Free(samples);                                                                  // safe once CreateShape has returned
```
- A static compound of boxes (`StaticCompoundShapeSettings.Create()`, `AddShape(pos, rot, boxSettings)`, `CreateShape()`) still works [RUN], and is simplest for levels built from boxes.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T51 | **A hinge motor's torque is unlimited by default** (`float.MaxValue`), so a motor driving a body off the pivot goes straight through the hinge limits. A body centred on the axis stops [RUN] | `hinge.GetMotorSettings().SetTorqueLimit(t)` on the constraint (a live view; the settings' `Get/SetMotorSettings` are unexported, T46). Choose `t` from the load |
| T6 | **Jolt 2026.10.2 only:** 774 functions declared but not exported, e.g. `BoxShape.Create(float3)` → `EntryPointNotFoundException: BoxShape_Create_3`. 2026.10.4 exports all of them [RUN: ExportAudit] | Use 2026.10.4+. On 2026.10.2, create shapes with `XxxShapeSettings.Create(…).CreateShape()` |
| T7 | `jolt/README.md` uses `EMotionType`/`EActivation`, which don't exist | `MotionType` / `Activation` |
| T18 | Jolt teardown out of order is a use-after-free: physics holds raw pointers to the layer tables | Bodies → physics → jobs/temp → filter tables → `UnregisterTypes` → `Factory.DestroyInstance` |
| T25 | Jolt is "highly experimental"; its API and exports change between versions (2026.10.2 → 2026.10.4 closed 774 gaps) | Pin the version and re-run ExportAudit after upgrading |
| T56 | `MotorSettings.GetSpringSettings()` returns a **copy** (its doc comment says "an owned copy"). Setting values on it changes nothing, with no error [RUN: 7.34] | Edit the copy, write it back with `SetSpringSettings(copy)`, dispose both. Check other getters' doc comments for "owned copy" |
| T36 | **Jolt 2026.10.2 only:** mesh, convex-hull and height-field shapes can't be created. `MeshShapeSettings.Create(triangles, materials)`, `ConvexHullShapeSettings.Create(points)` and `HeightFieldShapeSettings.Create(samples, …)` run, but the data never reaches Jolt, so `CreateShape()` throws `JoltException` ("Need triangles…", "Need at least 3 points…", "Sample count too low!"). Fixed in 2026.10.4 ([ahwootech/BRUTAL#13](https://github.com/ahwootech/BRUTAL/issues/13)) [RUN: 7.44] | Use 2026.10.4+ (7.44). On 2026.10.2, build from a `StaticCompoundShapeSettings` of boxes |
| T46 | **Jolt 2026.10.2 only:** constraints and characters worked, but their supporting calls were unexported: the `Constraint.AsXxx()` casts, `MotorSettings.GetSpringSettings` (so every `...ConstraintSettings.Get/SetMotorSettings`), the body locks, `PhysicsSystem.AddConstraints`/`GetDefault*Filter`, every `CharacterContactListener` proxy (`new ManagedCharacterContactListener` threw `EntryPointNotFoundException`), and character `GetActiveContacts`/`CheckCollision*`. 2026.10.4 exports them all; the casts, spring settings, body locks and character listener have run [RUN: 7.34] | Use 2026.10.4+. On 2026.10.2: `Unsafe.BitCast<TwoBodyConstraint, HingeConstraint>(c)`, motors through the constraint, `XxxFilter.Create()`, and distance checks in `Sim/` instead of callbacks |

## Export gaps

**None from Jolt 2026.10.4** (ExportAudit: 3246 declared, 0 missing). Exported isn't the same as working, so run each new call once (R2). Run so far: everything in 7.6, 7.27, 7.34 and 7.44. **Exported but not run:** `ContactListener` / `ContactEventListener` (body contacts), ray and shape casts (`NarrowPhaseQuery`, `BroadPhaseQuery`, `Shape.CastRay`), `BodyInterface.AddForces/AddImpulses/MoveKinematics`, `PhysicsSystem.SaveState/RestoreState`, `SoftBody*`, `Ragdoll*`, `Vehicle*`, and the gear, pulley, path and six-DOF constraints.
