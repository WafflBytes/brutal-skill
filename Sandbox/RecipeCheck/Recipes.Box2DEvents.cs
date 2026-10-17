// Recipe 7.22: Box2D v3 contact, hit and sensor events. Headless.
using Brutal.Box2DApi;
using Brutal.Numerics;

static unsafe partial class Recipes
{
    public static void Box2DEventsRecipe()
    {
        // T42: every event flag is off in B2ShapeDef.Default (the header says so; Box2D v3.0 defaulted contacts on).
        var d = B2ShapeDef.Default;
        Check(!d.EnableContactEvents && !d.EnableHitEvents && !d.EnableSensorEvents, "T42 holds: contact, hit and sensor events all default to off; changed upstream? update the skill");
        var (begin, hit, end, sensorIn, sensorOut) = DropBallThroughSensor(onSensor: true, onVisitor: true);
        Check(begin == 1, $"one contact begin when the ball lands ({begin})");
        Check(hit >= 1, $"a hit event for the landing ({hit})");
        Check(end == 0, $"no contact end while it rests ({end})");
        Check(sensorIn == 1 && sensorOut == 1, $"the ball entered and left the sensor once ({sensorIn}/{sensorOut})");

        // Sensor events need EnableSensorEvents on the sensor *and* on the visitor (T42).
        var (_, _, _, onlySensorIn, _) = DropBallThroughSensor(onSensor: true, onVisitor: false);
        var (_, _, _, onlyVisitorIn, _) = DropBallThroughSensor(onSensor: false, onVisitor: true);
        Check(onlySensorIn == 0 && onlyVisitorIn == 0, $"sensor events need the flag on both shapes (sensor only: {onlySensorIn}, visitor only: {onlyVisitorIn})");
    }

    static (int Begin, int Hit, int End, int SensorIn, int SensorOut) DropBallThroughSensor(bool onSensor, bool onVisitor)
    {
        var world = Box2D.CreateWorld(B2WorldDef.Default with { Gravity = new float2(0, -10) });
        try
        {
            var ground = world.CreateBody(B2BodyDef.Default with { Position = new float2(0, -1) });
            var groundShape = ground.CreatePolygonShape(B2ShapeDef.Default, Box2D.MakeBox(20, 1));

            // A static sensor box the ball falls through on the way down.
            var zone = world.CreateBody(B2BodyDef.Default with { Position = new float2(0, 4) });
            var sensor = zone.CreatePolygonShape(
                B2ShapeDef.Default with { IsSensor = true, EnableSensorEvents = onSensor },
                Box2D.MakeBox(2, 0.5f)
            );

            var ballBody = world.CreateBody(B2BodyDef.Default with { Type = B2BodyType.Dynamic, Position = new float2(0, 8) });
            var ball = ballBody.CreateCircleShape(
                // Opt in to every event this shape should report: contacts, hits (impact speed/point, for sounds and
                // damage) and, as a visitor, sensor overlaps.
                B2ShapeDef.Default with { EnableContactEvents = true, EnableHitEvents = true, EnableSensorEvents = onVisitor },
                new B2Circle { Center = float2.Zero, Radius = 0.5f }
            );

            int begin = 0, hit = 0, end = 0, sIn = 0, sOut = 0;
            for (var i = 0; i < 180; i++)
            {
                world.Step(1 / 60f, 4);
                // Events are valid until the next Step. Each pointer array holds Count entries.
                var c = world.GetContactEvents();
                for (var k = 0; k < c.BeginCount; k++)
                    if (Involves(c.BeginEvents[k].ShapeIdA, c.BeginEvents[k].ShapeIdB, ball, groundShape))
                        begin++;
                for (var k = 0; k < c.HitCount; k++)
                    if (Involves(c.HitEvents[k].ShapeIdA, c.HitEvents[k].ShapeIdB, ball, groundShape) && c.HitEvents[k].ApproachSpeed > 1)
                        hit++;
                end += c.EndCount;
                var s = world.GetSensorEvents();
                for (var k = 0; k < s.BeginCount; k++)
                    if (s.BeginEvents[k].SensorShapeId == sensor && s.BeginEvents[k].VisitorShapeId == ball)
                        sIn++;
                for (var k = 0; k < s.EndCount; k++)
                    if (s.EndEvents[k].SensorShapeId == sensor && s.EndEvents[k].VisitorShapeId == ball)
                        sOut++;
            }
            return (begin, hit, end, sIn, sOut);
        }
        finally
        {
            world.Dispose();
        }
    }

    static bool Involves(B2Shape a, B2Shape b, B2Shape x, B2Shape y) => (a == x && b == y) || (a == y && b == x);
}
