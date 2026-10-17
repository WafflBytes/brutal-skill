# Physics: Box2D v3 and Jolt

Read this for 2D physics (Box2D) or 3D physics (Jolt). Jolt has 774 declared-but-unexported functions; check the gap list below before calling anything not in the recipe.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Jolt | `Brutal.JoltApi` | jolt.dll (custom C shim) | **yes** | Gen | "Highly experimental"; export gaps: `export-gaps.md` and the list below |
| Box2D | `Brutal.Box2DApi` | box2d.dll v3 | **yes** | Gen+Manual | IDs are record structs with instance methods |

## Name translation

**Box2D v3**
- `b2World_Step(w, …)` → `w.Step(…)`; `b2CreateWorld` → `Box2D.CreateWorld`; `b2MakeBox` → `Box2D.MakeBox`.
- `b2DefaultWorldDef()` → `B2WorldDef.Default with { … }` (same for `B2BodyDef`/`B2ShapeDef`).

**Jolt**
- `JPH::PhysicsSystem::Update` → `physics.Update(…)`. It goes through the C shim `PhysicsSystem_Update`; C++ overloads become shim names `_2`, `_3`.
- The `E` enum prefix is dropped (`EMotionType` → `MotionType`, `EActivation` → `Activation`).
- `BodyID` → `uint`, `ObjectLayer` → `ushort`, `BroadPhaseLayer` → `byte`.

## Key source locations

| What | File:line |
|---|---|
| Jolt `Factory.Instance` | `Brutal.Jolt/Manual/Handles.cs:14` |
| Box2D default defs | `Brutal.Box2D/Structs.cs:99,189,195` |

## Recipes

### 7.5 Box2D [RUN]
```csharp
var world = Box2D.CreateWorld(B2WorldDef.Default with { Gravity = new float2(0, -10) });
var ground = world.CreateBody(B2BodyDef.Default with { Position = new float2(0, -1) });
ground.CreatePolygonShape(B2ShapeDef.Default, Box2D.MakeBox(50, 1));
var body = world.CreateBody(B2BodyDef.Default with { Type = B2BodyType.Dynamic, Position = new float2(0, 10) });
body.CreatePolygonShape(B2ShapeDef.Default, Box2D.MakeBox(0.5f, 0.5f));
for (var i = 0; i < 60; i++) world.Step(1 / 60f, 4);
float2 pos = body.GetPosition();
world.Dispose();
```

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

### 7.22 Box2D contact, hit and sensor events [RUN]
Every event flag is **off by default** in `B2ShapeDef.Default`, including on sensors (T42). Opt in per shape:
```csharp
var sensor = zone.CreatePolygonShape(B2ShapeDef.Default with { IsSensor = true, EnableSensorEvents = true }, Box2D.MakeBox(2, 0.5f));
var ball = body.CreateCircleShape(
    B2ShapeDef.Default with { EnableContactEvents = true, EnableHitEvents = true, EnableSensorEvents = true }, // the visitor opts in too
    new B2Circle { Center = float2.Zero, Radius = 0.5f });
world.Step(1 / 60f, 4);
var c = world.GetContactEvents();          // valid until the next Step
for (var i = 0; i < c.BeginCount; i++) { ref var e = ref c.BeginEvents[i]; /* e.ShapeIdA, e.ShapeIdB, e.Manifold */ }
for (var i = 0; i < c.HitCount; i++)   { ref var h = ref c.HitEvents[i];   /* h.Point, h.Normal, h.ApproachSpeed: impact sounds */ }
// c.EndEvents / c.EndCount work the same way
var s = world.GetSensorEvents();           // BeginEvents/EndEvents with SensorShapeId, VisitorShapeId
```
- `B2Shape` is a record struct, so `==` compares ids. Map shapes back to game objects with `shape.SetUserData(nint)` / `GetUserData()`.
- Hit events fire only above the world's hit-speed threshold (`world.SetHitEventThreshold`, default 1 m/s).

## Traps

| ID | Trap | Fix |
|---|---|---|
| T6 | Declared but not exported, e.g. `BoxShape.Create(float3)` → `EntryPointNotFoundException: BoxShape_Create_3` | See the Jolt gaps below. For primitive Jolt shapes (box, sphere, capsule, cylinder) use `XxxShapeSettings.Create(…).CreateShape()`; that does **not** work for mesh, hull or height field (T36) |
| T7 | `jolt/README.md` uses `EMotionType`/`EActivation`, which don't exist | `MotionType` / `Activation` |
| T18 | Jolt teardown out of order is a use-after-free: physics holds raw pointers to the layer tables | Bodies → physics → jobs/temp → filter tables → `UnregisterTypes` → `Factory.DestroyInstance` |
| T23 | `Box2D.cs:11-16` loads `box2dd` under `#if DEBUG` | Matters only when building from source |
| T25 | Jolt is "highly experimental"; its API changes between versions | Pin the version and re-run ExportAudit after upgrading |
| T42 | **Every Box2D event flag defaults to off** in this build: `B2ShapeDef.Default` has `EnableContactEvents`, `EnableHitEvents` and `EnableSensorEvents` all false. Box2D v3.0 defaulted contact events to on, so code written for it silently gets no events. Sensor events need the flag on **both** the sensor and the visitor shape [RUN] | Set the flags in every shape def that should report. `RecipeCheck 7.22` fails if the defaults change |
| T36 | **Mesh, convex-hull and height-field shapes can't be created in Jolt 2026.10.2.** `MeshShapeSettings.Create(triangles, materials)`, `ConvexHullShapeSettings.Create(points)` and `HeightFieldShapeSettings.Create(samples, …)` resolve and run, but the data never reaches Jolt, so `CreateShape()` throws `JoltException` ("Need triangles to create a mesh shape!", "Need at least 3 points to make a hull", "HeightFieldShape: Sample count too low!" even with 64×64 samples). Every other route is unexported: `Create()`, `MeshShapeSettings.Create(vertices, indexedTriangles)`, `SetTriangleVertices`, `SetIndexedTriangles`, `SetPoints`, `SetHeightSamples`. Same geometry works in upstream Jolt 5.6.0. Reported as [ahwootech/BRUTAL#13](https://github.com/ahwootech/BRUTAL/issues/13); RocketWerkz says it's fixed internally (2026-10-15), not yet released [RUN] | Build level collision from primitives: `StaticCompoundShapeSettings.Create()` + `AddShape(position, rotation, boxSettings)` per piece, then `CreateShape()` [RUN: a ball rests on it]. For terrain, approximate with boxes; for dynamic convex objects, use box/sphere/capsule or a compound of them. On upgrade, `RecipeCheck` check `T36` fails as soon as the bug is fixed |

## Export gaps

Box2D: 64 inline maths helpers are missing (`b2Add`, `b2Dot`, `b2Normalize`, `b2MakeRot`, `b2Rot_*`, `b2AABB_*`…); use Numerics instead.

**Jolt gaps:**
- **Shape creation:**
  - Missing: the direct creators `BoxShape`/`SphereShape`/`CapsuleShape`/`CylinderShape.Create(…)` (`*_Create_3`), `ConvexHullShape_Create_2`, `MeshShape_Create_2`, and all `*_Register`.
  - Use instead, for primitives only: `XxxShapeSettings.Create(…).CreateShape()` [RUN].
  - Mesh, convex hull and height field have **no working route** (T36): their settings setters and other `Create` overloads are missing, and the exported `Create` drops its data. Use a `StaticCompoundShapeSettings` of primitives.
- **`Shape`:** every `AsXxx()` cast, `CastRay`, `CastRayClosest`, `CollidePoint`, `GetLocalBounds`, `GetWorldSpaceBounds`, `GetMassProperties`, `TransformShape`, save/restore.
- **`BodyInterface`:** `AddForces`, `AddImpulses`, `MoveKinematics`, `SetPositionsAndRotations*`, `SetLinearAndAngularVelocities`, `PrepareAddBodies`, `GetTransformedShape`.
- **`PhysicsSystem`:** `GetBodies`, `GetActiveBodies`, `GetBodyStats`, `GetBounds`, `AddConstraints`, `RemoveConstraints`, `SaveState`, `RestoreState`, `GetPhysicsSettings`, `GetMax*Limit`, `SetCombineFriction`, `SetCombineRestitution`.
- **Advertised helpers, all unusable in 2026.10.2:** `ContactEventListener`, `BodyAddBatch`, `SetTraceHandler`, `SetAssertHandler`, the `*ToIDMap` helpers.
- **Partially missing:** `CharacterVirtual`, `NarrowPhaseQuery`, `BroadPhaseQuery`, `SoftBody*`, `Ragdoll*`, `Vehicle*`, `Constraint`. Run the audit for the exact list.
