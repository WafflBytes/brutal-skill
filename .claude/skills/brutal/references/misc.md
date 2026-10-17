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

**RakNet** [SRC]:
```csharp
var peer = RakNetLibrary.CreateInstance();
peer.Startup(max, [new SocketDescriptor(port)]);
peer.SetMaximumIncomingConnections(n);    // server
peer.Connect("host"u8, port, pwd, null);  // client
Packet* p = peer.Receive();               // then peer.DeallocatePacket(p)
```

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
