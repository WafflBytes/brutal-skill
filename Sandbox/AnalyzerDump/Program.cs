// AnalyzerDump (skill: references/core-libs.md): loads every analyzers/dotnet/cs/*.dll in the cached Ahwoo.BRUTAL.* packages and prints
// each diagnostic (id, default severity, title), which ids have code fixes, and every source generator.
// Usage: dotnet run -c Release --project Sandbox/AnalyzerDump
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

var nugetRoot =
    Environment.GetEnvironmentVariable("NUGET_PACKAGES")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

var dlls = Directory
    .GetDirectories(nugetRoot, "ahwoo.brutal.*")
    .Select(dir => Directory.GetDirectories(dir).OrderBy(v => Version.Parse(Path.GetFileName(v))).Last())
    .Select(dir => Path.Combine(dir, "analyzers", "dotnet", "cs"))
    .Where(Directory.Exists)
    .SelectMany(dir => Directory.GetFiles(dir, "*.dll"))
    .OrderBy(Path.GetFileName)
    .ToList();
if (dlls.Count == 0)
{
    Console.Error.WriteLine($"No BRUTAL analyzer DLLs in {nugetRoot}. Restore Sandbox/ExportAudit first.");
    return 2;
}

var rules = new SortedDictionary<string, (DiagnosticDescriptor Descriptor, string Source)>(StringComparer.Ordinal);
var fixable = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
var generators = new List<string>();

foreach (var path in dlls)
{
    var assembly = Assembly.LoadFrom(path);
    Type[] types;
    try
    {
        types = assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException e)
    {
        types = e.Types.Where(t => t != null).ToArray()!;
        Console.Error.WriteLine($"warning: {Path.GetFileName(path)}: {e.LoaderExceptions.FirstOrDefault()?.Message}");
    }
    var source = Path.GetFileNameWithoutExtension(path);
    foreach (var type in types.Where(t => t is { IsAbstract: false, IsClass: true }))
    {
        if (typeof(DiagnosticAnalyzer).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
        {
            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
            foreach (var d in analyzer.SupportedDiagnostics)
                rules.TryAdd(d.Id, (d, $"{source}.{type.Name}"));
        }
        else if (typeof(CodeFixProvider).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
        {
            var fix = (CodeFixProvider)Activator.CreateInstance(type)!;
            foreach (var id in fix.FixableDiagnosticIds)
            {
                if (!fixable.TryGetValue(id, out var list))
                    fixable[id] = list = [];
                list.Add(type.Name);
            }
        }
        else if (
            typeof(IIncrementalGenerator).IsAssignableFrom(type) || typeof(ISourceGenerator).IsAssignableFrom(type)
        )
            generators.Add($"{source}.{type.Name}");
    }
}

Console.WriteLine($"Analyzer assemblies ({dlls.Count}):");
foreach (var dll in dlls)
    Console.WriteLine($"  {Path.GetRelativePath(nugetRoot, dll)}");
Console.WriteLine();
Console.WriteLine($"{"Id", -16} {"Severity", -8} {"Fix", -3}  Title");
foreach (var (id, (d, _)) in rules)
{
    var sev = d.DefaultSeverity switch
    {
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Warning => "Warning",
        DiagnosticSeverity.Info => "Info",
        _ => "Hidden",
    };
    Console.WriteLine($"{id, -16} {sev, -8} {(fixable.ContainsKey(id) ? "yes" : ""), -3}  {d.Title}");
}
foreach (var id in fixable.Keys.Where(id => !rules.ContainsKey(id)))
    Console.WriteLine($"{id, -16} {"?", -8} yes  (code fix for a diagnostic not declared by these analyzers)");
Console.WriteLine();
Console.WriteLine($"Source generators ({generators.Count}):");
foreach (var g in generators)
    Console.WriteLine($"  {g}");
return 0;
