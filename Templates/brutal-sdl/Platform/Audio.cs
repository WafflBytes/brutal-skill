using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace BrutalGame.Platform;

public enum Sfx
{
    Start,
    Pickup,
    Restart,
}

/// <summary>
/// Sound effects through SDL_mixer. The game keeps running silently when there is no audio device: creating the
/// device mixer throws, and <see cref="Enabled"/> stays false. Sounds are embedded WAVs (OGG, FLAC and MP3 load the
/// same way), decoded up front so playing one never touches the disk.
/// </summary>
public sealed unsafe class Audio : IDisposable
{
    readonly MixMixer _mixer;
    readonly Dictionary<Sfx, MixAudio> _sounds = [];

    public bool Enabled { get; }
    public string? DisabledReason { get; }

    /// <summary>Opens the default playback device. <paramref name="gain"/> 0 mutes everything (used by --smoke).</summary>
    public static Audio OpenDevice(float gain = 1)
    {
        SdlMixer.Init();
        try
        {
            var mixer = SdlMixer.CreateMixerDevice(0xFFFFFFFFu, null); // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK; no SDL_Init(Audio) needed
            SdlMixer.SetMixerGain(mixer, gain);
            return new Audio(mixer);
        }
        catch (SdlException e)
        {
            return new Audio(e.Message);
        }
    }

    /// <summary>An offline mixer you pull samples from with <see cref="Mix"/>: no device, used by --selftest.</summary>
    public static Audio OpenOffline()
    {
        SdlMixer.Init();
        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = 2, freq = 48000 };
        return new Audio(SdlMixer.CreateMixer(&spec));
    }

    Audio(MixMixer mixer)
    {
        _mixer = mixer;
        Enabled = true;
        foreach (var sfx in Enum.GetValues<Sfx>())
        {
            var bytes = Resources.Read($"sounds/{sfx.ToString().ToLowerInvariant()}.wav");
            fixed (byte* p = bytes) // predecode: true decodes now, so the bytes aren't needed afterwards
                _sounds[sfx] = SdlMixer.LoadAudioIO(_mixer, Sdl.PInvoke.IOFromConstMem(p, (nuint)bytes.Length), predecode: true, closeio: true);
        }
    }

    Audio(string reason)
    {
        DisabledReason = reason;
        SdlMixer.Quit();
    }

    public void Play(Sfx sfx)
    {
        if (Enabled)
            SdlMixer.PlayAudio(_mixer, _sounds[sfx]); // fire-and-forget on an internal track
    }

    /// <summary>Offline mixers only: renders interleaved stereo float samples into <paramref name="buffer"/>.</summary>
    public void Mix(Span<float> buffer)
    {
        fixed (float* p = buffer)
            SdlMixer.Generate(_mixer, p, buffer.Length * sizeof(float));
    }

    public void Dispose()
    {
        if (!Enabled)
            return;
        foreach (var audio in _sounds.Values)
            SdlMixer.DestroyAudio(audio);
        SdlMixer.DestroyMixer(_mixer);
        SdlMixer.Quit();
    }
}
