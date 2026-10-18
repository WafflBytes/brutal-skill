using System.Runtime.InteropServices;
using Brutal.RakNetApi;
using NetStream = Brutal.RakNetApi.Utility.Stream; // ImplicitUsings also imports System.IO.Stream (CS0104)

namespace NetSession;

/// <summary>
/// The wire format. The first byte of every RakNet packet is its id; RakNet's own ids come first and the game's
/// start at UserPacketEnum (134). Payloads are flat unmanaged structs written with RakNet's Stream helper.
/// </summary>
static class Protocol
{
    /// <summary>Bump when a message layout or a rule both ends must agree on changes.</summary>
    public static ushort Version = 1;

    public static readonly byte Hello = (byte)DefaultMessageIDTypes.UserPacketEnum; // client -> host, reliable
    public static readonly byte Welcome = (byte)(Hello + 1); // host -> client, reliable
    public static readonly byte Reject = (byte)(Hello + 2); // host -> client, reliable, then the host disconnects
    public static readonly byte State = (byte)(Hello + 3); // host -> client, every tick, unreliable sequenced
    public static readonly byte Input = (byte)(Hello + 4); // client -> host, every tick, unreliable sequenced

    public const int MaxMessage = 64;

    public static ReadOnlySpan<byte> Write<T>(Span<byte> buffer, byte id, in T payload)
        where T : unmanaged
    {
        var w = new NetStream(buffer); // ref struct
        w.Write(id);
        w.Write(payload);
        return w.Written();
    }

    /// <summary>Reads the payload after the id byte. False when the message is too short (a bad or foreign packet).</summary>
    public static unsafe bool TryRead<T>(ReadOnlySpan<byte> message, out T payload)
        where T : unmanaged
    {
        payload = default;
        if (message.Length < 1 + sizeof(T))
            return false;
        Span<byte> copy = stackalloc byte[1 + sizeof(T)]; // NetStream wants a Span: never write into packet memory
        message[..copy.Length].CopyTo(copy);
        var r = new NetStream(copy);
        r.ReadMessageId();
        payload = r.Read<T>();
        return true;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct HelloMsg { public ushort Version; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct WelcomeMsg { public ushort Version; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct RejectMsg { public ushort HostVersion; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct InputMsg
{
    public uint Tick; // the client's own counter: lets the host drop packets that arrive out of order
    public float PaddleY;
}

/// <summary>Everything a client needs to draw one frame. Small and flat, so it is sent whole every tick.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct WorldState
{
    public uint Tick;
    public float BallX, BallY, BallVx, BallVy;
    public float LeftY, RightY;
}

/// <summary>The host's simulation: a ball bouncing in a box plus two paddle positions. Deterministic, no network types.</summary>
sealed class World
{
    public const float Dt = 1f / 60f;
    public const float Width = 100, Height = 60, PaddleMin = 5, PaddleMax = 55;
    public WorldState State = new() { BallX = 50, BallY = 30, BallVx = 37, BallVy = 23, LeftY = 30, RightY = 30 };

    public static float ClampPaddle(float y) => float.IsFinite(y) ? Math.Clamp(y, PaddleMin, PaddleMax) : 30; // never trust the wire

    public void Step(float leftY, float rightY)
    {
        ref var s = ref State;
        s.Tick++;
        s.LeftY = ClampPaddle(leftY);
        s.RightY = ClampPaddle(rightY);
        s.BallX += s.BallVx * Dt;
        s.BallY += s.BallVy * Dt;
        if (s.BallX < 0 || s.BallX > Width) { s.BallVx = -s.BallVx; s.BallX = Math.Clamp(s.BallX, 0, Width); }
        if (s.BallY < 0 || s.BallY > Height) { s.BallVy = -s.BallVy; s.BallY = Math.Clamp(s.BallY, 0, Height); }
    }
}

/// <summary>The game's view of a connection. Both sides log what happens so a test can read it.</summary>
abstract class Session : INetListener, IDisposable
{
    protected readonly NetPeer Net;
    protected Session(NetPeer net) => Net = net;

    public bool Playing { get; protected set; }
    public bool Ended { get; protected set; }

    public void Step(Action<string> log, float paddleY)
    {
        Net.Poll(this);
        Tick(log, paddleY);
    }

    protected Action<string> Log = _ => { };
    protected abstract void Tick(Action<string> log, float paddleY);
    public abstract void OnConnected();
    public abstract void OnDisconnected(bool graceful);
    public virtual void OnConnectFailed(string reason) { }
    public abstract void OnMessage(ReadOnlySpan<byte> message);
    public void Dispose() => Net.Close();
    public void Crash() => Net.Close(sayGoodbye: false); // vanish without a goodbye (the other side times out)
    public int Ping => Net.Ping;
}

/// <summary>Owns the authoritative <see cref="World"/>, applies the client's input, streams state every tick.</summary>
sealed class HostSession : Session
{
    readonly World _world = new();
    float _remoteY = 30;
    uint _remoteTick;
    bool _present;

    public HostSession(ushort port) : base(NetPeer.Host(port, "127.0.0.1")) { }

    public bool OpponentWasHere { get; private set; }
    public WorldState State => _world.State;

    protected override void Tick(Action<string> log, float paddleY)
    {
        Log = log;
        _world.Step(paddleY, _remoteY);
        if (!_present)
            return;
        Span<byte> buffer = stackalloc byte[Protocol.MaxMessage];
        Net.Send(Protocol.Write(buffer, Protocol.State, _world.State), reliable: false);
    }

    public override void OnConnected() { }

    public override void OnDisconnected(bool graceful)
    {
        if (!_present)
            return;
        _present = false;
        Playing = false;
        Ended = true;
        Log(graceful ? "opponent left: goodbye" : "opponent left: lost connection");
    }

    public override void OnMessage(ReadOnlySpan<byte> message)
    {
        Span<byte> buffer = stackalloc byte[Protocol.MaxMessage];
        var id = message[0];
        if (id == Protocol.Hello && Protocol.TryRead<HelloMsg>(message, out var hello))
        {
            if (hello.Version != Protocol.Version)
            {
                Net.Send(Protocol.Write(buffer, Protocol.Reject, new RejectMsg { HostVersion = Protocol.Version }), reliable: true);
                Net.CloseRemote();
                Log($"turned away version {hello.Version}");
                return;
            }
            Net.Send(Protocol.Write(buffer, Protocol.Welcome, new WelcomeMsg { Version = Protocol.Version }), reliable: true);
            _present = OpponentWasHere = Playing = true;
            _remoteTick = 0;
            Log("opponent joined");
        }
        else if (id == Protocol.Input && _present && Protocol.TryRead<InputMsg>(message, out var input))
        {
            if (input.Tick <= _remoteTick)
                return; // older than what we have: unreliable-sequenced already drops these, but never rely on one layer
            _remoteTick = input.Tick;
            _remoteY = World.ClampPaddle(input.PaddleY);
        }
    }
}

/// <summary>Sends its paddle, draws the host's state. <see cref="View"/> is what a renderer would draw each frame.</summary>
sealed class ClientSession : Session
{
    const int MaxExtrapolationSteps = 8; // cover a few late snapshots, never more: the ball would run through a wall

    WorldState _last;
    float _clock; // the client's estimate of the host's tick: +1 per frame, pulled forward by snapshots, never back
    uint _tick;
    readonly int _dropEvery; // simulate packet loss: ignore every Nth snapshot
    public readonly Queue<float> RecentSent = new();
    public int Snapshots, Dropped;

    public ClientSession(string host, ushort port, int dropEvery) : base(NetPeer.Join(host, port, "127.0.0.1")) => _dropEvery = dropEvery;

    public bool HasState { get; private set; }
    public WorldState Latest => _last;
    public string FailReason { get; private set; } = "";

    /// <summary>Snapshot-only view: holds the last state until the next one arrives (stutters when one is late or lost).</summary>
    public (float X, float Y) RawBall => (_last.BallX, _last.BallY);

    /// <summary>
    /// Smoothed view: the newest snapshot carried on along its velocity (dead reckoning) by its real age, bounded and
    /// clamped. The age comes from tick numbers, so a late snapshot isn't mistaken for a fresh one (which would stall
    /// the ball for a frame, because the new position equals what the last frame already showed).
    /// </summary>
    public (float X, float Y) SmoothBall
    {
        get
        {
            var t = Math.Clamp(_clock - _last.Tick, 0, MaxExtrapolationSteps) * World.Dt;
            return (Math.Clamp(_last.BallX + _last.BallVx * t, 0, World.Width), Math.Clamp(_last.BallY + _last.BallVy * t, 0, World.Height));
        }
    }

    protected override void Tick(Action<string> log, float paddleY)
    {
        Log = log;
        _clock++;
        if (!Playing)
            return;
        Span<byte> buffer = stackalloc byte[Protocol.MaxMessage];
        Net.Send(Protocol.Write(buffer, Protocol.Input, new InputMsg { Tick = ++_tick, PaddleY = paddleY }), reliable: false);
        RecentSent.Enqueue(paddleY);
        while (RecentSent.Count > 30)
            RecentSent.Dequeue();
    }

    public override void OnConnected()
    {
        Span<byte> buffer = stackalloc byte[Protocol.MaxMessage];
        Net.Send(Protocol.Write(buffer, Protocol.Hello, new HelloMsg { Version = Protocol.Version }), reliable: true);
    }

    public override void OnDisconnected(bool graceful)
    {
        if (Ended)
            return;
        Ended = true;
        Playing = false;
        Log(graceful ? "host left: goodbye" : "host left: lost connection");
    }

    public override void OnConnectFailed(string reason)
    {
        Ended = true;
        FailReason = reason;
        Log($"connect failed: {reason}");
    }

    public override void OnMessage(ReadOnlySpan<byte> message)
    {
        var id = message[0];
        if (id == Protocol.Welcome && Protocol.TryRead<WelcomeMsg>(message, out _))
        {
            Playing = true;
            Log("joined");
        }
        else if (id == Protocol.Reject && Protocol.TryRead<RejectMsg>(message, out var reject))
        {
            Ended = true;
            Log($"rejected: host version {reject.HostVersion}");
        }
        else if (id == Protocol.State && Protocol.TryRead<WorldState>(message, out var state))
        {
            if (HasState && state.Tick <= _last.Tick)
                return;
            if (_dropEvery > 0 && state.Tick % _dropEvery == 0)
            {
                Dropped++; // pretend the network lost it
                return;
            }
            _clock = HasState ? Math.Max(_clock, state.Tick) : state.Tick; // first snapshot starts the clock
            _last = state;
            HasState = true;
            Snapshots++;
        }
    }
}
