using BrutalGame;
using BrutalGame.Platform;

// BrutalGame: a 2D game built on BRUTAL (SDL3 renderer, SDL_ttf text, SDL_mixer sound, Stb, Numerics).
//   BrutalGame.exe                    play (logo splash first, as the BRUTAL licence requires)
//   BrutalGame.exe --selftest         headless simulation checks; exit code 0 = pass
//   BrutalGame.exe --snapshot <dir>   scripted run drawn off-screen to PNGs, no window needed; exit code 0 = pass
//   BrutalGame.exe --smoke [seconds]  real window loop with scripted input, then exits; exit code 0 = pass

if (args.Length > 0 && args[0] == "--selftest")
    return SelfTest.Run();

if (args.Length > 0 && args[0] == "--snapshot")
    return SnapshotRun.Run(args.Length > 1 ? args[1] : "snapshots");

if (args.Length > 0 && args[0] == "--smoke")
{
    using var smoke = new App(args.Length > 1 && float.TryParse(args[1], out var secs) ? secs : 4f);
    return smoke.RunSmoke();
}

using var app = new App();
app.Run();
return 0;
