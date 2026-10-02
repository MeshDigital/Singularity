using Singularity.Karaoke.Calibration;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class LatencyCalibratorTests
{
    private const int Rate = 48_000;

    /// <summary>What a microphone hears: the click track delayed, quieter, with room noise and an echo.</summary>
    private static float[] Record(float[] played, double delayMs, double gain = 0.2, double noise = 0.005, double echoMs = 0, int seed = 7)
    {
        var rng = new Random(seed);
        var recorded = new float[played.Length];
        int delay = (int)(delayMs * Rate / 1000), echo = (int)(echoMs * Rate / 1000);
        for (int i = 0; i < recorded.Length; i++)
        {
            double x = noise * (rng.NextDouble() * 2 - 1);
            if (i - delay >= 0) x += gain * played[i - delay];
            if (echoMs > 0 && i - delay - echo >= 0) x += 0.3 * gain * played[i - delay - echo];
            recorded[i] = (float)x;
        }
        return recorded;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(120)]
    [InlineData(310)]
    public void FindsTheDelay(double delayMs)
    {
        var (track, clicks) = LatencyCalibrator.CreateClickTrack(Rate);
        var estimate = LatencyCalibrator.Estimate(Record(track, delayMs), Rate, clicks);

        Assert.True(estimate.IsReliable, estimate.ToString());
        Assert.InRange(estimate.LatencyMs, delayMs - 3, delayMs + 3);
        Assert.Equal(clicks.Length, estimate.ClicksHeard);
    }

    [Fact]
    public void RoomEcho_DoesNotMoveTheEstimate()
    {
        var (track, clicks) = LatencyCalibrator.CreateClickTrack(Rate);
        var estimate = LatencyCalibrator.Estimate(Record(track, 80, echoMs: 40), Rate, clicks);
        Assert.InRange(estimate.LatencyMs, 77, 83); // the first arrival, not the reflection
    }

    [Fact]
    public void MicrophoneThatHearsNothing_IsUnreliable()
    {
        var (track, clicks) = LatencyCalibrator.CreateClickTrack(Rate);
        var estimate = LatencyCalibrator.Estimate(Record(track, 100, gain: 0), Rate, clicks);
        Assert.False(estimate.IsReliable);
    }

    [Fact]
    public void ClickTrack_HasUnevenGaps()
    {
        var (_, clicks) = LatencyCalibrator.CreateClickTrack(Rate);
        var gaps = clicks.Zip(clicks.Skip(1), (a, b) => b - a).Distinct().Count();
        Assert.True(gaps > 2);
    }
}
