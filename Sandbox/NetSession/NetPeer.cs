using System.Text;
using Brutal.RakNetApi;

namespace NetSession;

/// <summary>What the game hears from the network, in the order packets arrive.</summary>
public interface INetListener
{
    /// <summary>Host: a player connected. Client: the host accepted our connection.</summary>
    void OnConnected();

    /// <summary>The other side left. <paramref name="graceful"/> = it said goodbye; false = it timed out.</summary>
    void OnDisconnected(bool graceful);

    /// <summary>Client only: the connection attempt failed; <paramref name="reason"/> is for the player.</summary>
    void OnConnectFailed(string reason);

    /// <summary>A game message (first byte is its id).</summary>
    void OnMessage(ReadOnlySpan<byte> message);
}

/// <summary>
/// One RakNet peer with at most one remote player: the host listens for one opponent, the client connects to the
/// host. RakNet runs its own network thread; <see cref="Poll"/> drains its queue on the caller's thread.
/// Owns the RakPeerInstance and destroys it exactly once (skill rule R5).
/// </summary>
public sealed unsafe class NetPeer : IDisposable
{
    /// <summary>
    /// Both ends use the same timeout (RakNet asks for that). A crashed or unplugged opponent is noticed after this.
    /// </summary>
    public const uint TimeoutMs = 1500; // a real game would use 4000 or more; short here so the crash test is quick

    RakPeerInstance _peer; // not readonly: Dispose() zeroes the handle in place (handles are values, T17)
    RakNetGUID _remote = RakNetGUID.UNASSIGNED_RAKNET_GUID;

    public bool IsHost { get; }
    public ushort Port { get; }
    public bool Connected => _remote != RakNetGUID.UNASSIGNED_RAKNET_GUID;

    NetPeer(RakPeerInstance peer, bool isHost, ushort port)
    {
        _peer = peer;
        IsHost = isHost;
        Port = port;
    }

    /// <summary>
    /// Listens for one opponent on <paramref name="port"/>. <paramref name="bindIp"/> "" = every interface (what a
    /// real host needs); "127.0.0.1" keeps it on this PC (tests). Throws with a player-readable message on failure.
    /// </summary>
    public static NetPeer Host(ushort port, string bindIp = "")
    {
        var peer = RakNetLibrary.CreateInstance();
        Span<SocketDescriptor> sd = [new SocketDescriptor(bindIp, port)];
        var r = peer.Startup(1, sd);
        if (r != StartupResult.RaknetStarted)
        {
            peer.Dispose();
            throw new InvalidOperationException(
                r is StartupResult.SocketPortAlreadyInUse or StartupResult.SocketFailedToBind
                    ? $"UDP port {port} is already in use. Is another copy of this game hosting on this PC? Try --port <other>."
                    : $"Couldn't start networking ({r})."
            );
        }
        peer.SetMaximumIncomingConnections(1); // without this every client is refused; 1 = a third player is turned away
        peer.SetTimeoutTime(TimeoutMs, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);
        return new NetPeer(peer, true, port);
    }

    /// <summary>Starts connecting to <paramref name="host"/> (an IP address or a name). Result arrives via Poll.</summary>
    public static NetPeer Join(string host, ushort port, string bindIp = "")
    {
        var peer = RakNetLibrary.CreateInstance();
        Span<SocketDescriptor> sd = [new SocketDescriptor(bindIp, 0)]; // port 0: the OS picks one
        var r = peer.Startup(1, sd);
        if (r != StartupResult.RaknetStarted)
        {
            peer.Dispose();
            throw new InvalidOperationException($"Couldn't start networking ({r}).");
        }
        peer.SetTimeoutTime(TimeoutMs, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);
        // RakNet reads the host as a C string, so it needs the NUL terminator (a "..."u8 literal has one hidden).
        var hostZ = Encoding.UTF8.GetBytes(host + "\0");
        var attempt = peer.Connect(hostZ, port, default, null, 0, 10, 500); // 10 tries, 0.5 s apart = 5 s
        if (attempt != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            peer.Dispose();
            throw new InvalidOperationException(
                attempt == ConnectionAttemptResult.CannotResolveDomainName
                    ? $"Can't find a computer called \"{host}\"."
                    : $"Couldn't connect to {host} ({attempt})."
            );
        }
        return new NetPeer(peer, false, port);
    }

    /// <summary>Handles every packet waiting in RakNet's queue. Call once per fixed step.</summary>
    public void Poll(INetListener listener)
    {
        for (var p = _peer.Receive(); p != null; _peer.DeallocatePacket(p), p = _peer.Receive())
        {
            if (p->Length == 0)
                continue;
            var data = new ReadOnlySpan<byte>(p->Data, (int)p->Length);
            var id = data[0];
            switch (id)
            {
                case (byte)DefaultMessageIDTypes.NewIncomingConnection: // host side
                    if (Connected) // can't happen with 1 incoming slot, but never let a second player in
                        _peer.CloseConnection(AddressOrGUID.From(p->Guid), true);
                    else
                    {
                        _remote = p->Guid;
                        listener.OnConnected();
                    }
                    break;
                case (byte)DefaultMessageIDTypes.ConnectionRequestAccepted: // client side
                    _remote = p->Guid;
                    listener.OnConnected();
                    break;
                case (byte)DefaultMessageIDTypes.DisconnectionNotification:
                case (byte)DefaultMessageIDTypes.ConnectionLost:
                    if (p->Guid == _remote)
                    {
                        _remote = RakNetGUID.UNASSIGNED_RAKNET_GUID;
                        listener.OnDisconnected(id == (byte)DefaultMessageIDTypes.DisconnectionNotification);
                    }
                    break;
                case (byte)DefaultMessageIDTypes.ConnectionAttemptFailed:
                    listener.OnConnectFailed("No answer from the host. Check the IP address, that the host is waiting, and their firewall / port forwarding.");
                    break;
                case (byte)DefaultMessageIDTypes.NoFreeIncomingConnections:
                    listener.OnConnectFailed("That host already has an opponent.");
                    break;
                case (byte)DefaultMessageIDTypes.ConnectionBanned:
                case (byte)DefaultMessageIDTypes.InvalidPassword:
                case (byte)DefaultMessageIDTypes.IncompatibleProtocolVersion:
                case (byte)DefaultMessageIDTypes.AlreadyConnected:
                case (byte)DefaultMessageIDTypes.IpRecentlyConnected:
                    listener.OnConnectFailed($"The host refused the connection ({(DefaultMessageIDTypes)id}). Try again in a few seconds.");
                    break;
                default:
                    if (id >= (byte)DefaultMessageIDTypes.UserPacketEnum && p->Guid == _remote)
                        listener.OnMessage(data);
                    break;
            }
        }
    }

    /// <summary>Sends to the connected opponent; does nothing when nobody is connected.</summary>
    public void Send(ReadOnlySpan<byte> message, bool reliable)
    {
        if (!Connected)
            return;
        _peer.Send(
            message,
            reliable ? PacketPriority.HighPriority : PacketPriority.ImmediatePriority,
            reliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableSequenced,
            reliable ? (byte)0 : (byte)1, // separate ordering channels so state never waits behind a reliable message
            AddressOrGUID.From(_remote),
            false
        );
    }

    /// <summary>Drops the opponent (host: kick; client: leave), telling them so.</summary>
    public void CloseRemote()
    {
        if (!Connected)
            return;
        _peer.CloseConnection(AddressOrGUID.From(_remote), true);
        _remote = RakNetGUID.UNASSIGNED_RAKNET_GUID;
    }

    /// <summary>Round-trip time to the opponent in ms, or -1.</summary>
    public int Ping => Connected ? _peer.GetAveragePing(AddressOrGUID.From(_remote)) : -1;

    /// <summary>
    /// <paramref name="sayGoodbye"/> true waits up to 300 ms so the "disconnected" notice reaches the other side;
    /// false vanishes silently (used by tests to simulate a crash: the other side sees a timeout).
    /// </summary>
    public void Close(bool sayGoodbye = true)
    {
        if (_peer.IsNull())
            return;
        _peer.Shutdown(sayGoodbye ? 300u : 0u);
        _peer.Dispose(); // DestroyInstance
        _remote = RakNetGUID.UNASSIGNED_RAKNET_GUID;
    }

    public void Dispose() => Close();
}
