# Networking, terminal UI, file dialogs: RakNet, NotCurses, NativeFileDialog

Read this for multiplayer networking (RakNet), terminal games (NotCurses) or native open/save dialogs.

Paths (`BRUTAL/`, `Sandbox/`) are relative to the BRUTAL home folder named in `SKILL.md`. Trap (`T#`) and bug (`B#`) IDs are global; the index is in `SKILL.md`.

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

**glTF** [DOC]. `LightData` is nullable.
```csharp
var model = GltfUtility.LoadModel("m.glb");
var buf = model.LoadBinaryBuffer(0, "m.glb");
```
