using Brutal.Numerics;

namespace BrutalGame3D.Sim;

public enum GameState
{
    Playing,
    Won,
    TimeUp,
}

/// <summary>
/// The whole game: a ball rolled by the player through a Jolt physics arena, coins to collect, a countdown, and a
/// chase camera. Deterministic and SDL-free: it advances only through <see cref="Step"/> at a fixed timestep, so it
/// runs the same in the window, the snapshot script and the headless self-test.
/// </summary>
public sealed class World : IDisposable
{
    public const float Dt = 1f / 60f;
    public const float TimeLimit = 60f;
    public const float Gravity = -14f;
    public const float Accel = 16f; // m/s^2 the player can add on the ground
    public const float MaxSpeed = 10f;
    public const float TurnRate = 2.4f; // camera rad/s
    public const float CoinRadius = 0.45f;
    public const float CameraDistance = 7.5f;
    public const float CameraHeight = 4.2f;
    public const float FallLimit = -8f;

    PhysicsScene _scene;

    public float3 BallPosition { get; private set; }
    public floatQuat BallRotation { get; private set; }
    public float3 BallVelocity { get; private set; }
    public bool[] CoinTaken { get; } = new bool[Level.Coins.Length];
    public int Score { get; private set; }
    public int CoinCount => Level.Coins.Length;
    public float TimeLeft { get; private set; }
    public GameState State { get; private set; }
    public long Ticks { get; private set; }
    public float Time => Ticks * Dt;
    public float FinishTime { get; private set; } // game time when the round ended

    /// <summary>Camera yaw: 0 looks toward -Z (north); positive turns right.</summary>
    public float CameraYaw { get; private set; }
    public float3 CameraPosition { get; private set; }
    public float3 CameraTarget { get; private set; }

    // What happened during the last Step, for sounds. Reset every step.
    public int CoinsThisStep { get; private set; }
    public float BumpThisStep { get; private set; } // impact speed change in m/s, 0 = none
    public bool TickThisStep { get; private set; } // one per second during the last 5 seconds
    public bool FinishedThisStep { get; private set; }
    public bool RestartedThisStep { get; private set; }
    public bool FellThisStep { get; private set; }

    public World()
    {
        _scene = new PhysicsScene(Level.Blocks, Level.BallStart, Gravity);
        Reset(recreatePhysics: false);
    }

    void Reset(bool recreatePhysics)
    {
        if (recreatePhysics)
        {
            // A fresh scene, not a teleport: no contact cache or solver state survives, so a restart is
            // bit-identical to a new game.
            _scene.Dispose();
            _scene = new PhysicsScene(Level.Blocks, Level.BallStart, Gravity);
        }
        Array.Clear(CoinTaken);
        Score = 0;
        TimeLeft = TimeLimit;
        State = GameState.Playing;
        Ticks = 0;
        FinishTime = 0;
        CameraYaw = 0;
        ReadBall();
        CameraTarget = BallPosition;
        CameraPosition = DesiredCamera();
    }

    public static float3 Forward(float yaw) => new(MathF.Sin(yaw), 0, -MathF.Cos(yaw));

    public static float3 Right(float yaw) => new(MathF.Cos(yaw), 0, MathF.Sin(yaw));

    /// <summary>Converts a world-space XZ direction into the camera-relative <see cref="InputState.Move"/>.</summary>
    public float2 ToCameraRelative(float3 worldDir) =>
        new(float3.Dot(worldDir, Right(CameraYaw)), float3.Dot(worldDir, Forward(CameraYaw)));

    public void Step(in InputState input)
    {
        CoinsThisStep = 0;
        BumpThisStep = 0;
        TickThisStep = false;
        FinishedThisStep = false;
        RestartedThisStep = false;
        FellThisStep = false;

        if (input.Restart || (State != GameState.Playing && input.Confirm && Time - FinishTime > 0.75f))
        {
            Reset(recreatePhysics: true);
            RestartedThisStep = true;
            return;
        }

        CameraYaw += Math.Clamp(input.Turn, -1, 1) * TurnRate * Dt;

        var before = BallVelocity;
        var pushed = float3.Zero;
        if (State == GameState.Playing)
        {
            var move = input.Move.LengthSquared() > 1 ? float2.NormalizeOrZero(input.Move) : input.Move; // T19
            var dir = Right(CameraYaw) * move.X + Forward(CameraYaw) * move.Y;
            // Don't accelerate past the top speed in the push direction; braking and steering always work.
            var along = float3.Dot(new float3(before.X, 0, before.Z), dir);
            if (along < MaxSpeed && dir.LengthSquared() > 0)
            {
                pushed = dir * (Accel * Dt);
                // Matching spin (v = w x r) so the ball rolls instead of skidding: w = up x dir * (|v| / r).
                var spin = float3.Cross(new float3(0, 1, 0), dir) * (Accel * Dt / PhysicsScene.BallRadius);
                _scene.Push(pushed, spin);
            }
        }

        _scene.Update(Dt);
        ReadBall();

        // A big velocity change the player's push doesn't explain is an impact: walls, landing, block edges.
        var change = (BallVelocity - before - pushed).Length();
        var gravityStep = MathF.Abs(Gravity) * Dt;
        if (change > 2.5f + gravityStep)
            BumpThisStep = change;

        if (BallPosition.Y < FallLimit)
        {
            _scene.PlaceBall(Level.BallStart);
            ReadBall();
            FellThisStep = true;
        }

        if (State == GameState.Playing)
        {
            var reach = PhysicsScene.BallRadius + CoinRadius;
            for (var i = 0; i < Level.Coins.Length; i++)
            {
                if (CoinTaken[i] || float3.DistanceSquared(BallPosition, Level.Coins[i]) > reach * reach)
                    continue;
                CoinTaken[i] = true;
                Score++;
                CoinsThisStep++;
            }

            var previous = TimeLeft;
            TimeLeft = MathF.Max(0, TimeLeft - Dt);
            if (TimeLeft <= 5 && TimeLeft > 0 && MathF.Ceiling(TimeLeft) < MathF.Ceiling(previous))
                TickThisStep = true;

            if (Score == CoinCount)
                Finish(GameState.Won);
            else if (TimeLeft <= 0)
                Finish(GameState.TimeUp);
        }

        // Chase camera: follow the ball smoothly from behind and above (frame-rate independent smoothing).
        var k = 1 - MathF.Exp(-Dt * 6f);
        CameraTarget = float3.Lerp(CameraTarget, BallPosition, 1 - MathF.Exp(-Dt * 12f));
        CameraPosition = float3.Lerp(CameraPosition, DesiredCamera(), k);
        Ticks++;
    }

    void Finish(GameState state)
    {
        State = state;
        FinishTime = Time;
        FinishedThisStep = true;
    }

    float3 DesiredCamera() => CameraTarget - Forward(CameraYaw) * CameraDistance + new float3(0, CameraHeight, 0);

    void ReadBall()
    {
        BallPosition = _scene.BallPosition;
        BallRotation = _scene.BallRotation;
        BallVelocity = _scene.BallVelocity;
    }

    /// <summary>Order-sensitive hash of the gameplay state, for determinism checks.</summary>
    public ulong Hash()
    {
        var h = 1469598103934665603ul;
        void Mix(float f)
        {
            h ^= (uint)BitConverter.SingleToInt32Bits(f);
            h *= 1099511628211ul;
        }
        Mix(BallPosition.X);
        Mix(BallPosition.Y);
        Mix(BallPosition.Z);
        Mix(BallVelocity.X);
        Mix(BallVelocity.Y);
        Mix(BallVelocity.Z);
        Mix(CameraYaw);
        Mix(TimeLeft);
        foreach (var taken in CoinTaken)
            Mix(taken ? 1 : 0);
        h ^= (ulong)Score;
        h ^= (ulong)Ticks << 32;
        return h;
    }

    public void Dispose() => _scene.Dispose();
}
