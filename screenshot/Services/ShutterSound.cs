using System.IO;
using System.Media;

namespace screenshot.Services;

/// <summary>
/// Camera-shutter click played on every successful capture. The WAV is
/// synthesized in memory (two decaying noise bursts: mirror down / mirror up),
/// so no audio asset ships with the app.
/// </summary>
public static class ShutterSound
{
    private static MemoryStream? _wav;
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            _wav ??= BuildWav();
            _player ??= new SoundPlayer();
            _wav.Position = 0;
            _player.Stream = _wav;
            _player.Play();
        }
        catch { /* sound is a nicety, never a failure */ }
    }

    private static MemoryStream BuildWav()
    {
        const int rate = 44100;
        const double seconds = 0.14;
        int frames = (int)(rate * seconds);
        var rnd = new Random(42);
        var pcm = new short[frames];

        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            double noise = rnd.NextDouble() * 2 - 1;
            // First snap: sharp attack, ~8ms half-life.
            double env = Math.Exp(-t * 120);
            // Softer return click at ~70ms.
            double env2 = t > 0.07 ? Math.Exp(-(t - 0.07) * 140) * 0.5 : 0;
            double s = noise * (env + env2) * 0.55;
            pcm[i] = (short)Math.Clamp(s * short.MaxValue, short.MinValue, short.MaxValue);
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + frames * 2);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);                 // PCM chunk size
        w.Write((short)1);           // PCM format
        w.Write((short)1);           // mono
        w.Write(rate);
        w.Write(rate * 2);           // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits per sample
        w.Write("data".ToCharArray());
        w.Write(frames * 2);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        ms.Position = 0;
        return ms;
    }
}
