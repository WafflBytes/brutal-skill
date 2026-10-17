// Recipes 7.18-7.20: SDL_image, SDL_ttf and SDL_mixer. All headless: images and text go through a software
// renderer on a plain surface, audio is mixed into a memory buffer with no audio device.
using Brutal.SdlApi;
using Brutal.SdlApi.Image;
using Brutal.SdlApi.Mixer;
using Brutal.SdlApi.Ttf;
using Brutal.StbApi;

static unsafe partial class Recipes
{
    static int LitPixels(SdlSurface* s)
    {
        var rgba = Sdl.PInvoke.ConvertSurface(s, SdlPixelFormat.Abgr8888);
        try
        {
            var lit = 0;
            for (var y = 0; y < rgba->h; y++)
            {
                var row = new ReadOnlySpan<byte>((byte*)rgba->pixels + y * rgba->pitch, rgba->w * 4);
                for (var x = 0; x < row.Length; x += 4)
                    if (row[x + 3] > 0 && (row[x] > 40 || row[x + 1] > 40 || row[x + 2] > 40))
                        lit++;
            }
            return lit;
        }
        finally
        {
            Sdl.PInvoke.DestroySurface(rgba);
        }
    }

    // ---- 7.18 SDL_image ---------------------------------------------------------------------------------------

    public static void SdlImageRecipe()
    {
        // A 4x2 RGBA test image written by Stb, then decoded by SDL_image from memory and from a file.
        const int w = 4, h = 2;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 7 + 3);
        var dir = Path.Combine(Path.GetTempPath(), $"recipecheck-img-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            var pngPath = Path.Combine(dir, "test.png");
            Stb.WritePng(pngPath, w, h, 4, rgba, w * 4);
            var png = File.ReadAllBytes(pngPath);

            // From memory: IMG_Load_IO -> SdlImage.LoadIO (underscores dropped). closeio: true frees the stream.
            SdlSurface* fromMem;
            fixed (byte* p = png)
                fromMem = SdlImage.LoadIO(Sdl.PInvoke.IOFromConstMem(p, (nuint)png.Length), closeio: true);
            Check(fromMem->w == w && fromMem->h == h, "LoadIO dimensions");
            var conv = Sdl.PInvoke.ConvertSurface(fromMem, SdlPixelFormat.Abgr8888); // RGBA byte order
            var same = true;
            for (var y = 0; y < h; y++)
                same &= new ReadOnlySpan<byte>((byte*)conv->pixels + y * conv->pitch, w * 4).SequenceEqual(rgba.AsSpan(y * w * 4, w * 4));
            Sdl.PInvoke.DestroySurface(conv);
            Check(same, "LoadIO pixels match after ConvertSurface(Abgr8888)");

            // From a file, then SavePNG/SaveJPG round trips.
            var fromFile = SdlImage.Load(pngPath);
            Check(fromFile->w == w && fromFile->h == h, "Load(path) dimensions");
            var resaved = Path.Combine(dir, "resaved.png");
            SdlImage.SavePNG(fromFile, resaved);
            var jpg = Path.Combine(dir, "lossy.jpg");
            SdlImage.SaveJPG(fromFile, jpg, 90);
            var back = SdlImage.Load(resaved);
            var backJpg = SdlImage.Load(jpg);
            Check(back->w == w && backJpg->w == w && backJpg->h == h, "PNG and JPG round trips keep dimensions");
            foreach (var s in new[] { fromMem, fromFile, back, backJpg })
                Sdl.PInvoke.DestroySurface(s);

            // Texture straight from a file, on a headless software renderer.
            var target = Sdl.PInvoke.CreateSurface(64, 64, SdlPixelFormat.Abgr8888);
            var renderer = Sdl.Render.CreateSoftwareRenderer(target);
            try
            {
                var tex = SdlImage.LoadTexture(renderer, pngPath);
                Check(!tex.IsNull(), "LoadTexture returns a texture");
                Sdl.Render.DestroyTexture(tex);
            }
            finally
            {
                Sdl.Render.DestroyRenderer(renderer);
                Sdl.PInvoke.DestroySurface(target);
            }

            // Errors throw SdlException (unlike Stb, T1).
            var threw = false;
            try
            {
                SdlImage.Load(Path.Combine(dir, "missing.png"));
            }
            catch (SdlException)
            {
                threw = true;
            }
            Check(threw, "Load of a missing file throws SdlException");

            // WEBP (also AVIF, JXL, TIFF) isn't compiled in: detection is compiled out too, so IsWEBP is false even
            // for a valid header, and LoadIO throws "Unsupported image format".
            var webp = "RIFF\x1a\0\0\0WEBPVP8L\x0d\0\0\0\x2f\0\0\0\x10\x07\x10\x11\x11\x88\x88\xfe\x07\0"u8.ToArray();
            bool isWebp;
            var webpLoaded = false;
            fixed (byte* p = webp)
            {
                var io = Sdl.PInvoke.IOFromConstMem(p, (nuint)webp.Length);
                isWebp = SdlImage.IsWEBP(io);
                try
                {
                    var s = SdlImage.LoadIO(io, closeio: true);
                    webpLoaded = true;
                    Sdl.PInvoke.DestroySurface(s);
                }
                catch (SdlException e)
                {
                    Console.WriteLine($"     WEBP: IsWEBP={isWebp}, LoadIO threw: {e.Message}");
                }
            }
            Check(!isWebp && !webpLoaded, "WEBP: IsWEBP false and LoadIO throws (codec and detection disabled)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 7.19 SDL_ttf -----------------------------------------------------------------------------------------

    static string? FindTestFont()
    {
        // No font ships with the package. Use BRUTAL_TEST_FONT, else a common system font.
        var env = Environment.GetEnvironmentVariable("BRUTAL_TEST_FONT");
        if (!string.IsNullOrEmpty(env))
            return File.Exists(env) ? env : null;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void SdlTtfRecipe()
    {
        var fontPath = FindTestFont() ?? throw new SkipException("no test font (set BRUTAL_TEST_FONT)");
        SdlTtf.Init(); // throws SdlException on failure; no SDL video init needed
        var font = SdlTtf.OpenFont(fontPath, 24);
        try
        {
            int tw, th;
            SdlTtf.GetStringSize(font, "Hello, BRUTAL", 0, &tw, &th); // length 0 = whole string
            Check(tw > 50 && th > 10, $"GetStringSize ({tw}x{th})");
            Check(SdlTtf.GetFontHeight(font) > 10, "GetFontHeight");

            // Path 1: text to a surface (TTF_RenderText_Blended -> RenderTextBlended).
            var surface = SdlTtf.RenderTextBlended(font, "Hello, BRUTAL", 0, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
            Check(surface->w == tw && surface->h == th, "RenderTextBlended size matches GetStringSize");
            Check(LitPixels(surface) > 100, "RenderTextBlended drew glyphs");
            Sdl.PInvoke.DestroySurface(surface);

            // Path 2: renderer text engine (cached glyph atlas; what a game loop wants), on a software renderer.
            var target = Sdl.PInvoke.CreateSurface(320, 64, SdlPixelFormat.Abgr8888);
            var renderer = Sdl.Render.CreateSoftwareRenderer(target);
            var engine = SdlTtf.CreateRendererTextEngine(renderer);
            var text = SdlTtf.CreateText(engine, font, "Score 123", 0);
            try
            {
                SdlTtf.SetTextColor(text, 255, 220, 80, 255);
                SdlTtf.GetTextSize(text, out var w, out var h);
                Check(w > 40 && h > 10, $"GetTextSize ({w}x{h})");
                Sdl.Render.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                Sdl.Render.RenderClear(renderer);
                Check(SdlTtf.DrawRendererText(text, 8, 8), "DrawRendererText");
                SdlTtf.SetTextString(text, "Score 124", 0); // update in place; no new allocation per frame
                Check(SdlTtf.DrawRendererText(text, 8, 32), "DrawRendererText after SetTextString");
                var shot = Sdl.PInvoke.RenderReadPixels(renderer, null);
                Check(LitPixels(shot) > 200, "renderer text engine drew glyphs");
                Sdl.PInvoke.DestroySurface(shot);
            }
            finally
            {
                SdlTtf.DestroyText(text); // order: text -> engine -> renderer
                SdlTtf.DestroyRendererTextEngine(engine);
                Sdl.Render.DestroyRenderer(renderer);
                Sdl.PInvoke.DestroySurface(target);
            }

            var threw = false;
            try
            {
                SdlTtf.OpenFont(fontPath + ".missing", 24);
            }
            catch (SdlException)
            {
                threw = true;
            }
            Check(threw, "OpenFont of a missing file throws SdlException");
        }
        finally
        {
            SdlTtf.CloseFont(font);
            SdlTtf.Quit();
        }
    }

    // ---- 7.20 SDL_mixer ---------------------------------------------------------------------------------------

    static float Rms(ReadOnlySpan<float> s)
    {
        var sum = 0.0;
        foreach (var v in s)
            sum += v * v;
        return (float)Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    static byte[] MakeWav(int rate, float seconds, float hz)
    {
        var frames = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8);
        bw.Write(36 + frames * 2);
        bw.Write("WAVEfmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(rate);
        bw.Write(rate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(frames * 2);
        for (var i = 0; i < frames; i++)
            bw.Write((short)(MathF.Sin(i * hz * MathF.Tau / rate) * 12000));
        bw.Flush();
        return ms.ToArray();
    }

    public static void SdlMixerRecipe()
    {
        // MIX_Init + MIX_CreateMixer need no audio device: the mixer renders into our buffer via Generate.
        SdlMixer.Init();
        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 2, freq = 48000 };
        var mixer = SdlMixer.CreateMixer(&spec);
        var dir = Path.Combine(Path.GetTempPath(), $"recipecheck-mix-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        var buffer = new float[4800 * 2]; // 0.1 s stereo
        try
        {
            int Mix()
            {
                fixed (float* p = buffer)
                    return SdlMixer.Generate(mixer, p, buffer.Length * sizeof(float)); // bytes in, bytes out
            }

            Mix();
            Check(Rms(buffer) == 0, "silence before anything plays");

            // A WAV file (OGG/FLAC/MP3 decode the same way), played on a track.
            var wavPath = Path.Combine(dir, "beep.wav");
            File.WriteAllBytes(wavPath, MakeWav(22050, 0.25f, 440));
            var audio = SdlMixer.LoadAudio(mixer, wavPath, predecode: true);
            var frames = SdlMixer.GetAudioDuration(audio);
            Check(Math.Abs(frames - 5512) <= 2, $"GetAudioDuration is in source sample frames ({frames})");
            var track = SdlMixer.CreateTrack(mixer);
            SdlMixer.SetTrackAudio(track, audio);
            SdlMixer.PlayTrack(track, 0); // 0 = no options (SDL_PropertiesID)
            var bytes = Mix();
            // Generate always writes the whole buffer; it returns the bytes of real audio before any trailing
            // silence, which can be a little short even mid-clip. Use TrackPlaying for "has it finished".
            Check(bytes > 0 && bytes <= buffer.Length * sizeof(float), $"Generate returned real-audio bytes ({bytes} of {buffer.Length * sizeof(float)})");
            Check(Rms(buffer) > 0.05f, $"track audible (rms {Rms(buffer):F3}), resampled 22.05 -> 48 kHz");
            Check(SdlMixer.TrackPlaying(track), "TrackPlaying while it plays");

            // T40: PlayTrack resets the loop count from its options (default 0), so loops set before it are lost.
            SdlMixer.SetTrackLoops(track, -1);
            SdlMixer.PlayTrack(track, 0);
            var loopsAfterPlay = SdlMixer.GetTrackLoops(track);
            Check(loopsAfterPlay == 0, $"T40 still holds: PlayTrack reset SetTrackLoops(-1) to {loopsAfterPlay} (fixed upstream? update the skill)");

            SdlMixer.SetTrackGain(track, 0);
            Mix();
            Check(Rms(buffer) < 1e-4f, "SetTrackGain(0) silences the track");
            SdlMixer.SetTrackGain(track, 1);
            for (var i = 0; i < 4; i++)
                Mix(); // 0.4 s more: past the 0.25 s clip
            Check(!SdlMixer.TrackPlaying(track), "track stops at the end of the clip");

            // Raw PCM from native memory; LoadRawAudio copies it, so the source can be freed straight away.
            const int rawFrames = 24000;
            var raw = (float*)System.Runtime.InteropServices.NativeMemory.Alloc(rawFrames * sizeof(float));
            for (var i = 0; i < rawFrames; i++)
                raw[i] = MathF.Sin(i * 660 * MathF.Tau / 48000) * 0.5f;
            var monoSpec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 1, freq = 48000 };
            var rawAudio = SdlMixer.LoadRawAudio(mixer, raw, rawFrames * sizeof(float), &monoSpec);
            new Span<float>(raw, rawFrames).Clear();
            System.Runtime.InteropServices.NativeMemory.Free(raw);
            SdlMixer.PlayAudio(mixer, rawAudio); // fire-and-forget on an internal track
            Mix();
            Check(Rms(buffer) > 0.1f, $"PlayAudio of copied raw PCM audible after freeing the source (rms {Rms(buffer):F3})");

            // Device mixer on the default playback device, muted. Reported, not failed, when there is no device.
            try
            {
                var device = SdlMixer.CreateMixerDevice(0xFFFFFFFFu, null); // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK
                var deviceAudio = SdlMixer.LoadAudio(device, wavPath, predecode: true);
                var deviceTrack = SdlMixer.CreateTrack(device);
                SdlMixer.SetTrackAudio(deviceTrack, deviceAudio);
                SdlMixer.SetTrackGain(deviceTrack, 0);
                SdlMixer.PlayTrack(deviceTrack, 0);
                SdlMixer.SetTrackLoops(deviceTrack, -1); // after PlayTrack, which resets loops (T40)
                Thread.Sleep(400); // longer than the 0.25 s clip: still playing only if looping works
                var looping = SdlMixer.TrackPlaying(deviceTrack);
                SdlMixer.DestroyTrack(deviceTrack);
                SdlMixer.DestroyAudio(deviceAudio);
                SdlMixer.DestroyMixer(device);
                Console.WriteLine($"     device mixer: created without SDL_Init(Audio); looping track still playing after 0.4 s: {looping}");
                Check(looping, "SetTrackLoops(-1) keeps a device track playing");
            }
            catch (SdlException e)
            {
                Console.WriteLine($"     device mixer unavailable (no audio device?): {e.Message}");
            }

            SdlMixer.DestroyTrack(track);
            SdlMixer.DestroyAudio(audio);
            SdlMixer.DestroyAudio(rawAudio);
        }
        finally
        {
            SdlMixer.DestroyMixer(mixer);
            SdlMixer.Quit();
            Directory.Delete(dir, recursive: true);
        }
    }
}
