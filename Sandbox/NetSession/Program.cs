// NetSession: skill recipe 7.33. A host-authoritative online game over RakNet, tested with real processes.
//   Host:   owns the simulation, applies the client's input, streams a state snapshot every fixed tick (unreliable).
//   Client: sends its paddle every tick (unreliable), draws the newest snapshot carried on along its velocity.
// With no arguments this tool is the test driver: it starts itself as host and client processes on 127.0.0.1 (bots
// drive the input, no window), reads their logs and checks them. Child modes:
//   --host <port>                                  stays up until an opponent has come and gone
//   --join <port> [--ticks N] [--drop-every K] [--version V]
// Usage: dotnet run -c Release --project Sandbox/NetSession      Exit code = number of failed checks.
using System.Diagnostics;
using System.Globalization;
using NetSession;

var culture = CultureInfo.InvariantCulture;
if (args.Contains("--host"))
    return RunHost(ushort.Parse(args[Array.IndexOf(args, "--host") + 1]));
if (args.Contains("--join"))
    return RunClient(args);
return RunDriver();

static void FixedStep(Func<bool> step)
{
    // Fixed 1/60 s steps against the wall clock; sleeping keeps the CPU idle, and a slow step catches up.
    var clock = Stopwatch.StartNew();
    var next = 0.0;
    while (true)
    {
        if (!step())
            return;
        next += World.Dt * 1000;
        while (clock.Elapsed.TotalMilliseconds < next)
            Thread.Sleep(1);
    }
}

static int RunHost(ushort port)
{
    var log = Console.Out;
    HostSession host;
    try
    {
        host = new HostSession(port);
    }
    catch (Exception e)
    {
        log.WriteLine($"start failed: {e.Message}");
        return 2;
    }
    using (host)
    {
        log.WriteLine($"hosting port {port}");
        var tick = 0;
        var endedAt = -1;
        FixedStep(() =>
        {
            tick++;
            var wasEnded = host.Ended;
            host.Step(s => { log.WriteLine(s); log.Flush(); }, 30 + 20 * MathF.Sin(tick * 0.03f)); // the host's bot paddle
            if (host.Playing && host.State.Tick % 10 == 0)
                log.WriteLine(FormattableString.Invariant($"state {host.State.Tick} {host.State.BallX:F3} {host.State.BallY:F3}"));
            if (host.Ended && !wasEnded)
                endedAt = tick;
            log.Flush();
            return !(endedAt >= 0 && tick > endedAt + 20) && tick < 60 * 30; // done 1/3 s after the opponent left; 30 s safety
        });
    }
    return 0;
}

static int RunClient(string[] args)
{
    string Arg(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 ? args[i + 1] : fallback;
    var port = ushort.Parse(Arg("--join", "0"));
    var ticks = int.Parse(Arg("--ticks", "240"));
    var dropEvery = int.Parse(Arg("--drop-every", "0"));
    Protocol.Version = ushort.Parse(Arg("--version", "1"));
    var log = Console.Out;
    ClientSession client;
    try
    {
        client = new ClientSession("127.0.0.1", port, dropEvery);
    }
    catch (Exception e)
    {
        log.WriteLine($"start failed: {e.Message}");
        return 2;
    }
    using (client)
    {
        var frame = 0;
        var frames = 0;
        var rawStalls = 0;
        var smoothStalls = 0;
        (float X, float Y) prevRaw = default, prevSmooth = default;
        var havePrev = false;
        uint lastLogged = 0;
        FixedStep(() =>
        {
            frame++;
            client.Step(s => { log.WriteLine(s); log.Flush(); }, 30 + 25 * MathF.Sin(frame * 0.05f));
            if (client.HasState && client.Playing)
            {
                // What a renderer would show this frame, raw versus smoothed: a stall is a frame where the ball didn't move.
                var raw = client.RawBall;
                var smooth = client.SmoothBall;
                if (havePrev)
                {
                    frames++;
                    if (MathF.Abs(raw.X - prevRaw.X) + MathF.Abs(raw.Y - prevRaw.Y) < 0.001f)
                        rawStalls++;
                    if (MathF.Abs(smooth.X - prevSmooth.X) + MathF.Abs(smooth.Y - prevSmooth.Y) < 0.001f)
                        smoothStalls++;
                }
                (prevRaw, prevSmooth, havePrev) = (raw, smooth, true);
                var s = client.Latest;
                if (s.Tick % 10 == 0 && s.Tick != lastLogged)
                {
                    lastLogged = s.Tick;
                    log.WriteLine(FormattableString.Invariant($"got {s.Tick} {s.BallX:F3} {s.BallY:F3}"));
                }
            }
            log.Flush();
            return !client.Ended && frame < ticks;
        });
        var echo = client.RecentSent.Contains(client.Latest.RightY) ? "ok" : "MISMATCH";
        log.WriteLine($"summary frames={frames} snapshots={client.Snapshots} dropped={client.Dropped} rawStalls={rawStalls} smoothStalls={smoothStalls} echo={echo} ping={client.Ping}");
    }
    return 0;
}

static int RunDriver()
{
    var failures = 0;
    void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (!ok)
            failures++;
    }

    var all = new List<Proc>();
    Proc Start(params string[] a)
    {
        var p = new Proc(a);
        all.Add(p);
        return p;
    }

    try
    {
        // ---- 1. a full match with simulated packet loss on the client ----
        Console.WriteLine("-- match: 4 s, the client ignores every 3rd snapshot");
        var host = Start("--host", "62110");
        Check(host.WaitFor("hosting port", 15), "host is listening");
        var a = Start("--join", "62110", "--ticks", "240", "--drop-every", "3");
        Check(a.WaitFor("joined", 10), "client joined (Hello -> Welcome)");
        Check(host.WaitFor("opponent joined", 5), "host saw the client join");
        Check(a.WaitExit(15) && a.Process.ExitCode == 0, "client finished its ticks and left by itself");
        Check(host.WaitFor("opponent left: goodbye", 5), "host was told the client left (DisconnectionNotification, not a timeout)");
        Check(host.WaitExit(5) && host.Process.ExitCode == 0, "host exited by itself after the opponent left");

        var summary = a.Lines().FirstOrDefault(l => l.StartsWith("summary"));
        Console.WriteLine($"     {summary}");
        int Field(string name) => summary == null ? -1 : int.Parse(summary.Split(' ').First(f => f.StartsWith(name + "=")).Split('=')[1]);
        var frames = Field("frames");
        Check(frames > 150 && Field("snapshots") > 100, $"snapshots flowed ({Field("snapshots")} received, {Field("dropped")} dropped, {frames} frames)");
        Check(summary != null && summary.Contains("echo=ok"), "the host applied the client's paddle and echoed it back in the state (input -> sim -> snapshot)");
        Check(Field("rawStalls") > frames / 5, $"without smoothing the ball stalls on lost snapshots ({Field("rawStalls")} of {frames} frames)");
        Check(Field("smoothStalls") <= frames / 50, $"dead reckoning keeps it moving ({Field("smoothStalls")} stalled frames)");
        Check(Field("ping") is >= 0 and < 100, $"ping is available ({Field("ping")} ms)");

        // The host is authoritative: any tick both logged must carry identical numbers.
        var hostStates = host.Lines().Where(l => l.StartsWith("state ")).Select(l => l.Split(' ')).ToDictionary(p => p[1], p => (p[2], p[3]));
        var common = a.Lines().Where(l => l.StartsWith("got ")).Select(l => l.Split(' ')).Where(p => hostStates.ContainsKey(p[1])).ToList();
        Check(common.Count >= 8 && common.All(p => hostStates[p[1]] == (p[2], p[3])), $"client and host agree on every shared tick ({common.Count} compared)");

        // ---- 2. a crash: the process is killed, so no goodbye is sent ----
        Console.WriteLine("-- crash: the client process is killed");
        host = Start("--host", "62111");
        Check(host.WaitFor("hosting port", 15), "host is listening");
        var b = Start("--join", "62111", "--ticks", "6000");
        Check(host.WaitFor("opponent joined", 10), "client joined");
        Thread.Sleep(1000);
        b.Process.Kill();
        var killed = Stopwatch.StartNew();
        Check(host.WaitFor("opponent left: lost connection", 10), "host noticed the crash (timeout, not a goodbye)");
        Console.WriteLine($"     noticed after {killed.ElapsedMilliseconds} ms (timeout {NetPeer.TimeoutMs} ms on both ends, T44)");
        Check(killed.ElapsedMilliseconds < NetPeer.TimeoutMs + 3000, "...within the timeout plus a margin");
        Check(host.WaitExit(5), "host exited");

        // ---- 3. a version mismatch ----
        Console.WriteLine("-- version mismatch");
        host = Start("--host", "62112");
        Check(host.WaitFor("hosting port", 15), "host is listening");
        var old = Start("--join", "62112", "--version", "2", "--ticks", "600");
        Check(old.WaitFor("rejected: host version 1", 10), "client was told the host runs a different version");
        Check(host.WaitFor("turned away version 2", 5), "host logged turning it away");
        Check(old.WaitExit(5) && old.Process.ExitCode == 0, "the rejected client exited cleanly");
        host.Process.Kill();

        // ---- 4. a full host ----
        Console.WriteLine("-- full host: a second client");
        host = Start("--host", "62113");
        Check(host.WaitFor("hosting port", 15), "host is listening");
        var first = Start("--join", "62113", "--ticks", "300");
        Check(first.WaitFor("joined", 10), "first client joined");
        var second = Start("--join", "62113", "--ticks", "300");
        Check(second.WaitFor("connect failed: That host already has an opponent", 10), "second client was refused (NoFreeIncomingConnections)");
        Check(first.WaitExit(15) && host.WaitExit(10), "the first client's match was not disturbed");
        Check(host.Lines().Count(l => l == "opponent joined") == 1, "the host only ever saw one opponent");
    }
    finally
    {
        foreach (var p in all)
            p.Dispose();
    }
    Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
    return failures;
}

/// <summary>A child copy of this program with its output collected line by line.</summary>
sealed class Proc : IDisposable
{
    readonly List<string> _lines = [];
    public Process Process { get; }

    public Proc(string[] args)
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") // started as "dotnet NetSession.dll"
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process = Process.Start(psi)!;
        Process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (_lines)
                    _lines.Add(e.Data);
        };
        Process.BeginOutputReadLine();
    }

    public List<string> Lines()
    {
        lock (_lines)
            return [.. _lines];
    }

    public bool WaitFor(string text, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (Lines().Any(l => l.Contains(text)))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public bool WaitExit(int seconds)
    {
        var done = Process.WaitForExit(seconds * 1000);
        if (done)
            Process.WaitForExit(); // let the output handlers drain
        return done;
    }

    public void Dispose()
    {
        if (!Process.HasExited)
            Process.Kill();
        Process.Dispose();
    }
}
