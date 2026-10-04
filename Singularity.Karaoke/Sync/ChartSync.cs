using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Sync;

/// <summary>
/// Places a community chart on a recording. A chart from USDB was timed against whatever audio its
/// author had, which is rarely the exact file we play: another rip of the same release starts a little
/// earlier or later. When the chart has notes sounding is matched against when the recording's
/// separated vocals are loud, window by window through the song, with the same peak-and-consensus measurement as
/// <see cref="VideoSync"/>. The result says how far to move the chart (recording time = chart time +
/// offset), or that it doesn't fit at all because the recording is another edit.
/// </summary>
public static class ChartSync
{
    /// <summary>Notes are sparser than drum hits, so the windows are longer than the video sync's.</summary>
    public const int WindowMs = 20_000;

    public const int MaxOffsetMs = 30_000;

    /// <summary>Note starts against vocal onsets correlate less than two recordings of one mix.</summary>
    public const double MinCorrelation = 0.15;

    /// <summary>
    /// How closely the windows must agree. Human charts place notes by ear, tens of milliseconds either
    /// side of the sung onset, so this is looser than the video sync's; 60 ms is well inside what a singer
    /// notices.
    /// </summary>
    public const int ToleranceMs = 60;

    /// <summary>Nine windows, as single ones often find no clear peak; three quarters of those that do must agree.</summary>
    public static readonly double[] WindowPositions = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };

    public const double MinInlierShare = 0.75;

    /// <param name="vocals">The recording's separated vocals, mono, at <paramref name="sampleRate"/>.</param>
    public static VideoSyncResult Measure(UltraStarSong chart, ReadOnlySpan<float> vocals, int sampleRate)
    {
        var v = VocalActivity(vocals, sampleRate);
        var c = NoteActivity(chart, v.Length);
        return VideoSync.MeasureEnvelopes(c, v, WindowMs, MaxOffsetMs, MinCorrelation, ToleranceMs, WindowPositions, MinInlierShare);
    }

    /// <summary>
    /// When the chart has a note sounding (every voice, freestyle included, line breaks not), per frame,
    /// normalised: the on/off pattern of lines and pauses is distinctive and long, unlike single onsets.
    /// </summary>
    internal static float[] NoteActivity(UltraStarSong chart, int frames)
    {
        var signal = new float[frames];
        foreach (var note in chart.Voices.SelectMany(v => v.Notes))
        {
            if (note.Type is NoteType.LineBreak) continue;
            int from = (int)Math.Round(chart.BeatToMs(note.StartBeat) / VideoSync.FrameMs);
            int to = (int)Math.Round(chart.BeatToMs(note.StartBeat + note.DurationBeats) / VideoSync.FrameMs);
            for (int f = Math.Max(0, from); f < Math.Min(frames, Math.Max(to, from + 1)); f++) signal[f] = 1;
        }
        return Normalise(signal);
    }

    /// <summary>The vocal stem's loudness per frame (log energy, clipped at a floor), normalised.</summary>
    internal static float[] VocalActivity(ReadOnlySpan<float> audio, int sampleRate)
    {
        int frame = sampleRate * VideoSync.FrameMs / 1000;
        var env = new float[audio.Length / frame];
        for (int f = 0; f < env.Length; f++)
        {
            double energy = 0;
            var span = audio.Slice(f * frame, frame);
            for (int i = 0; i < span.Length; i++) energy += span[i] * (double)span[i];
            env[f] = (float)Math.Max(-6, Math.Log10(energy / frame + 1e-12)); // -60 dB floor: silence is silence
        }
        return Normalise(env);
    }

    private static float[] Normalise(float[] signal)
    {
        if (signal.Length == 0) return signal;
        double mean = signal.Average(x => (double)x);
        double sd = Math.Sqrt(signal.Average(x => (x - mean) * (x - mean))) + 1e-9;
        for (int i = 0; i < signal.Length; i++) signal[i] = (float)((signal[i] - mean) / sd);
        return signal;
    }

    /// <summary>The chart moved by <paramref name="offsetMs"/>: its gap, and the song-time headers that go with it.</summary>
    public static UltraStarSong Shift(UltraStarSong chart, int offsetMs) => chart with
    {
        GapMs = chart.GapMs + offsetMs,
        StartMs = chart.StartMs is { } s ? Math.Max(0, s + offsetMs) : null,
        EndMs = chart.EndMs is { } e ? e + offsetMs : null,
        PreviewStartMs = chart.PreviewStartMs is { } p ? Math.Max(0, p + offsetMs) : null,
    };
}
