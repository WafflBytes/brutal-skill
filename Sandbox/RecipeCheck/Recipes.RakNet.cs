// Recipe 7.23: RakNet client and server in one process over 127.0.0.1. Headless; uses a local UDP port.
using System.Diagnostics;
using Brutal.RakNetApi;
using NetStream = Brutal.RakNetApi.Utility.Stream; // ImplicitUsings brings System.IO.Stream too

static unsafe partial class Recipes
{
    public static void RakNetRecipe()
    {
        // Message ids: RakNet's own ids come first; the game's start at UserPacketEnum (134 in RakNet 4.081).
        Check((int)DefaultMessageIDTypes.UserPacketEnum == 134, $"UserPacketEnum = {(int)DefaultMessageIDTypes.UserPacketEnum}");
        var msgPing = (byte)DefaultMessageIDTypes.UserPacketEnum;
        var msgPong = (byte)(msgPing + 1);

        using var server = RakNetLibrary.CreateInstance(); // Dispose = DestroyInstance
        using var client = RakNetLibrary.CreateInstance();

        ushort port = 0;
        for (ushort p = 61234; p < 61244 && port == 0; p++) // first free port in a small range
        {
            Span<SocketDescriptor> sd = [new SocketDescriptor(p)];
            var r = server.Startup(4, sd);
            if (r == StartupResult.RaknetStarted)
                port = p;
            else if (r != StartupResult.SocketPortAlreadyInUse && r != StartupResult.SocketFailedToBind)
                throw new InvalidOperationException($"server Startup: {r}");
        }
        Check(port != 0, "server bound a port");
        server.SetMaximumIncomingConnections(4); // without this the server refuses every client

        Span<SocketDescriptor> any = [new SocketDescriptor()]; // port 0: the OS picks one
        Check(client.Startup(1, any) == StartupResult.RaknetStarted, "client Startup");
        // host is read as a C string: u8 literals carry a hidden trailing NUL, so "..."u8 is safe here.
        var attempt = client.Connect("127.0.0.1"u8, port, default, null);
        Check(attempt == ConnectionAttemptResult.ConnectionAttemptStarted, $"Connect: {attempt}");

        bool serverSawClient = false, clientConnected = false, serverGotPing = false, clientGotPong = false;
        var pongValue = 0;
        Span<byte> buffer = stackalloc byte[64];
        var sw = Stopwatch.StartNew();
        while (!clientGotPong && sw.ElapsedMilliseconds < 5000)
        {
            for (var p = server.Receive(); p != null; server.DeallocatePacket(p), p = server.Receive())
            {
                var id = p->Data[0];
                if (id == (byte)DefaultMessageIDTypes.NewIncomingConnection)
                    serverSawClient = true;
                else if (id == msgPing)
                {
                    var read = new NetStream(new Span<byte>(p->Data, (int)p->Length));
                    read.ReadMessageId();
                    var value = read.Read<int>();
                    serverGotPing = true;
                    var write = new NetStream(buffer);
                    write.Write(msgPong);
                    write.Write(value * 2);
                    server.Send(write.Written(), PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, AddressOrGUID.From(p->Guid), false);
                }
            }
            for (var p = client.Receive(); p != null; client.DeallocatePacket(p), p = client.Receive())
            {
                var id = p->Data[0];
                if (id == (byte)DefaultMessageIDTypes.ConnectionRequestAccepted)
                {
                    clientConnected = true;
                    var write = new NetStream(buffer);
                    write.Write(msgPing);
                    write.Write(21);
                    client.Send(write.Written(), PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, AddressOrGUID.From(p->Guid), false);
                }
                else if (id == msgPong)
                {
                    var read = new NetStream(new Span<byte>(p->Data, (int)p->Length));
                    read.ReadMessageId();
                    pongValue = read.Read<int>();
                    clientGotPong = true;
                }
            }
            Thread.Sleep(5); // RakNet works on its own thread; Receive just drains its queue
        }
        Console.WriteLine($"     RakNet loopback on port {port}: connected in time, round trip done after {sw.ElapsedMilliseconds} ms");
        Check(clientConnected && serverSawClient, "client got ConnectionRequestAccepted, server got NewIncomingConnection");
        Check(serverGotPing && clientGotPong && pongValue == 42, $"ping 21 -> pong {pongValue}");
        Check(server.NumberOfConnections() == 1, $"server has 1 connection ({server.NumberOfConnections()})");

        client.Shutdown(300); // blockDuration ms: lets the disconnect notification go out
        server.Shutdown(0);
    }
}
