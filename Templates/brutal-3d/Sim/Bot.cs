using Brutal.Numerics;

namespace BrutalGame3D.Sim;

/// <summary>
/// Autopilot that follows <see cref="Level.Route"/> through the real input path (camera-relative moves and camera
/// turns), so the self-test and the snapshot run exercise the same physics a player does.
/// </summary>
public sealed class Bot
{
    int _next;

    public int WaypointIndex => _next;
    public bool Done => _next >= Level.Route.Length;

    public InputState Think(World world)
    {
        // Skip coin waypoints already collected and plain waypoints already reached.
        var pos = new float2(world.BallPosition.X, world.BallPosition.Z);
        while (_next < Level.Route.Length)
        {
            var wp = Level.Route[_next];
            var reached = wp.Coin >= 0 ? world.CoinTaken[wp.Coin] : float2.Distance(pos, wp.XZ) < 1.2f;
            if (!reached)
                break;
            _next++;
        }
        if (Done)
            return default;

        var target = Level.Route[_next].XZ;
        var to = target - pos;
        var dist = to.Length();
        var desiredSpeed = MathF.Min(World.MaxSpeed * 0.75f, 1.2f + dist * 1.6f);
        var desired = float2.NormalizeOrZero(to) * desiredSpeed;
        var vel = new float2(world.BallVelocity.X, world.BallVelocity.Z);
        var error = desired - vel;
        var push = error / MathF.Max(1f, error.Length() * 0.5f);
        if (push.LengthSquared() > 1)
            push = float2.NormalizeOrZero(push);
        var move = world.ToCameraRelative(new float3(push.X, 0, push.Y));

        // Turn the camera toward the direction of travel, as a player would.
        var turn = 0f;
        if (vel.LengthSquared() > 1)
        {
            var heading = MathF.Atan2(vel.X, -vel.Y); // yaw whose Forward() matches the velocity
            var diff = MathF.IEEERemainder(heading - world.CameraYaw, MathF.Tau);
            turn = Math.Clamp(diff * 1.5f, -1, 1);
        }
        return new InputState(move, turn, false, false);
    }
}
