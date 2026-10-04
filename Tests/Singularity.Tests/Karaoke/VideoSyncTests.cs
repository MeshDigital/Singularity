using Singularity.Karaoke.Sync;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class VideoSyncTests
{
    private const int Rate = 8000;

    /// <summary>A minute-plus of "music": irregular percussive hits over noise, deterministic.</summary>
    private static float[] Music(int seconds, int seed = 3)
    {
        var rng = new Random(seed);
        var x = new float[seconds * Rate];
        for (int i = 0; i < x.Length; i++) x[i] = (float)((rng.NextDouble() - 0.5) * 0.02);
        double t = 0.2;
        while (t < seconds - 0.3)
        {
            int at = (int)(t * Rate);
            double amp = 0.3 + rng.NextDouble() * 0.6;
            for (int i = 0; i < Rate / 20 && at + i < x.Length; i++)
                x[at + i] += (float)(amp * Math.Exp(-i / (Rate * 0.01)) * Math.Sin(i * 0.3));
            t += 0.12 + rng.NextDouble() * 0.4;
        }
        return x;
    }

    /// <summary>The video's soundtrack: the master shifted by <paramref name="gapMs"/> (video = audio + gap), a bit quieter, with its own noise.</summary>
    private static float[] Video(float[] master, int gapMs, int seed = 9)
    {
        var rng = new Random(seed);
        int shift = gapMs * Rate / 1000;
        var v = new float[master.Length + Math.Max(0, shift)];
        for (int i = 0; i < v.Length; i++)
        {
            int src = i - shift;
            v[i] = (float)((src >= 0 && src < master.Length ? master[src] * 0.7 : 0) + (rng.NextDouble() - 0.5) * 0.01);
        }
        return v;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3800)]
    [InlineData(-1250)]
    [InlineData(12345)]
    public void ConstantOffset_IsMeasuredWithinAFrame(int gapMs)
    {
        var master = Music(120);
        var result = VideoSync.Measure(master, Video(master, gapMs), Rate);

        Assert.True(result.Gap.IsValid, string.Join(", ", result.Windows.Select(w => $"{w.OffsetMs}@{w.Correlation:0.00}")));
        Assert.InRange(result.Gap.VideoGapMs, gapMs - 10, gapMs + 10);
    }

    [Fact]
    public void SlightlySlowVideo_IsSyncedMidSongAndReportsTheDrift()
    {
        // The video's soundtrack plays 0.1 % slow (60 ms per minute) and starts 2 s late.
        var master = Music(180);
        const double speed = 0.999;
        var video = new float[(int)(master.Length / speed) + 2 * Rate];
        for (int i = 2 * Rate; i < video.Length; i++)
        {
            int src = (int)((i - 2 * Rate) * speed);
            if (src < master.Length) video[i] = master[src] * 0.7f;
        }

        var result = VideoSync.Measure(master, video, Rate);

        Assert.True(result.Gap.IsValid, string.Join(", ", result.Windows.Select(w => $"{w.OffsetMs}@{w.Correlation:0.00}")));
        // Mid-song (90 s in) the video is 2000 + 90 ms ahead.
        Assert.InRange(result.Gap.VideoGapMs, 2070, 2110);
        Assert.InRange(result.Gap.DriftMsPerMinute, 50, 70);
    }

    [Fact]
    public void ExtendedIntro_InTheMiddle_FailsConsensus()
    {
        // The video inserts 9 s of something else after the first third: later windows land 9 s further on.
        var master = Music(120);
        int cut = master.Length / 3;
        var insert = Music(9, seed: 77);
        var video = master[..cut].Concat(insert).Concat(master[cut..]).ToArray();

        var result = VideoSync.Measure(master, video, Rate);

        Assert.False(result.Gap.IsValid);
        Assert.Equal(0, result.Gap.VideoGapMs);
    }

    [Fact]
    public void UnrelatedVideo_FindsNoReliableMatch()
    {
        var result = VideoSync.Measure(Music(90, seed: 1), Music(90, seed: 2), Rate);
        Assert.False(result.Gap.IsValid);
    }
}
