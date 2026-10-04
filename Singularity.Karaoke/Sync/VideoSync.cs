using Singularity.Contracts.Quality;

namespace Singularity.Karaoke.Sync;

/// <summary>One measuring window: where in the master it sat and what offset it found (null when no clear match).</summary>
public sealed record SyncWindow(double MasterMs, int? OffsetMs, double Correlation);

/// <summary>The video offset, if the windows agree, plus what each window found.</summary>
public sealed record VideoSyncResult(VideoGapResult Gap, IReadOnlyList<SyncWindow> Windows);

/// <summary>
/// Measures UltraStar's #VIDEOGAP (video position = audio position + gap) by matching the music video's
/// soundtrack against the master audio. Both are reduced to an onset envelope: the rise in log energy
/// per 10 ms frame. That is robust to the two being different mixes or masterings, and cheap. An
/// 8-second stretch of the master at five points through the song is located in the video by
/// normalised cross-correlation (within ±<see cref="MaxOffsetMs"/>), refined to sub-frame precision
/// with a parabola. The offsets then go through <see cref="VideoGapConsensus.EvaluateWithDrift"/>: if
/// they don't lie on one (gently sloped) line, the video has another arrangement (intro skit, radio
/// edit) and can't be synced with one offset. Five windows rather than three, so one quiet passage
/// without a clear match doesn't sink the measurement.
///
/// Repetitive songs fool a window's best match: a chorus that comes back 40 s later correlates as
/// well as the real spot. So each window keeps its few strongest peaks, the offset most windows have
/// a peak near wins, and each window then takes its peak nearest that offset. A window with no peak
/// near it keeps its own best, so a real structural change still shows up as disagreement.
/// </summary>
public static class VideoSync
{
    public const int FrameMs = 10;
    public const int WindowMs = 8000;
    public const int MaxOffsetMs = 60_000;

    /// <summary>A window whose best match correlates weaker than this found nothing reliable.</summary>
    public const double MinCorrelation = 0.3;

    public static readonly double[] WindowPositions = { 0.1, 0.3, 0.5, 0.7, 0.9 };

    /// <summary>A window's other peaks must reach this share of its best to count as candidates.</summary>
    public const double PeakShare = 0.7;

    /// <summary>Peaks of one window closer than this are the same peak.</summary>
    public const int PeakSeparationMs = 1000;

    /// <summary>Windows support the same offset when their peaks lie this close (allows for drift).</summary>
    public const int SupportToleranceMs = 300;

    /// <param name="master">Master audio, mono, at <paramref name="sampleRate"/>.</param>
    /// <param name="video">The video's soundtrack, mono, same rate.</param>
    public static VideoSyncResult Measure(ReadOnlySpan<float> master, ReadOnlySpan<float> video, int sampleRate)
    {
        var m = OnsetEnvelope(master, sampleRate);
        var v = OnsetEnvelope(video, sampleRate);
        int window = WindowMs / FrameMs, maxLag = MaxOffsetMs / FrameMs;

        var starts = WindowPositions.Select(p => (int)(m.Length * p) - window / 2).ToArray();
        var peaks = starts.Select(start => start < 0 || start + window > m.Length
            ? new List<(double OffsetMs, double Correlation)>()
            : Peaks(m, start, window, v, maxLag)).ToArray();

        // The offset most windows have a peak near; ties go to the stronger peaks.
        var all = peaks.SelectMany(p => p).ToList();
        double? consensus = all.Count == 0
            ? null
            : all.Select(c => (c.OffsetMs, Support: peaks.Count(p => p.Any(q => Math.Abs(q.OffsetMs - c.OffsetMs) <= SupportToleranceMs)),
                               Strength: peaks.Sum(p => p.Where(q => Math.Abs(q.OffsetMs - c.OffsetMs) <= SupportToleranceMs).Select(q => q.Correlation).DefaultIfEmpty(0).Max())))
                 .OrderByDescending(c => c.Support).ThenByDescending(c => c.Strength).First().OffsetMs;

        var windows = new List<SyncWindow>();
        for (int i = 0; i < starts.Length; i++)
        {
            if (peaks[i].Count == 0)
            {
                windows.Add(new SyncWindow(starts[i] * (double)FrameMs, null, 0));
                continue;
            }
            var near = consensus is { } c ? peaks[i].Where(q => Math.Abs(q.OffsetMs - c) <= SupportToleranceMs).ToList() : new();
            var pick = near.Count > 0 ? near.MaxBy(q => q.Correlation) : peaks[i][0];
            windows.Add(new SyncWindow(starts[i] * (double)FrameMs, (int)Math.Round(pick.OffsetMs), pick.Correlation));
        }

        return new VideoSyncResult(VideoGapConsensus.EvaluateWithDrift(windows.Select(w => (w.MasterMs, w.OffsetMs)).ToArray()), windows);
    }

    /// <summary>Positive log-energy differences per frame, normalised to zero mean and unit variance.</summary>
    internal static float[] OnsetEnvelope(ReadOnlySpan<float> audio, int sampleRate)
    {
        int frame = sampleRate * FrameMs / 1000;
        int frames = audio.Length / frame;
        var env = new float[frames];
        double prev = 0;
        for (int f = 0; f < frames; f++)
        {
            double energy = 0;
            var span = audio.Slice(f * frame, frame);
            for (int i = 0; i < span.Length; i++) energy += span[i] * (double)span[i];
            double log = Math.Log10(energy / frame + 1e-10);
            env[f] = (float)Math.Max(0, log - prev);
            prev = log;
        }
        double mean = env.Average(x => (double)x);
        double sd = Math.Sqrt(env.Average(x => (x - mean) * (x - mean))) + 1e-9;
        for (int f = 0; f < frames; f++) env[f] = (float)((env[f] - mean) / sd);
        return env;
    }

    /// <summary>
    /// Where the master segment may sit in the video: the strongest correlation peaks (best first) that
    /// reach <see cref="MinCorrelation"/> and <see cref="PeakShare"/> of the best, as offsets in ms.
    /// Empty when nothing correlates reliably.
    /// </summary>
    private static List<(double OffsetMs, double Correlation)> Peaks(float[] master, int start, int length, float[] video, int maxLag)
    {
        var (lo, scores) = Correlate(master, start, length, video, maxLag);
        var result = new List<(double, double)>();
        if (scores.Length == 0) return result;
        double best = scores.Max();
        if (best < MinCorrelation) return result;
        double floor = Math.Max(MinCorrelation, best * PeakShare);
        int separation = PeakSeparationMs / FrameMs;

        var candidates = new List<int>();
        for (int i = 0; i < scores.Length; i++)
        {
            if (scores[i] < floor) continue;
            if (i > 0 && scores[i - 1] > scores[i]) continue;
            if (i < scores.Length - 1 && scores[i + 1] >= scores[i]) continue;
            candidates.Add(i);
        }
        foreach (int i in candidates.OrderByDescending(i => scores[i]))
        {
            if (result.Count >= 4) break;
            double refined = i;
            if (i > 0 && i < scores.Length - 1)
            {
                double a = scores[i - 1], b = scores[i], c = scores[i + 1];
                double denom = a - 2 * b + c;
                if (Math.Abs(denom) > 1e-12) refined += Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
            }
            double offset = (refined + lo) * FrameMs;
            if (result.Any(r => Math.Abs(r.Item1 - offset) < separation * FrameMs)) continue;
            result.Add((offset, scores[i]));
        }
        return result;
    }

    /// <summary>Normalised cross-correlation of the master segment at every lag within ±maxLag frames.</summary>
    private static (int Lo, double[] Scores) Correlate(float[] master, int start, int length, float[] video, int maxLag)
    {
        // Normalise the master segment once.
        var seg = new double[length];
        double mean = 0;
        for (int i = 0; i < length; i++) mean += master[start + i];
        mean /= length;
        double norm = 0;
        for (int i = 0; i < length; i++) { seg[i] = master[start + i] - mean; norm += seg[i] * seg[i]; }
        norm = Math.Sqrt(norm) + 1e-9;

        int lo = Math.Max(-maxLag, -start), hi = Math.Min(maxLag, video.Length - length - start);
        if (hi < lo) return (0, Array.Empty<double>());
        var scores = new double[hi - lo + 1];
        for (int lag = lo; lag <= hi; lag++)
        {
            int vs = start + lag;
            double vMean = 0;
            for (int i = 0; i < length; i++) vMean += video[vs + i];
            vMean /= length;
            double dot = 0, vNorm = 0;
            for (int i = 0; i < length; i++)
            {
                double x = video[vs + i] - vMean;
                dot += seg[i] * x;
                vNorm += x * x;
            }
            scores[lag - lo] = dot / (norm * (Math.Sqrt(vNorm) + 1e-9));
        }
        return (lo, scores);
    }
}
