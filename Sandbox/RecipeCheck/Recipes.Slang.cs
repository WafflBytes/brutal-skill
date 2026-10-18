// Recipe 7.40: Slang. Global session, a session targeting SPIR-V, a compute shader compiled from a source string to
// SPIR-V (checked by its header words), diagnostics for a broken shader, and two binding bugs: B11 (every ISlangBlob
// reads as empty) and B12 (ISessionExtensions.CreateCompositeComponentType crashes). Headless.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.SlangApi;

static unsafe partial class Recipes
{
    public static void SlangRecipe()
    {
        var global = Slang.CreateGlobalSession();                    // throws SlangException on failure
        Console.WriteLine($"     slang build tag: {global.GetBuildTag()}");

        ProfileID profile;
        fixed (byte* p = "spirv_1_5"u8)                              // byte* APIs: u8 literals end in a hidden NUL
            profile = global.FindProfile(p);
        Check(profile != ProfileID.Unknown, "FindProfile(\"spirv_1_5\") knows the profile");

        var target = new TargetDesc
        {
            StructureSize = (ByteSize64)sizeof(TargetDesc),            // no constructor sets it
            Format = CompileTarget.Spirv,
            Profile = profile,
        };
        var desc = new SessionDesc { StructureSize = (ByteSize64)sizeof(SessionDesc), Targets = &target, TargetCount = 1 };
        global.CreateSession(ref desc, out var session).ThrowIfFailure();

        const string source = """
            RWStructuredBuffer<float> values;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID) { values[id.x] = values[id.x] * 2.0; }
            """;
        var module = session.LoadModuleFromSourceString("doubler", "doubler.slang", source, out var loadDiag);
        Check(module != null, $"LoadModuleFromSourceString compiled the module ({DiagText(loadDiag)})");

        IEntryPoint entryPoint;
        fixed (byte* name = "main"u8)
            module!.FindEntryPointByName(name, out entryPoint).ThrowIfFailure();

        // Link the entry point on its own: it pulls in its module. (The composite route crashes, B12.)
        entryPoint.Link(out var linked, out _).ThrowIfFailure();
        linked.GetEntryPointCode(0, 0, out var code, out _).ThrowIfFailure();

        // B11 (Windows): the binding declares GetBufferSize as returning the ByteSize64 struct. MSVC member functions return
        // a struct through a hidden pointer, Slang returns size_t in RAX, so it reads 0 and AsSpan() is empty. The
        // System V ABI returns an 8-byte struct in RAX, so on Linux it works.
        var spirv = BlobBytes(code);                                 // workaround: the size straight from the vtable
        if (OperatingSystem.IsWindows())
            Check(code.AsSpan().Length == 0 && code.GetBufferSize().Value == 0,
                "B11 still holds: ISlangBlob.GetBufferSize()/AsSpan() read 0 bytes (if this fails, upstream fixed it: update B11)");
        else
            Check(code.AsSpan().Length == spirv.Length, "off Windows, ISlangBlob.AsSpan() reads the whole blob (B11 is Windows-only)");
        Console.WriteLine($"     SPIR-V: {spirv.Length} bytes, magic 0x{BinaryPrimitives.ReadUInt32LittleEndian(spirv):X8}");
        Check(spirv.Length > 20 && spirv.Length % 4 == 0, "the entry point code is a whole number of SPIR-V words");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(spirv) == 0x07230203, "it starts with the SPIR-V magic number");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(spirv[4..]) == 0x00010500, "the SPIR-V version word is 1.5, as profiled");

        // A broken shader: no exception, a null module and readable diagnostics (through the workaround).
        var broken = session.LoadModuleFromSourceString("broken", "broken.slang", "void main( { }", out var badDiag);
        var badText = DiagText(badDiag);
        Console.WriteLine($"     broken shader diagnostics: {badText.Split('\n')[0]}");
        Check(broken == null, "a syntax error gives a null module, not an exception");
        Check(badText.Contains("broken.slang") && badText.Contains("error"), "the diagnostics blob names the file and the error");

        // B12: the composite helper passes managed wrappers (GetComInstance) where Slang needs its own objects, and the
        // process dies with an access violation, so it runs in a child process.
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--slang-composite-child"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        _ = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(60_000)) { child.Kill(true); throw new TimeoutException("slang composite child hung"); }
        Console.WriteLine($"     composite child exit code: {child.ExitCode}");
        Check(child.ExitCode != 0 && child.ExitCode != 42, "B12 still holds: CreateCompositeComponentType crashes the process (if this fails, upstream fixed it: update B12)");
    }

    // B11 workaround: read the blob through its vtable (IUnknown x3, then getBufferPointer, getBufferSize).
    static ReadOnlySpan<byte> BlobBytes(ISlangBlob? blob)
    {
        if (blob == null || !ComWrappers.TryGetComInstance(blob, out var unknown))
            return default;
        var iid = typeof(ISlangBlob).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        Marshal.Release(unknown);
        var vtable = *(void***)native;
        var data = ((delegate* unmanaged[MemberFunction]<nint, void*>)vtable[3])(native);
        var size = ((delegate* unmanaged[MemberFunction]<nint, nuint>)vtable[4])(native);
        Marshal.Release(native);                                     // the managed proxy still holds the blob
        return new ReadOnlySpan<byte>(data, checked((int)size));
    }

    static string DiagText(ISlangBlob? blob) =>
        blob == null ? "no diagnostics" : System.Text.Encoding.UTF8.GetString(BlobBytes(blob)).TrimEnd('\0', '\n', '\r');

    // Child process for B12. Exit 42 = the composite was created (bug fixed); a crash gives the OS's code.
    public static int SlangCompositeChild()
    {
        var global = Slang.CreateGlobalSession();
        var target = new TargetDesc { StructureSize = (ByteSize64)sizeof(TargetDesc), Format = CompileTarget.Spirv };
        var desc = new SessionDesc { StructureSize = (ByteSize64)sizeof(SessionDesc), Targets = &target, TargetCount = 1 };
        global.CreateSession(ref desc, out var session).ThrowIfFailure();
        var module = session.LoadModuleFromSourceString("m", "m.slang",
            "[shader(\"compute\")][numthreads(1,1,1)] void main() {}", out _)!;
        IEntryPoint entryPoint;
        fixed (byte* name = "main"u8)
            module.FindEntryPointByName(name, out entryPoint).ThrowIfFailure();
        session.CreateCompositeComponentType([module, entryPoint], out _);
        return 42;
    }
}
