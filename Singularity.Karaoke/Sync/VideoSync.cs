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
/// </summary>
public static class VideoSync
{
    public const int FrameMs = 10;
    public const int WindowMs = 8000;
    public const int MaxOffsetMs = 60_000;

    /// <summary>A window whose best match correlates weaker than this found nothing reliable.</summary>
    public const double MinCorrelation = 0.3;

    public static readonly double[] WindowPositions = { 0.1, 0.3, 0.5, 0.7, 0.9 };

    /// <param name="master">Master audio, mono, at <paramref name="sampleRate"/>.</param>
    /// <param name="video">The video's soundtrack, mono, same rate.</param>
    public static VideoSyncResult Measure(ReadOnlySpan<float> master, ReadOnlySpan<float> video, int sampleRate)
    {
        var m = OnsetEnvelope(master, sampleRate);
        var v = OnsetEnvelope(video, sampleRate);
        int window = WindowMs / FrameMs, maxLag = MaxOffsetMs / FrameMs;

        var windows = new List<SyncWindow>();
        foreach (var position in WindowPositions)
        {
            int start = (int)(m.Length * position) - window / 2;
            if (start < 0 || start + window > m.Length)
            {
                windows.Add(new SyncWindow(start * FrameMs, null, 0));
                continue;
            }
            var (lag, corr) = BestLag(m, start, window, v, maxLag);
            windows.Add(new SyncWindow(start * (double)FrameMs, corr >= MinCorrelation ? (int)Math.Round(lag * FrameMs) : null, corr));
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

    /// <summary>Lag (frames, fractional) of the master segment within the video, and its correlation.</summary>
    private static (double Lag, double Correlation) BestLag(float[] master, int start, int length, float[] video, int maxLag)
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
        if (hi < lo) return (0, 0);
        var scores = new double[hi - lo + 1];
        int best = 0;
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
            double score = dot / (norm * (Math.Sqrt(vNorm) + 1e-9));
            scores[lag - lo] = score;
            if (score > scores[best]) best = lag - lo;
        }

        // Parabolic refinement around the peak.
        double refined = best;
        if (best > 0 && best < scores.Length - 1)
        {
            double a = scores[best - 1], b = scores[best], c = scores[best + 1];
            double denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-12) refined += Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
        }
        return (refined + lo, scores[best]);
    }
}
