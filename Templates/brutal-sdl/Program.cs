using Brutal.SdlApi;
using BrutalGame;
using BrutalGame.Platform;

// BrutalGame: a 2D game built on BRUTAL (SDL3 renderer, SDL_ttf text, SDL_mixer sound, Stb, Numerics).
//   BrutalGame.exe                    play (logo splash first, as the BRUTAL licence requires)
//   BrutalGame.exe --selftest         headless simulation checks; exit code 0 = pass
//   BrutalGame.exe --snapshot <dir>   scripted run drawn off-screen to PNGs, no window needed; exit code 0 = pass
//   BrutalGame.exe --smoke [seconds]  real window loop with scripted input, then exits; exit code 0 = pass
//   BrutalGame.exe --crash-test       throws on purpose to show the crash dialog and write the error log

if (args.Length > 0 && args[0] == "--selftest")
    return SelfTest.Run();

if (args.Length > 0 && args[0] == "--snapshot")
    return SnapshotRun.Run(args.Length > 1 ? args[1] : "snapshots");

if (args.Length > 0 && args[0] == "--smoke")
{
    using var smoke = new App(args.Length > 1 && float.TryParse(args[1], out var secs) ? secs : 4f);
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
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrutalGame");
    var log = Path.Combine(dir, "error.log");
    try
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(log, $"{DateTime.Now:O}\n{e}\n");
    }
    catch (IOException) { } // never let the crash report crash
    catch (UnauthorizedAccessException) { }
    Sdl.PInvoke.ShowSimpleMessageBox(
        SdlMessageBoxFlags.Error,
        "BrutalGame stopped",
        $"Sorry, the game hit an error and has to close.\n\n{e.Message}\n\nDetails were saved to:\n{log}",
        default
    );
    return 1;
}
