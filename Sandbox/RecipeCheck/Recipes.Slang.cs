// Recipe 7.40: Slang (Ahwoo.BRUTAL.Slang 2026.10.3+). Global session, a session targeting SPIR-V, a compute shader
// compiled from a source string to SPIR-V (checked by its header words) through a composite of module + entry point,
// diagnostics for a broken shader, and explicit Slang.Release of every proxy.
// B11 (empty blobs on Windows) and B12 (composite crash) were fixed in 2026.10.3; this checks they stay fixed. Headless.
using System.Buffers.Binary;
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

        var target = new TargetDesc { StructureSize = ByteSize.Of<TargetDesc>(), Format = CompileTarget.Spirv, Profile = profile };
        var desc = new SessionDesc { StructureSize = ByteSize.Of<SessionDesc>(), Targets = &target, TargetCount = 1 }; // no constructor sets StructureSize
        global.CreateSession(ref desc, out var session).ThrowIfFailure();

        const string source = """
            RWStructuredBuffer<float> values;
            [shader("compute")]
            [numthreads(64, 2, 1)]
            void main(uint3 id : SV_DispatchThreadID) { values[id.x] = values[id.x] * 2.0; }
            """;
        var module = session.LoadModuleFromSourceString("doubler", "doubler.slang", source, out var loadDiag);
        Check(module != null, $"LoadModuleFromSourceString compiled the module ({DiagText(loadDiag)})");

        IEntryPoint entryPoint;
        fixed (byte* name = "main"u8)
            module!.FindEntryPointByName(name, out entryPoint).ThrowIfFailure();

        var program = session.CreateCompositeComponentType([module, entryPoint], out var composeDiag); // crashed before 2026.10.3 (B12)
        program.Link(out var linked, out var linkDiag).ThrowIfFailure();
        linked.GetEntryPointCode(0, 0, out var code, out var codeDiag).ThrowIfFailure();

        var spirv = code.AsSpan();                                   // read 0 bytes on Windows before 2026.10.3 (B11)
        Console.WriteLine($"     SPIR-V: {spirv.Length} bytes, magic 0x{BinaryPrimitives.ReadUInt32LittleEndian(spirv):X8}");
        Check(spirv.Length > 20 && spirv.Length % 4 == 0, "the entry point code is a whole number of SPIR-V words (B11 stays fixed)");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(spirv) == 0x07230203, "it starts with the SPIR-V magic number");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(spirv[4..]) == 0x00010500, "the SPIR-V version word is 1.5, as profiled");

        // A broken shader: no exception, a null module and readable diagnostics.
        var broken = session.LoadModuleFromSourceString("broken", "broken.slang", "void main( { }", out var badDiag);
        var badText = DiagText(badDiag);
        Console.WriteLine($"     broken shader diagnostics: {badText.Split('\n')[0]}");
        Check(broken == null, "a syntax error gives a null module, not an exception");
        Check(badText.Contains("broken.slang") && badText.Contains("error"), "the diagnostics blob names the file and the error");

        // Every proxy holds a native reference until Slang.Release; the GC never releases it. Children first.
        foreach (var o in new object?[] { badDiag, code, codeDiag, linkDiag, linked, composeDiag, program, entryPoint, loadDiag, module, session, global })
            Slang.Release(o);
        Slang.Release(global);                                       // a repeated release is harmless
    }

    static string DiagText(ISlangBlob? blob) =>
        blob == null ? "no diagnostics" : System.Text.Encoding.UTF8.GetString(blob.AsSpan()).TrimEnd('\0', '\n', '\r');
}
