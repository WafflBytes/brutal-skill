using BrutalGame3D;
using BrutalGame3D.Platform;
using BrutalGame3D.Sim;

// BrutalGame3D: a 3D ball-rolling coin hunt built on BRUTAL (SDL3 window, OpenGL 4.1, Jolt physics, SDL_ttf text,
// SDL_mixer sound, Stb, Numerics).
//   BrutalGame3D.exe                    play (logo splash first, as the BRUTAL licence requires)
//   BrutalGame3D.exe --selftest         headless simulation checks; exit code 0 = pass
//   BrutalGame3D.exe --snapshot <dir>   scripted run drawn off-screen to PNGs, no window needed; exit code 0 = pass
//   BrutalGame3D.exe --smoke [seconds]  real window loop with scripted input, then exits; exit code 0 = pass (needs >= 5 s)
//   BrutalGame3D.exe --crash-test       throws on purpose to show the crash dialog and write the error log
//   BrutalGame3D.exe --vcruntime-test   shows the "VC++ runtime missing" dialog as if the runtime weren't installed

try
{
    return RunMode(args);
}
finally
{
    JoltRuntime.Shutdown(); // after every World is disposed (T18)
}

static int RunMode(string[] args)
{
if (args.Length > 0 && args[0] == "--selftest")
    return SelfTest.Run();

// The BRUTAL native DLLs (SDL3, SDL_ttf, SDL_mixer, stb, ...) need the Microsoft Visual C++ runtime, which
// Windows doesn't include and a self-contained .NET publish doesn't bundle (skill trap T41). Without it, the
// first SDL call fails with "Unable to load DLL 'SDL3' or one of its dependencies".
if (!NativeDialog.VcRuntimeInstalled() || (args.Length > 0 && args[0] == "--vcruntime-test"))
{
    NativeDialog.ShowError(
        "BrutalGame3D can't start",
        "BrutalGame3D needs the Microsoft Visual C++ Redistributable (x64), which isn't installed on this PC.\n\n"
            + "Download and install it from Microsoft:\n"
            + NativeDialog.VcRedistUrl
            + "\n\nThen start BrutalGame3D again."
    );
    return 3;
}

if (args.Length > 0 && args[0] == "--snapshot")
    return SnapshotRun.Run(args.Length > 1 ? args[1] : "snapshots");

if (args.Length > 0 && args[0] == "--smoke")
{
    using var smoke = new App(args.Length > 1 && float.TryParse(args[1], out var secs) ? secs : 5f);
    return smoke.RunSmoke();
}

// Play. The published exe is a GUI app with no console, so an unhandled exception would close the window
// without a word. Log it and tell the player where the log is instead.
try
{
    if (args.Length > 0 && args[0] == "--crash-test")
        throw new InvalidOperationException("crash test: this error was raised on purpose");
    using var app = new App();
    app.Run();
    return 0;
}
catch (Exception e)
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrutalGame3D");
    var log = Path.Combine(dir, "error.log");
    try
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(log, $"{DateTime.Now:O}\n{e}\n");
    }
    catch (IOException) { } // never let the crash report crash
    catch (UnauthorizedAccessException) { }
    // A Win32 dialog, not SDL's: the error may be SDL itself failing to load.
    NativeDialog.ShowError(
        "BrutalGame3D stopped",
        $"Sorry, the game hit an error and has to close.\n\n{e.Message}\n\nDetails were saved to:\n{log}"
    );
    return 1;
}
}
