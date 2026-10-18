// Recipe 7.39: Brutal.Monitor. Setup errors (T29), the log file and crash-tail ring of a clean run, a real unhandled-exception
// crash in a child process (last-chance dump, the tail beside it), the next run recovering that tail and archiving the
// dead run's log, and a first-chance dump. Child modes run through `RecipeCheck --monitor-child <mode> <dir>`.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Brutal.Logging;
using Brutal.Monitor.Common;
using Microsoft.Extensions.Logging;
using Monitor = Brutal.Monitor.Monitor;

static partial class Recipes
{
    public static void MonitorRecipe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"recipecheck-monitor-{Environment.ProcessId}");
        var logs = Path.Combine(root, "Logs");
        var dumps = Path.Combine(root, "Dumps");
        Directory.CreateDirectory(root);
        try
        {
            // T29: the name becomes file names and retention globs.
            foreach (var bad in new[] { "a/b", "game*", "x?" })
                Check(Throws<ArgumentException>(() => new Monitor(bad).Dispose()), $"name '{bad}' throws ArgumentException");

            // T29: the directories are the game's to create.
            using (var m = new Monitor("rc-setup"))
            {
                Check(m.SubprocessRunning, "the subprocess was found (runtimes/<rid>/native in the build output) and runs");
                Check(Throws<DirectoryNotFoundException>(() => m.AddLogger(new LoggerOptions { LogDirectory = logs })),
                    "AddLogger on a missing directory throws DirectoryNotFoundException");
                Check(Throws<DirectoryNotFoundException>(() => m.AddCollector(new CollectorOptions { DumpPath = dumps, LastChance = DumpType.Mini })),
                    "AddCollector on a missing directory throws DirectoryNotFoundException");
            }
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(dumps);

            // A clean run: log file named <name>.<yyMMdd-HHmmss>.<pid>.log, a ring beside it that a clean Dispose deletes.
            string logPath, ringPath;
            using (var m = new Monitor("rc-clean", LogLevel.Information))
            {
                m.AddLogger(new LoggerOptions { LogDirectory = logs });
                Check(Throws<InvalidOperationException>(() => m.AddLogger(new LoggerOptions { LogDirectory = logs })),
                    "a second AddLogger throws InvalidOperationException");
                logPath = m.LogProvider.LogFilePath!;
                ringPath = m.LogProvider.CrashTailPath!;
                Check(Regex.IsMatch(Path.GetFileName(logPath), $@"^rc-clean\.\d{{6}}-\d{{6}}\.{Environment.ProcessId}\.log$"),
                    $"log file name is <name>.<stamp>.<pid>.log ({Path.GetFileName(logPath)})");
                Check(File.Exists(ringPath) && ringPath == logPath + ".ring", "the crash-tail ring sits beside the log");

                // LogProvider is a plain ILoggerProvider: usable without Brutal.Logging.
                var game = m.LogProvider.CreateLogger("Game");
                game.LogInformation("hello {Who}", "world");
                game.LogDebug("below the minimum level");
                Check(WaitFor(() => m.LogProvider.GetStats().LinesWritten >= 1, 2000), "the background writer writes the line");
                var stats = m.LogProvider.GetStats();
                Console.WriteLine($"     clean run: {stats.LinesWritten} line(s), {stats.BytesWritten} bytes, ring {stats.RingSlotCount}x{stats.RingSlotSize} B, seq {stats.RingSequence}");
                Check(stats.RingSequence == 1, "the debug line never reached the ring (dropped before formatting)");
            }
            Check(!File.Exists(ringPath), "a clean Dispose deletes the ring");
            var cleanLog = File.ReadAllText(logPath);
            Check(cleanLog.Contains("hello world") && !cleanLog.Contains("below the minimum"), "the log holds the Information line and not the Debug one");
            Check(cleanLog.TrimEnd() == "hello world", "with no LogFormatter a line is the bare message (no level, category or time)");

            // A real crash: unhandled exception in a child with LastChance = Mini.
            var crash = RunMonitorChild("crash", root, 120_000);
            Console.WriteLine($"     crash child: exit code {crash.ExitCode} after {crash.Ms} ms");
            Check(crash.ExitCode != 0, "the crash child died with a nonzero exit code");
            var lastChance = Path.Combine(dumps, "LastChance");
            var crashFiles = Directory.Exists(lastChance) ? Directory.GetFiles(lastChance) : [];
            foreach (var f in crashFiles)
                Console.WriteLine($"     LastChance/{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)");
            var dump = crashFiles.FirstOrDefault(f => f.EndsWith(".dmp"));
            var tail = crashFiles.FirstOrDefault(f => f.EndsWith(".crash.log"));
            Check(dump != null && new FileInfo(dump).Length > 0, "a last-chance .dmp was written");
            Check(Path.GetFileName(dump!).StartsWith("rc-crash."), "the dump is named after the Monitor name");
            Check(tail != null, "a .crash.log tail was written beside the dump");
            var tailText = File.ReadAllText(tail!);
            Check(tailText.Contains("last words"), "the crash tail holds the line logged just before the throw");
            Check(!tailText.Contains("unwired"), "LogCategory lines from before LogSystem.Initialize(AddProvider) are not captured");

            var crashLog = Directory.GetFiles(logs, "rc-crash.*.log").Single(f => Regex.IsMatch(Path.GetFileName(f), @"^rc-crash\.\d{6}-\d{6}\.\d+\.log$"));
            var crashLogText = ReadShared(crashLog);
            Console.WriteLine($"     crash child's own log: wired line {(crashLogText.Contains("wired line") ? "in" : "missing")}, last words {(crashLogText.Contains("last words") ? "in" : "missing")}");
            // Main-thread crash: `using` disposed the Monitor on the way out (its own "stop" line is in the tail), so the
            // writer drained to the file and the subprocess got a clean Stop: no abnormal-exit.log, ring kept for next run.
            Check(crashLogText.Contains("wired line") && crashLogText.Contains("last words"), "the log file holds every line, the last one too");
            var leftovers = Directory.GetFiles(logs, "rc-crash.*").Select(Path.GetFileName).ToArray();
            Console.WriteLine($"     Logs after the crash: {string.Join(", ", leftovers)}");
            Check(leftovers.Any(f => f!.EndsWith(".log.ring")) && !leftovers.Any(f => f!.Contains("abnormal-exit")), "the ring is preserved and no abnormal-exit.log is written");

            // The next run with the same name recovers the tail and archives the dead run's log.
            using (var m = new Monitor("rc-crash"))
                m.AddLogger(new LoggerOptions { LogDirectory = logs });
            var recovered = Directory.GetFiles(logs, "rc-crash.*.previous-crash.log");
            var archives = Directory.Exists(Path.Combine(logs, "Archives")) ? Directory.GetFiles(Path.Combine(logs, "Archives"), "rc-crash.*.log") : [];
            Console.WriteLine($"     next run: {recovered.Length} previous-crash.log, {archives.Length} archive(s): {string.Join(", ", archives.Select(Path.GetFileName))}");
            Check(recovered.Length == 1 && File.ReadAllText(recovered[0]).Contains("last words"), "the next run writes <stem>.previous-crash.log from the ring");
            Check(archives.Length == 1 && Regex.IsMatch(Path.GetFileName(archives[0]), @"^rc-crash\.\d{6}\.0\.log$") && File.ReadAllText(archives[0]).Contains("wired line"),
                "the dead run's log moves to Logs/Archives/<name>.<yyMMdd>.0.log");
            Check(Directory.GetFiles(logs, "rc-crash.*.ring").Length == 0, "the recovered ring is deleted");

            // First chance: best effort, not waited on; the child waits for the file itself.
            var first = RunMonitorChild("firstchance", root, 60_000);
            Console.WriteLine($"     first-chance child: exit code {first.ExitCode} after {first.Ms} ms; {first.Output.Trim()}");
            Check(first.ExitCode == 0, "a handled exception produced a FirstChance dump");

            // Logging only (no collector), crash on a worker thread: Dispose never runs, so the subprocess sees the host
            // exit nonzero without a Stop and saves the ring itself as <stem>.<time>.abnormal-exit.log.
            var nodump = RunMonitorChild("crash-nodump", root, 60_000);
            Check(nodump.ExitCode != 0, "the worker-thread crash child died with a nonzero exit code");
            Check(WaitFor(() => Directory.GetFiles(logs, "rc-nodump.*.abnormal-exit.log").Length == 1, 5000), "the subprocess wrote an abnormal-exit.log");
            var abnormal = Directory.GetFiles(logs, "rc-nodump.*.abnormal-exit.log")[0];
            Console.WriteLine($"     worker crash, no collector: exit {nodump.ExitCode} after {nodump.Ms} ms; {string.Join(", ", Directory.GetFiles(logs, "rc-nodump.*").Select(Path.GetFileName))}");
            Check(Regex.IsMatch(Path.GetFileName(abnormal), @"^rc-nodump\.\d{6}-\d{6}\.\d+\.\d{8}_\d{6}\.abnormal-exit\.log$"), "abnormal-exit.log is named <stem>.<yyyyMMdd_HHmmss>.abnormal-exit.log");
            Check(File.ReadAllText(abnormal).Contains("last words"), "the abnormal-exit tail holds the last line");
            Check(Directory.GetFiles(logs, "rc-nodump.*.ring").Length == 0, "the subprocess removed the ring after saving it");
            using (var m = new Monitor("rc-nodump"))
                m.AddLogger(new LoggerOptions { LogDirectory = logs });
            Check(Directory.GetFiles(logs, "rc-nodump.*.previous-crash.log").Length == 0, "so the next run has no previous-crash.log to write");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Runs in the child process. Exit code 0 = what the parent asked for happened (the crash mode never returns).
    public static int MonitorChild(string mode, string root)
    {
        var logs = Path.Combine(root, "Logs");
        var dumps = Path.Combine(root, "Dumps");
        if (mode is "crash" or "crash-nodump")
        {
            using var m = new Monitor(mode == "crash" ? "rc-crash" : "rc-nodump", LogLevel.Information);
            var log = LogCategory.Make("Game");
            m.AddLogger(new LoggerOptions { LogDirectory = logs });
            if (mode == "crash")
                m.AddCollector(new CollectorOptions { DumpPath = dumps, LastChance = DumpType.Mini });
            log.Info($"unwired: LogCategory before LogSystem.Initialize");
            LogSystem.Initialize(b => b.AddProvider(m.LogProvider));
            log.Info($"wired line");
            Thread.Sleep(500); // past the 100 ms flush interval
            log.Info($"last words");
            if (mode == "crash")
                throw new InvalidOperationException("recipe 7.39 crash on purpose"); // main thread: `using` still disposes m
            // A worker thread: nothing unwinds through the main thread's `using`.
            new Thread(() => throw new InvalidOperationException("recipe 7.39 worker crash on purpose")).Start();
            Thread.Sleep(Timeout.Infinite);
            return 3;
        }
        if (mode == "firstchance")
        {
            using var m = new Monitor("rc-first");
            m.AddCollector(new CollectorOptions { DumpPath = dumps, FirstChance = DumpType.Triage });
            try { throw new InvalidOperationException("handled on purpose"); }
            catch (InvalidOperationException) { }
            var dir = Path.Combine(dumps, "FirstChance");
            var found = WaitFor(() => Directory.Exists(dir) && Directory.GetFiles(dir, "rc-first.*.dmp").Any(f => new FileInfo(f).Length > 0), 30_000);
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
            Console.WriteLine($"FirstChance: {string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)"))}");
            return found ? 0 : 1;
        }
        Console.Error.WriteLine($"unknown monitor child mode '{mode}'");
        return 2;
    }

    static (int ExitCode, long Ms, string Output) RunMonitorChild(string mode, string root, int timeoutMs)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, ["--monitor-child", mode, root])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        var sw = Stopwatch.StartNew();
        using var child = Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(timeoutMs))
        {
            child.Kill(true);
            throw new TimeoutException($"monitor child '{mode}' still running after {timeoutMs} ms");
        }
        return (child.ExitCode, sw.ElapsedMilliseconds, stdout.Result + stderr.Result);
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd();
    }

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T) { return true; }
        return false;
    }
}
