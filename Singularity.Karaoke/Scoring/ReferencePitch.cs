using Singularity.Karaoke.Pitch;

namespace Singularity.Karaoke.Scoring;

/// <summary>
/// What the original singer sang: their pitch every <see cref="FrameMs"/> ms, from the song's separated vocals, through
/// the same detector as the microphones. A 5-frame median takes out single-frame errors (a consonant read as an octave
/// jump); a frame counts as sung only when most of its neighbours are voiced. <see cref="SingScorer"/> uses it so a
/// singer who sings what the artist sang isn't marked down where the chart's note is a little off (AI charts are wrong
/// by a semitone or two in places).
/// </summary>
public sealed class ReferencePitch
{
    public const int FrameMs = 10;

    private readonly float[] _midi; // per frame; NaN = not sung

    private ReferencePitch(float[] midi) => _midi = midi;

    /// <summary>Frames covered (for tests and logging).</summary>
    public int Frames => _midi.Length;

    /// <param name="readings">Voiced readings: time in ms, MIDI pitch (unvoiced frames simply absent).</param>
    /// <param name="lengthMs">How long the recording is.</param>
    public static ReferencePitch FromReadings(IEnumerable<(double Ms, double Midi)> readings, double lengthMs)
    {
        int frames = Math.Max(0, (int)Math.Ceiling(lengthMs / FrameMs));
        var raw = new float[frames];
        Array.Fill(raw, float.NaN);
        foreach (var (ms, midi) in readings)
        {
            int f = (int)Math.Round(ms / FrameMs);
            if (f >= 0 && f < frames) raw[f] = (float)midi;
        }

        // 5-frame median over the voiced neighbours, kept only where at least 3 of the 5 are voiced.
        var smoothed = new float[frames];
        var window = new List<float>(5);
        for (int f = 0; f < frames; f++)
        {
            window.Clear();
            for (int k = f - 2; k <= f + 2; k++)
                if (k >= 0 && k < frames && !float.IsNaN(raw[k])) window.Add(raw[k]);
            if (window.Count < 3) { smoothed[f] = float.NaN; continue; }
            window.Sort();
            smoothed[f] = window[window.Count / 2];
        }
        return new ReferencePitch(smoothed);
    }

    /// <summary>From mono samples of the separated vocals.</summary>
    public static ReferencePitch FromVocals(ReadOnlySpan<float> vocals, int sampleRate)
    {
        // A voice's fundamental stays under ~1.4 kHz: at 44.1/48 kHz, averaging groups of 3 samples (to ~15-16 kHz)
        // reads the same pitch in a third of the time (3 s -> ~1 s for a song), so the curve is there sooner.
        int factor = Math.Max(1, (int)Math.Round(sampleRate / 16_000.0));
        if (factor > 1)
        {
            var reduced = new float[vocals.Length / factor];
            for (int i = 0; i < reduced.Length; i++)
            {
                float sum = 0;
                for (int k = 0; k < factor; k++) sum += vocals[i * factor + k];
                reduced[i] = sum / factor;
            }
            vocals = reduced;
            sampleRate /= factor;
        }

        var readings = new List<(double, double)>();
        var stream = new PitchStream(sampleRate);
        stream.Frame += (ms, est) => { if (est.IsVoiced) readings.Add((ms, est.Midi)); };
        stream.Push(vocals, 0);
        return FromReadings(readings, vocals.Length * 1000.0 / sampleRate);
    }

    /// <summary>The singer's pitch at <paramref name="ms"/> (song time), or null where they don't sing.</summary>
    public double? At(double ms)
    {
        int f = (int)Math.Round(ms / FrameMs);
        return f >= 0 && f < _midi.Length && !float.IsNaN(_midi[f]) ? _midi[f] : null;
    }
}
