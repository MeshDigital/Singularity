namespace Singularity.Karaoke.Calibration;

/// <summary>Outcome of a calibration run.</summary>
/// <param name="LatencyMs">Median delay from a click being played to it arriving in the recording.</param>
/// <param name="SpreadMs">Median absolute deviation of the per-click delays: small means trustworthy.</param>
/// <param name="ClicksHeard">How many of the played clicks were found.</param>
public sealed record LatencyEstimate(double LatencyMs, double SpreadMs, int ClicksHeard, int ClicksPlayed)
{
    /// <summary>Enough clicks heard, and they agree.</summary>
    public bool IsReliable => ClicksHeard >= Math.Max(4, ClicksPlayed * 2 / 3) && SpreadMs <= LatencyCalibrator.MaxSpreadMs;
}

/// <summary>
/// Measures the round trip from speakers to microphone. The app plays <see cref="CreateClickTrack"/>
/// while recording the microphone from the same moment. <see cref="Estimate"/> then finds each
/// click's onset in the recording and reports the median delay. The result is the
/// <see cref="SingerSession.LatencyMs"/> to use, give or take the speaker's own output latency,
/// which the app adds. Clicks are spaced irregularly so a delay can't be mistaken for one that is
/// a whole click period longer.
/// </summary>
public static class LatencyCalibrator
{
    public const double MaxSpreadMs = 10;
    public const double ClickMs = 15;
    public const double MaxLatencyMs = 400;

    /// <summary>Gaps between clicks, ms; uneven on purpose.</summary>
    private static readonly int[] Gaps = { 600, 700, 550, 800, 650, 750, 600, 700 };

    /// <summary>A click track (1 kHz bursts) and the time each click starts, in ms from the track's start.</summary>
    public static (float[] Samples, double[] ClickTimesMs) CreateClickTrack(int sampleRate, float amplitude = 0.5f)
    {
        var times = new List<double>();
        double t = 500;
        foreach (var gap in Gaps)
        {
            times.Add(t);
            t += gap;
        }
        var samples = new float[(int)((t + MaxLatencyMs + 500) * sampleRate / 1000)];
        int clickLength = (int)(ClickMs * sampleRate / 1000);
        foreach (var start in times)
        {
            int s0 = (int)(start * sampleRate / 1000);
            for (int i = 0; i < clickLength; i++)
            {
                double envelope = Math.Sin(Math.PI * i / clickLength); // no hard edges
                samples[s0 + i] = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
            }
        }
        return (samples, times.ToArray());
    }

    /// <param name="recorded">Mono microphone audio, recorded from the moment the click track started playing.</param>
    public static LatencyEstimate Estimate(ReadOnlySpan<float> recorded, int sampleRate, IReadOnlyList<double> clickTimesMs)
    {
        // Short-window energy envelope (1 ms hops, 5 ms windows).
        int hop = Math.Max(1, sampleRate / 1000), window = hop * 5;
        int frames = Math.Max(0, (recorded.Length - window) / hop);
        var energy = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < window; i++)
            {
                double x = recorded[f * hop + i];
                sum += x * x;
            }
            energy[f] = sum / window;
        }
        if (frames == 0) return new LatencyEstimate(0, double.MaxValue, 0, clickTimesMs.Count);

        // Noise floor from the quietest tenth of the recording; an onset must clear it by 20 dB.
        var sorted = energy.Order().ToArray();
        double floor = Math.Max(sorted[sorted.Length / 10], 1e-10);
        double threshold = floor * 100;

        var delays = new List<double>();
        foreach (var click in clickTimesMs)
        {
            int from = (int)click, to = Math.Min(frames - 1, (int)(click + MaxLatencyMs));
            for (int f = Math.Max(0, from); f <= to; f++)
            {
                if (energy[f] < threshold) continue;
                delays.Add(f - click); // frame index = ms
                break;
            }
        }
        if (delays.Count == 0) return new LatencyEstimate(0, double.MaxValue, 0, clickTimesMs.Count);

        double median = Median(delays);
        double spread = Median(delays.Select(d => Math.Abs(d - median)).ToList());
        return new LatencyEstimate(median, spread, delays.Count, clickTimesMs.Count);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
