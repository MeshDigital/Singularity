using Singularity.Karaoke.Pitch;
using Xunit;

namespace Singularity.Tests.Karaoke;

[Collection(NonParallelCollection.Name)] // the speed test needs the CPU to itself
public class PitchDetectorTests
{
    private const int Rate = 48_000;

    private static float[] Tone(double hz, double amplitude = 0.3, int n = 2048, double harmonics = 0, double noise = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, v = amplitude * Math.Sin(2 * Math.PI * hz * t);
            // A voice-like spectrum: strong upper partials, which push autocorrelation methods an octave off.
            for (int k = 2; k <= 4; k++) v += harmonics * amplitude * Math.Sin(2 * Math.PI * hz * k * t + k);
            v += noise * (rng.NextDouble() * 2 - 1);
            x[i] = (float)v;
        }
        return x;
    }

    [Theory]
    [InlineData(82.41)]   // E2, low bass
    [InlineData(110.0)]   // A2
    [InlineData(220.0)]   // A3
    [InlineData(261.63)]  // C4
    [InlineData(440.0)]   // A4
    [InlineData(987.77)]  // B5, high soprano
    public void PureTones_WithinFiveCents(double hz)
    {
        var p = new PitchDetector(Rate).Detect(Tone(hz));
        Assert.True(p.IsVoiced);
        Assert.InRange(1200 * Math.Log2(p.Hz / hz), -5, 5);
    }

    [Theory]
    [InlineData(110.0)]
    [InlineData(196.0)]
    [InlineData(330.0)]
    public void StrongHarmonicsAndNoise_StayOnTheFundamental(double hz)
    {
        var p = new PitchDetector(Rate).Detect(Tone(hz, harmonics: 0.8, noise: 0.05));
        Assert.InRange(1200 * Math.Log2(p.Hz / hz), -20, 20); // no octave error
    }

    [Fact]
    public void Silence_And_Noise_AreUnvoiced()
    {
        var detector = new PitchDetector(Rate);
        Assert.False(detector.Detect(new float[2048]).IsVoiced);
        Assert.False(detector.Detect(Tone(220, amplitude: 0.001)).IsVoiced); // below the silence gate
        Assert.False(detector.Detect(Tone(220, amplitude: 0, noise: 0.3)).IsVoiced); // loud but aperiodic
    }

    [Fact]
    public void Midi_IsTheNoteNumber()
    {
        var p = new PitchDetector(Rate).Detect(Tone(440));
        Assert.Equal(69, p.Midi, 1);
    }

    [Fact]
    public void FastEnoughForSixSingers()
    {
        var detector = new PitchDetector(Rate);
        var frame = Tone(220, harmonics: 0.5);
        detector.Detect(frame); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 600; // 6 singers x 100 frames per second
        for (int i = 0; i < frames; i++) detector.Detect(frame);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{frames} frames took {sw.Elapsed.TotalMilliseconds:0} ms");
    }
}
