# Physics 2D: Box2D v3

Read this for 2D physics (Box2D). For 3D physics read `jolt.md`.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Package

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| Box2D | `Brutal.Box2DApi` | box2d.dll v3 | **yes** | Gen+Manual | IDs are record structs with instance methods |

## Name translation

**Box2D v3**
- `b2World_Step(w, …)` → `w.Step(…)`; `b2CreateWorld` → `Box2D.CreateWorld`; `b2MakeBox` → `Box2D.MakeBox`.
- `b2DefaultWorldDef()` → `B2WorldDef.Default with { … }` (same for `B2BodyDef`/`B2ShapeDef`).

## Key source locations

| What | File:line |
|---|---|
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
| T23 | `Box2D.cs:11-16` loads `box2dd` under `#if DEBUG` | Matters only when building from source |
| T42 | **Every Box2D event flag defaults to off** in this build: `B2ShapeDef.Default` has `EnableContactEvents`, `EnableHitEvents` and `EnableSensorEvents` all false. Code written for upstream v3.0 (contact events on) silently gets none. Sensor events need the flag on **both** the sensor and the visitor shape [RUN] | Set the flags in every shape def that should report. `RecipeCheck 7.22` fails if the defaults change |

## Export gaps

Box2D: 64 inline maths helpers are missing (`b2Add`, `b2Dot`, `b2Normalize`, `b2MakeRot`, `b2Rot_*`, `b2AABB_*`…); use Numerics instead.
