# Networking, terminal UI, file dialogs: RakNet, NotCurses, NativeFileDialog

Read this for multiplayer networking (RakNet), terminal games (NotCurses) or native open/save dialogs.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is `traps.md`.

## Packages

Column key: **H** = headless (no window or GPU needed); **Gen** = generated bindings (`*.gen.cs`), **Hand** = hand-written, **Manual** = hand-written ergonomics over generated code.

| Package | Namespace | Native (win-x64) | H | Gen | Notes |
|---|---|---|---|---|---|
| RakNet | `Brutal.RakNetApi` | RakNetDLL.dll (4.081) | **yes** | Gen+Manual | One peer API for client and server; `Stream` serializer |
| NotCurses | `Brutal.NotCursesApi` | notcurses-core/-ffi, ncursesw, unistring, winpthread | terminal | Gen | Terminal UI incl. pixel blitters; Windows support is experimental |
| NativeFileDialog | `Brutal.NativeFileDialogApi` | nfd.dll | no | Hand | |

## RakNet

### 7.23 RakNet client and server [RUN over 127.0.0.1]
Namespace `Brutal.RakNetApi`. The same peer type serves both sides. RakNet runs its own network thread, and `Receive` drains its queue.
```csharp
using NetStream = Brutal.RakNetApi.Utility.Stream;   // ImplicitUsings also imports System.IO.Stream (CS0104)

using var server = RakNetLibrary.CreateInstance();   // Dispose = DestroyInstance
Span<SocketDescriptor> sd = [new SocketDescriptor(port)];
if (server.Startup(4, sd) != StartupResult.RaknetStarted) ...   // SocketPortAlreadyInUse if taken
server.SetMaximumIncomingConnections(4);              // without this, every client is refused

using var client = RakNetLibrary.CreateInstance();
Span<SocketDescriptor> any = [new SocketDescriptor()];  // port 0: the OS picks one
client.Startup(1, any);
client.Connect("127.0.0.1"u8, port, default, null);  // host is read as a C string; u8 literals end in a hidden NUL

for (var p = peer.Receive(); p != null; peer.DeallocatePacket(p), p = peer.Receive())
{
    switch (p->Data[0])                               // the first byte is the message id
    {
        case (byte)DefaultMessageIDTypes.NewIncomingConnection: break;      // server side
        case (byte)DefaultMessageIDTypes.ConnectionRequestAccepted: break;  // client side
        case MsgPing:
            var read = new NetStream(new Span<byte>(p->Data, (int)p->Length));
            read.ReadMessageId(); var value = read.Read<int>();
            break;
    }
}
// Your own ids start at (byte)DefaultMessageIDTypes.UserPacketEnum (134).
var write = new NetStream(buffer);                    // ref struct: pass it by ref, or it gets copied
write.Write(MsgPing); write.Write(21);
peer.Send(write.Written(), PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, AddressOrGUID.From(p->Guid), false);
peer.Shutdown(300);                                   // ms to let the disconnect notification go out
```
- Measured: connecting plus one round trip takes about 90 ms on loopback.
- A real server binds all interfaces, so Windows Firewall will ask the player the first time [SRC]. Mention it in the player README.
### 7.28 RakNet: full servers, failed connects, leaving, crashing [RUN over 127.0.0.1]
Checked by `RecipeCheck 7.28`.
```csharp
server.SetTimeoutTime(4000, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS); // right after Startup, the same on BOTH ends (T44)
client.SetTimeoutTime(4000, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);
var host = Encoding.UTF8.GetBytes(typedAddress + "\0");              // a typed IP: Connect reads a C string, add the NUL yourself
client.Connect(host, port, default, null, 0, 4, 250);                // optional: 4 tries x 250 ms (default 12 x 500 ms)
```
Message ids to handle (`p->Data[0]`, as `(byte)DefaultMessageIDTypes.X`):

| Id | Who gets it | When |
|---|---|---|
| `ConnectionRequestAccepted` | client | connected |
| `NewIncomingConnection` | server | a client connected |
| `NoFreeIncomingConnections` | client | the server is full (`SetMaximumIncomingConnections` reached) |
| `ConnectionAttemptFailed` | client | nobody answered after all the retries (about 6 s by default) |
| `DisconnectionNotification` | the other side | a polite leave: `Shutdown(ms > 0)` or `CloseConnection(guid, true)` |
| `ConnectionLost` | the other side | the peer vanished (crash, cable): only after its reliable messages time out (T44) |

- `Shutdown(0)` sends no goodbye, so it's a handy way to simulate a crash in tests.
- `GetAveragePing(AddressOrGUID.From(guid))` returns ms (12 ms on loopback).
- **Testing two players on one PC:** bind with `new SocketDescriptor("127.0.0.1", port)` so Windows Firewall doesn't prompt, run two processes (`--host`, `--join 127.0.0.1`), give each test its own port range, and drive both with bots plus a log. Real players need the default all-interfaces bind.
- Fast-changing state (positions) can go `PacketReliability.UnreliableSequenced`; scores and events `ReliableOrdered`. Put them on different ordering channels (`orderingChannel` 0, 1, ...) so one doesn't hold up the other [SRC].
- Don't pause an online game on focus loss: the other player is still playing.

### 7.33 A host-authoritative online game: protocol, smoothing, two-process test [RUN over 127.0.0.1]
Runnable copy: `Sandbox/NetSession` (about 600 lines in three files; `NetPeer.cs` is the reusable peer wrapper, `Game.cs` the protocol, a tiny simulation and the two session classes, `Program.cs` the driver). It is distilled from the 2026-10-18 Pong eval, whose complete game is outside the repo.
- **Layers:** `NetPeer` (one RakNet peer, one remote player, `Poll(listener)` once per fixed step, `Send(bytes, reliable)`, `Close(sayGoodbye)`) under `HostSession`/`ClientSession` (game rules, no RakNet types) under the simulation (`World`, no network types, so it is testable headless and identical on both ends).
- **Roles:** the host owns the simulation, applies the client's input and sends a **snapshot every tick**; the client sends its **input every tick** and draws. The client never simulates the shared state, so it can't disagree with the host. Both ends run fixed 1/60 s steps (`Poll`, apply, send).
- **Messages** (ids start at `UserPacketEnum`; payloads are flat `[StructLayout(Sequential, Pack = 1)]` structs written through `NetStream`, as in 7.23):

| Message | Direction | Delivery |
|---|---|---|
| `Hello { Version }` (sent on `ConnectionRequestAccepted`) | client to host | `ReliableOrdered`, channel 0 |
| `Welcome` / `Reject { HostVersion }` | host to client | `ReliableOrdered`, channel 0 |
| `State { Tick, ball, paddles }` | host to client, every tick | `UnreliableSequenced`, channel 1 |
| `Input { Tick, PaddleY }` | client to host, every tick | `UnreliableSequenced`, channel 1 |

  A lost snapshot is replaced by the next one a tick later, so only the handshake needs reliability. `UnreliableSequenced` already drops a packet that arrives after a newer one; still compare `Tick` yourself and ignore anything not newer [RUN: both ends do]. Whether the two channels really keep a reliable message from delaying state wasn't measured [SRC].
- **Never trust the wire:** `TryRead<T>` rejects short packets and copies to the stack before `NetStream` reads; clamp every received value (`ClampPaddle`, with `float.IsFinite`) before it touches the simulation; check the protocol version in `Hello`, and answer a mismatch with `Reject` then `CloseRemote()` [RUN]. `SetMaximumIncomingConnections(1)` makes a second player get `NoFreeIncomingConnections`, which the client reports as "That host already has an opponent" [RUN].
- **Client smoothing without hiding the truth:** keep a client clock that adds 1 per frame and is pulled forward to each snapshot's `Tick`, never back. Draw the newest snapshot advanced by `clamp(clock - snapshot.Tick, 0, 8)` steps along its velocity (dead reckoning), clamped to the court. With 1 snapshot in 3 lost, the raw view (hold the last snapshot) stalled on 83 to 94 of about 234 frames and the smoothed view on 0 [RUN, three runs]. **Don't count frames since the last snapshot instead:** a late snapshot is then treated as fresh and lands exactly where the previous frame already drew the ball, a one-frame stall (19 of 236 frames in this tool's first version). The 8-step cap stops the ball running through a wall during a long outage.
- **Not in the tool, but used by the Pong eval [SRC]:** move the player's own paddle locally with no wait for the host (client-side prediction of the player's own object only) and take every other object from snapshots; keep every event between snapshots (several can arrive in one step) so sounds aren't dropped.
- **Disconnects** (the host log lines the test checks): a polite `Close()` gives `DisconnectionNotification` (`opponent left: goodbye`); a killed process gives `ConnectionLost` only after the timeout, measured 1515 to 1548 ms with `SetTimeoutTime(1500)` on both ends while the host kept sending state (T44). Use a few seconds in a real game.
- **The two-process test:** run with no arguments, the tool starts itself with `--host <port>` and `--join <port> [--ticks N] [--drop-every K] [--version V]`. Bots drive the input, each process prints a line per event plus the state of every 10th tick, and the driver reads the lines and checks them: join, input to simulation to snapshot round trip (`echo=ok`), host and client agree on every shared tick, smoothing, `Kill()` as a real crash, version mismatch, a full host. Give each scenario its own port, bind `127.0.0.1` so the firewall stays quiet, and use `ProcessStartInfo.ArgumentList` (it quotes paths with spaces for you). Loopback ping reads 0 ms.
- **Not covered:** lag compensation, interpolation buffers, more than two players, NAT traversal and relays, encryption, cheating beyond input clamping.
- A `brutal-net` template doesn't exist; to start a game, copy `NetPeer.cs` and the shape of `Game.cs`.

## Traps

| ID | Trap | Fix |
|---|---|---|
| T44 | **A crashed or unplugged peer is noticed only when a reliable message to it can't be delivered.** If you send it nothing reliable, you never get `ConnectionLost`. The timeout defaults to 10 s (30 s in RakNet debug builds), and RakNet requires the same value on both ends [RUN: 1.2 s with a 1.5 s timeout while sending reliable state] | Set `SetTimeoutTime` on both peers right after `Startup`, and keep sending reliable state or a heartbeat |

## NotCurses

**NotCurses** [SRC]:
- Text: `NotCursesHandle.Init(opts)`, `nc.StdPlane()`, `plane.PutStr(new int2(x, y), "text"u8)` (takes `RefString8`), `nc.Render()`.
- Input: `nc.GetNblock(out NcInput)`.
- Pixels: `NcVisualHandle.FromRgba(span, rows, cols)` + `nc.Blit(visual, new NcVisualOptions { Blitter = ... })`.

## NativeFileDialog

**NativeFileDialog** [DOC]. Then check `r == NativeFileDialog.Result.Okay`.
```csharp
var path = NativeFileDialog.OpenDialog(new FileDialogInfo { Filters = [new FilterItem { Name = "Images", Spec = "png,jpg" }] }, out var r);
```

glTF loading is in `assets.md` (7.25, [RUN]).
