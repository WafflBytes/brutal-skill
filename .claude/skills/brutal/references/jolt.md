# Physics 3D: Jolt

Read this for 3D physics (Jolt). For 2D physics read `box2d.md`. Jolt has 774 declared-but-unexported functions; check the gap list below before calling anything not in the recipes.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Package

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Jolt | `Brutal.JoltApi` | jolt.dll (custom C shim) | **yes** | Gen | "Highly experimental"; export gaps: `export-gaps.md` and the list below |

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
uint ball;                                    // primitive shapes via XxxShapeSettings only (T6); no mesh/hull/height field (T36)
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
- Pickups and triggers without the (unexported) contact listener: check distances in `Sim/` each step (coin within ball radius + coin radius). Ground checks: compare against your own level data. Jolt's ray casts are unexported (export gaps).

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
var hinge = Unsafe.BitCast<TwoBodyConstraint, HingeConstraint>(c);   // the AsHingeConstraint() cast is unexported (T46); same handle layout
hinge.SetMotorState(MotorState.Velocity); hinge.SetTargetAngularVelocity(2f);
float angle = hinge.GetCurrentAngle();
hinge.SetLimits(-0.3f, 0.8f);                                // or hs.SetLimitsMin/Max before creating; SetLimits(-pi, pi) = none
hinge.GetMotorSettings().SetTorqueLimit(1000f);              // a live view of the motor; the default is float.MaxValue (T51)
physics.RemoveConstraint(c); c.Dispose();                    // reference counts 1 -> 2 -> 1 -> 0 [RUN]: Dispose yours once
```
- **Which settings work** (create, simulate, limits held): `HingeConstraintSettings` (motor through the constraint's `SetMotorState`; limits through `SetLimitsMin/Max` or the constraint's `SetLimits`, read back with `HasLimits`/`GetLimitsMin/Max`), `PointConstraintSettings` (`SetPoint1/2`), `DistanceConstraintSettings` (`SetMinDistance/SetMaxDistance`: a rope), `FixedConstraintSettings` (`SetAutoDetectPoint(true)`), `SliderConstraintSettings` (`SetSliderAxis`, `SetLimitsMin/Max`, `SetAutoDetectPoint(true)`), `ConeConstraintSettings` (`SetTwistAxis1/2`, `SetHalfConeAngle`), `SwingTwistConstraintSettings` (`SetNormalHalfConeAngle`, `SetPlaneHalfConeAngle`, `SetTwistMin/MaxAngle`). Limits are soft: expect a few degrees or millimetres of give. Gear, pulley, rack-and-pinion, path, six-DOF and vehicle constraints weren't run.
- **Create constraints from body IDs.** `settings.Create(Body, Body)` needs `Body` pointers, and the body locks that would give you one are unexported (T46).
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
- **Filters:** `PhysicsSystem.GetDefaultBroadPhaseLayerFilter` / `GetDefaultLayerFilter` are unexported, but `BroadPhaseLayerFilter.Create()`, `ObjectLayerFilter.Create()`, `BodyFilter.Create()` and `ShapeFilter.Create()` give allow-everything filters that work.
- **`Character`** (the body-based controller) also works: `CharacterSettings.Create()` (`SetShape`, `SetLayer`, `SetUp`, `SetMass`, `SetFriction`), `Character.Create(settings, pos, rot, userData, physics)`, `AddToPhysicsSystem(Activation.Activate)`, call `PostSimulation(0.05f)` after every `physics.Update`, `SetLinearVelocity`, `GetGroundState()`, `RemoveFromPhysicsSystem()`, `Dispose()`. [RUN]. Prefer `CharacterVirtual` for player movement; use `Character` when the character should be an ordinary body that other bodies push.
- **No contact callbacks:** `ManagedCharacterContactListener` can't be constructed (its native proxy is unexported), so `CharacterVirtual` has no `OnContactAdded` / `OnContactSolve` (no custom push-back, no moving-platform hooks).

## Traps

| ID | Trap | Fix |
|---|---|---|
| T51 | **A hinge motor's torque is unlimited by default** (`float.MaxValue`), so a motor driving a body off the pivot goes straight through the hinge limits. A body centred on the axis stops [RUN] | `hinge.GetMotorSettings().SetTorqueLimit(t)` on the constraint (a live view; the settings' `Get/SetMotorSettings` are unexported, T46). Choose `t` from the load |
| T6 | Declared but not exported, e.g. `BoxShape.Create(float3)` → `EntryPointNotFoundException: BoxShape_Create_3` | See the Jolt gaps below. For primitive Jolt shapes (box, sphere, capsule, cylinder) use `XxxShapeSettings.Create(…).CreateShape()`; that does **not** work for mesh, hull or height field (T36) |
| T7 | `jolt/README.md` uses `EMotionType`/`EActivation`, which don't exist | `MotionType` / `Activation` |
| T18 | Jolt teardown out of order is a use-after-free: physics holds raw pointers to the layer tables | Bodies → physics → jobs/temp → filter tables → `UnregisterTypes` → `Factory.DestroyInstance` |
| T25 | Jolt is "highly experimental"; its API changes between versions | Pin the version and re-run ExportAudit after upgrading |
| T36 | **Mesh, convex-hull and height-field shapes can't be created in Jolt 2026.10.2.** `MeshShapeSettings.Create(triangles, materials)`, `ConvexHullShapeSettings.Create(points)` and `HeightFieldShapeSettings.Create(samples, …)` run, but the data never reaches Jolt, so `CreateShape()` throws `JoltException` ("Need triangles…", "Need at least 3 points…", "Sample count too low!"). Every other route is unexported. [ahwootech/BRUTAL#13](https://github.com/ahwootech/BRUTAL/issues/13): fixed internally, not yet released [RUN] | Level collision from primitives: `StaticCompoundShapeSettings.Create()` + `AddShape(position, rotation, boxSettings)` per piece, then `CreateShape()` [RUN]. Terrain: boxes. Dynamic convex objects: primitives or a compound. `RecipeCheck` check `T36` fails once it's fixed |
| T46 | **Jolt constraints and characters work, but their supporting calls are unexported (2026.10.2).** Missing: the casts `Constraint.AsHingeConstraint` and the other `Constraint.AsXxx()` (and `ConstraintSettings.AsSwingTwistConstraintSettings`); `MotorSettings.GetSpringSettings`, so every `Hinge/Slider/SixDOF/SwingTwist...ConstraintSettings.Get/SetMotorSettings` and `GetLimitsSpringSettings`; the body locks `BodyLockRead/Write.Create/GetBody` (so no `Body` pointer from an ID, and `settings.Create(Body, Body)` is unusable); `PhysicsSystem.AddConstraints/RemoveConstraints/GetConstraints`, `GetDefaultBroadPhaseLayerFilter`, `GetDefaultLayerFilter`; every `CharacterContactListener` proxy (`new ManagedCharacterContactListener` throws `EntryPointNotFoundException`, so no contact callbacks or push-back control); on characters `GetActiveContacts`, `CheckCollision*`, `GetTransformedShape`, `CharacterVsCharacterCollision`, `Get/SetSupportingVolume` [RUN: 7.34] | Create constraints with `BodyInterface.CreateConstraint(settings, id1, id2)`; reach `HingeConstraint` calls with `Unsafe.BitCast<TwoBodyConstraint, HingeConstraint>(c)`; set motors through the constraint (`SetMotorState`, `SetTargetAngularVelocity`, and `GetMotorSettings().SetTorqueLimit`, which is exported [RUN]); use `XxxFilter.Create()` for allow-all filters; wake bodies after `RemoveConstraint` with `ActivateBody`; do contact reactions in `Sim/` by distance checks |

## Export gaps

**Jolt gaps:**
- **Shape creation:**
  - Missing: the direct creators `BoxShape`/`SphereShape`/`CapsuleShape`/`CylinderShape.Create(…)` (`*_Create_3`), `ConvexHullShape_Create_2`, `MeshShape_Create_2`, and all `*_Register`.
  - Use instead, for primitives only: `XxxShapeSettings.Create(…).CreateShape()` [RUN].
  - Mesh, convex hull and height field have **no working route** (T36): their settings setters and other `Create` overloads are missing, and the exported `Create` drops its data. Use a `StaticCompoundShapeSettings` of primitives.
- **`Shape`:** every `AsXxx()` cast, `CastRay`, `CastRayClosest`, `CollidePoint`, `GetLocalBounds`, `GetWorldSpaceBounds`, `GetMassProperties`, `TransformShape`, save/restore.
- **`BodyInterface`:** `AddForces`, `AddImpulses`, `MoveKinematics`, `SetPositionsAndRotations*`, `SetLinearAndAngularVelocities`, `PrepareAddBodies`, `GetTransformedShape`.
- **`PhysicsSystem`:** `GetBodies`, `GetActiveBodies`, `GetBodyStats`, `GetBounds`, `AddConstraints`, `RemoveConstraints`, `SaveState`, `RestoreState`, `GetPhysicsSettings`, `GetMax*Limit`, `SetCombineFriction`, `SetCombineRestitution`.
- **Advertised helpers, all unusable in 2026.10.2:** `ContactEventListener`, `BodyAddBatch`, `SetTraceHandler`, `SetAssertHandler`, the `*ToIDMap` helpers.
- **Partially missing:** `CharacterVirtual`, `NarrowPhaseQuery`, `BroadPhaseQuery`, `SoftBody*`, `Ragdoll*`, `Vehicle*`, `Constraint`. For the exact list, search `export-gaps-detail.txt` (e.g. for `CharacterVirtual_`). Constraints, `CharacterVirtual` and `Character` still work through their exported subset: see 7.34 and T46.
