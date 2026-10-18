// Recipe 7.32: OpenAL end to end. Open the default device, make a context, buffer generated PCM, play it on a source and
// watch the source state (Playing -> Stopped, looping, stop, pooling a source), plus the latching error model.
// Skips (not fails) when the machine has no audio device, like a game should fall back to silence.
using System.Runtime.InteropServices;
using Brutal.OpenAlApi;

static partial class Recipes
{
    /// <summary>A 16-bit mono sine tone: what a game would synthesise or decode into <c>AlBuffer.SetData</c>.</summary>
    static short[] OpenAlTone(float hz, float seconds, int rate)
    {
        var pcm = new short[(int)(seconds * rate)];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(MathF.Sin(2 * MathF.PI * hz * i / rate) * 0.4f * short.MaxValue);
        return pcm;
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                return false;
            Thread.Sleep(5);
        }
        return true;
    }

    public static void OpenAlRecipe()
    {
        const int Rate = 44100;
        var nullBackend = Environment.GetEnvironmentVariable("ALSOFT_DRIVERS") == "null";
        Device device;
        Context context;
        try
        {
            device = Alc.OpenDevice(); // null/omitted name = the default device; throws when there is none
            context = device.CreateContext();
        }
        catch (Exception e) when (!nullBackend) // AlcException: no sound card, no active default output, a remote session
        {
            // OpenAL Soft reads ALSOFT_DRIVERS once, when the library first loads, so setting it now is too late.
            // Re-run this recipe in a child process that starts with OpenAL Soft's own "null" backend (no sound out,
            // but the real buffer/source/state machine runs in real time).
            Console.WriteLine($"     no usable default device ({e.Message}); re-running on the null backend in a child process");
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, ["7.32"])
            {
                RedirectStandardOutput = true, UseShellExecute = false,
            };
            psi.Environment["ALSOFT_DRIVERS"] = "null";
            using var child = System.Diagnostics.Process.Start(psi)!;
            var output = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine("     [null backend] " + line.TrimEnd());
            if (child.ExitCode != 0 || !output.Contains("PASS 7.32"))
                throw new Exception("the OpenAL recipe failed on the null backend");
            return;
        }
        Console.WriteLine($"     opened: {(nullBackend ? "ALSOFT_DRIVERS=null backend" : "default playback device")}");

        using (device)
        using (context)
        {
            context.MakeCurrent(); // required before any Al.* call; errors latch per context and per thread
            Console.WriteLine($"     device: {device.Specifier}, ALC {device.MajorVersion}.{device.MinorVersion}, renderer {Al.GetString(AlGetString.Renderer)}");
            Check(Context.Current?.IsCurrent == true, "the context is current");
            Check(device.Connected, "the device reports connected");

            var buffer = Al.GenBuffer();
            var pcm = OpenAlTone(440, 0.25f, Rate);
            buffer.SetData(AlFormat.Mono16, MemoryMarshal.AsBytes(pcm.AsSpan()), Rate); // raw bytes, not shorts
            Al.ThrowIfError();

            var source = Al.GenSource();
            source.Buffer = buffer;
            source.Gain = 0.0f; // run silently: the state machine is the same
            Check(source.State == AlSourceState.Initial, "a new source is Initial");
            source.Play();
            Check(source.State == AlSourceState.Playing, "Play() makes the source Playing at once");
            Check(WaitFor(() => source.SampleOffset > 0, 1000), "the play position advances");
            Check(WaitFor(() => source.State == AlSourceState.Stopped, 3000), "a 0.25 s one-shot ends up Stopped by itself");

            // Looping keeps playing until stopped.
            source.Looping = true;
            source.Play();
            Thread.Sleep(450); // longer than the 0.25 s buffer
            Check(source.State == AlSourceState.Playing, "a looping source is still Playing after the buffer length");
            source.Stop();
            Check(source.State == AlSourceState.Stopped, "Stop() makes it Stopped");

            // Pooling: stop, swap the buffer, play again (what a game's sound-effect pool does).
            source.Looping = false;
            var buffer2 = Al.GenBuffer();
            buffer2.SetData(AlFormat.Mono16, MemoryMarshal.AsBytes(OpenAlTone(880, 0.1f, Rate).AsSpan()), Rate);
            source.Buffer = buffer2;
            source.Play();
            Check(WaitFor(() => source.State == AlSourceState.Stopped, 3000), "a pooled source plays a different buffer to the end");
            Al.ThrowIfError();

            // Errors latch, they don't throw: a bad call is silent until you poll.
            Check(Al.GetError() == AlError.NoError, "no latched error so far");
            source.Delete();
            buffer.Delete(); // delete each exactly once; a buffer can't be deleted while a source still has it queued
            buffer2.Delete();
            source.Play(); // deleted name
            Check(Al.GetError() == AlError.InvalidName, "using a deleted source latches AL_INVALID_NAME (no exception)");
            Check(Al.GetError() == AlError.NoError, "reading the error clears it");

            Context.MakeCurrent(null); // release before the context is disposed
        }
    }

    // T30: OpenAL Soft is under the GNU Library GPL v2. It must stay a separate library beside the exe, and its
    // LICENSES/ must ship. Nothing copies them by default; this project uses the target from shipping.md.
    public static void OpenAlLicenceTrap()
    {
        var dir = AppContext.BaseDirectory;
        var native = Directory.GetFiles(dir).Select(Path.GetFileName)
            .Where(f => f == "OpenAL32.dll" || f!.StartsWith("libopenal.")).ToArray();
        Console.WriteLine($"     native: {string.Join(", ", native)}");
        Check(native.Length > 0, "OpenAL Soft is a separate native library beside the exe (dynamic linking)");

        var licences = Path.Combine(dir, "licenses");
        Check(Directory.Exists(licences), "licenses/ exists (the IncludeBrutalThirdPartyNotices target ran)");
        string[] expected = ["BRUTAL-OPENAL-THIRD-PARTY-NOTICES.txt", "openal-soft-COPYING.txt", "openal-soft-BSD-3Clause.txt",
            "openal-soft-LICENSE-pffft.txt", "openal-soft-fmt-LICENSE.txt", "openal-soft-gsl-LICENSE.txt"];
        var missing = expected.Where(f => !File.Exists(Path.Combine(licences, f))).ToArray();
        Check(missing.Length == 0, $"all OpenAL licence files copied (missing: {string.Join(", ", missing)})");

        var copying = File.ReadLines(Path.Combine(licences, "openal-soft-COPYING.txt")).Take(3).Select(l => l.Trim()).ToArray();
        Console.WriteLine($"     COPYING: {copying[0]} / {copying[1]}");
        Check(copying[0] == "GNU LIBRARY GENERAL PUBLIC LICENSE" && copying[1].StartsWith("Version 2,"),
            "OpenAL Soft's licence is the GNU Library GPL v2 (not LGPL 2.1)");
    }
}
