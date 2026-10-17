// ExportAudit (skill: references/export-gaps.md): which P/Invoke entry points declared in Ahwoo.BRUTAL.* assemblies are missing from the
// packaged win-x64 native DLLs. Calls to a missing entry point compile, then throw EntryPointNotFoundException.
// Usage: dotnet run -c Release --project Sandbox/ExportAudit [-- --detail] [--package <Name>]
//   --detail       also print one "missing: <Package> <dll>!<entry>" line per gap (grep-friendly)
//   --package X    only audit Ahwoo.BRUTAL.X
// Only DllImport/LibraryImport declarations are audited; function pointers loaded at runtime (GLAD, Vulkan device
// functions) are not.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var detail = args.Contains("--detail");
var only = args.SkipWhile(a => a != "--package").Skip(1).FirstOrDefault();
const string Rid = "win-x64";

var nugetRoot =
    Environment.GetEnvironmentVariable("NUGET_PACKAGES")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

// Newest cached version of every Ahwoo.BRUTAL.* package.
var packages = Directory
    .GetDirectories(nugetRoot, "ahwoo.brutal.*")
    .Select(dir =>
        (
            Name: Path.GetFileName(dir)["ahwoo.brutal.".Length..],
            Dir: Directory.GetDirectories(dir).OrderBy(v => Version.Parse(Path.GetFileName(v))).Last()
        )
    )
    .OrderBy(p => p.Name)
    .ToList();
if (packages.Count == 0)
{
    Console.Error.WriteLine($"No ahwoo.brutal.* packages in {nugetRoot}. Run 'dotnet restore' on this project first.");
    return 2;
}

// Every packaged native DLL, keyed by file name without extension (a module ref may be "SDL3" or "SDL3.dll").
var natives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var (_, dir) in packages)
{
    var nativeDir = Path.Combine(dir, "runtimes", Rid, "native");
    if (Directory.Exists(nativeDir))
        foreach (var dll in Directory.GetFiles(nativeDir, "*.dll"))
            natives.TryAdd(Path.GetFileNameWithoutExtension(dll), dll);
}
var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

// Module names that a package's DllImportResolver remaps on Windows (checked in BRUTAL/ source).
var windowsAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["glfw"] = "glfw3", //            Brutal.Glfw/Manual/Libraries/Glfw.Initialization.cs:39
    ["openal"] = "OpenAL32", //       Brutal.OpenAl/Manual/NativeLoader.cs:33
    ["shaderc"] = "shaderc_shared", // Brutal.ShaderC/ShaderC.cs:38
    ["vulkan"] = "vulkan-1", //       Brutal.Vulkan/ImportResolver.cs:21 (System32 only, T5)
};
var exportCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"NuGet cache: {nugetRoot}  ({packages.Count} packages, {natives.Count} native DLLs for {Rid})");
Console.WriteLine();
Console.WriteLine($"{"Package", -26} {"Version", -10} {"Imports", 8} {"Missing", 8}  Native modules");
var detailLines = new List<string>();
var totalImports = 0;
var totalMissing = 0;

foreach (var (name, dir) in packages)
{
    if (only != null && !name.Equals(only, StringComparison.OrdinalIgnoreCase))
        continue;
    var libDir = Path.Combine(dir, "lib", "net10.0");
    if (!Directory.Exists(libDir))
        continue;

    var imports = new SortedSet<(string Module, string Entry)>();
    foreach (var assemblyPath in Directory.GetFiles(libDir, "*.dll"))
        CollectImports(assemblyPath, imports);
    if (imports.Count == 0)
        continue;

    var missing = 0;
    var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (module, entry) in imports)
    {
        var exports = ResolveExports(module, out var where);
        modules.Add($"{module}{where}");
        if (exports == null || !exports.Contains(entry))
        {
            missing++;
            if (detail)
                detailLines.Add($"missing: {name} {module}!{entry}{(exports == null ? " (module not found)" : "")}");
        }
    }
    totalImports += imports.Count;
    totalMissing += missing;
    Console.WriteLine($"{name, -26} {Path.GetFileName(dir), -10} {imports.Count, 8} {missing, 8}  {string.Join(", ", modules)}");
}

Console.WriteLine();
Console.WriteLine($"Total: {totalImports} imports, {totalMissing} missing.");
if (detail)
{
    Console.WriteLine();
    foreach (var line in detailLines)
        Console.WriteLine(line);
}
return 0;

static void CollectImports(string assemblyPath, SortedSet<(string, string)> imports)
{
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
        return;
    var md = pe.GetMetadataReader();
    foreach (var handle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(handle);
        var import = method.GetImport();
        if (import.Module.IsNil)
            continue;
        var module = md.GetString(md.GetModuleReference(import.Module).Name);
        var entry = import.Name.IsNil ? md.GetString(method.Name) : md.GetString(import.Name);
        if (entry.StartsWith('#'))
            continue; // by ordinal
        imports.Add((module, entry));
    }
}

HashSet<string>? ResolveExports(string module, out string where)
{
    var key = module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module[..^4] : module;
    string? path = null;
    where = "";
    if (windowsAliases.TryGetValue(key, out var alias))
    {
        key = alias;
        where = $" -> {alias}";
    }
    if (natives.TryGetValue(key, out var packaged) || natives.TryGetValue("lib" + key, out packaged))
        path = packaged;
    else if (File.Exists(Path.Combine(system32, key + ".dll")))
    {
        path = Path.Combine(system32, key + ".dll");
        where += " (System32)";
    }
    else
    {
        where += " (NOT FOUND)";
        return null;
    }
    if (!exportCache.TryGetValue(path, out var exports))
        exportCache[path] = exports = ReadExports(path);
    return exports;
}

static HashSet<string> ReadExports(string dllPath)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    var bytes = File.ReadAllBytes(dllPath);
    using var pe = new PEReader(new MemoryStream(bytes));
    var headers = pe.PEHeaders;
    var dir = headers.PEHeader!.ExportTableDirectory;
    if (dir.Size == 0)
        return names;

    int ToOffset(int rva)
    {
        foreach (var s in headers.SectionHeaders)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                return rva - s.VirtualAddress + s.PointerToRawData;
        throw new InvalidDataException($"RVA 0x{rva:X} outside every section of {dllPath}");
    }

    // IMAGE_EXPORT_DIRECTORY: NumberOfNames at +24, AddressOfNames at +32.
    var exportDir = ToOffset(dir.RelativeVirtualAddress);
    var count = BitConverter.ToInt32(bytes, exportDir + 24);
    var namesTable = ToOffset(BitConverter.ToInt32(bytes, exportDir + 32));
    for (var i = 0; i < count; i++)
    {
        var start = ToOffset(BitConverter.ToInt32(bytes, namesTable + 4 * i));
        var end = Array.IndexOf(bytes, (byte)0, start);
        names.Add(System.Text.Encoding.ASCII.GetString(bytes, start, end - start));
    }
    return names;
}
