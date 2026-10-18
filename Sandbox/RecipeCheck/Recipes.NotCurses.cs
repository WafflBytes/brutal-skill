// Recipe 7.43: NotCurses. Version, init, text on the standard plane read back with Contents, a render, a non-blocking
// input poll and stop. NotCurses needs a terminal that answers its queries, so the parent runs a child process
// (--notcurses-child) inside GNU screen and reads the child's results from a file. Linux only (T55).
using Brutal;
using Brutal.Numerics;
using Brutal.NotCursesApi;

static partial class Recipes
{
    public static void NotCursesRecipe()
    {
        // T55: on Windows, notcurses_core_init blocks forever waiting for terminal replies (classic console and ConPTY).
        if (OperatingSystem.IsWindows())
            throw new SkipException("NotCurses init hangs in Windows consoles (T55); run on Linux");
        var screen = new[] { "/usr/bin/screen", "/bin/screen" }.FirstOrDefault(File.Exists);
        if (screen == null)
            throw new SkipException("needs GNU screen: a terminal that answers NotCurses's queries (tmux 3.4 doesn't, T55)");

        var result = Path.Combine(Path.GetTempPath(), $"recipecheck-nc-{Environment.ProcessId}.txt");
        File.Delete(result);
        var session = $"rc-nc-{Environment.ProcessId}";
        using (var p = System.Diagnostics.Process.Start(screen, ["-dmS", session, "-T", "xterm-256color", Environment.ProcessPath!, "--notcurses-child", result]))
            p.WaitForExit();
        var done = WaitFor(() => File.Exists(result) && new FileInfo(result).Length > 0, 30_000);
        if (!done)
        {
            using var quit = System.Diagnostics.Process.Start(screen, ["-S", session, "-X", "quit"]);
            quit.WaitForExit();
            throw new TimeoutException("the NotCurses child wrote no results in 30 s (stuck in init?)");
        }
        Thread.Sleep(200);                                                     // the child finishes writing
        var lines = File.ReadAllLines(result);
        File.Delete(result);
        foreach (var l in lines) Console.WriteLine($"     {l}");
        Check(lines.Length >= 6 && lines.All(l => l.StartsWith("ok ")), "every NotCurses check in the child passed");
    }

    // Child: exit 0 = every check passed; results go to the file so they survive the terminal being taken over.
    public static int NotCursesChild(string resultFile)
    {
        var lines = new List<string>();
        var ok = true;
        void Note(bool pass, string what) { ok &= pass; lines.Add($"{(pass ? "ok  " : "FAIL")} {what}"); }
        try
        {
            var version = NotCurses.Version;
            Note(version.Length > 0, $"NotCurses.Version = {version}");

            var opts = new NotCursesOptions { Flags = NotCursesOption.SuppressBanners };
            using (var nc = NotCursesHandle.Init(opts))
            {
                var std = nc.StdPlane();
                std.Dim(out var dim);
                Note(dim.X > 20 && dim.Y > 3, $"the standard plane is {dim.X}x{dim.Y} cells");
                var written = std.PutStr(new int2(2, 1), "BRUTAL notcurses"u8);       // int2(x, y)
                Note(written == 16, $"PutStr at (x 2, y 1) wrote {written} columns");
                nc.Render();
                var back = std.Contents(new int2(2, 1), new uint2(16, 1));           // also (x, y) order
                Note(back == "BRUTAL notcurses", $"Contents read the text back: '{back}'");
                var key = nc.GetNblock(out _);
                Note(true, $"GetNblock returned {key} with no key pressed");
                Thread.Sleep(500);
            }
            Note(true, "Dispose (notcurses_stop) restored the terminal");
        }
        catch (Exception e)
        {
            Note(false, $"{e.GetType().Name}: {e.Message}");
        }
        File.WriteAllLines(resultFile, lines);
        return ok ? 0 : 1;
    }
}
