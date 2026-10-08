using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ReferencePitchTests
{
    private static ReferencePitch Steady(double midi, double fromMs, double toMs, double lengthMs = 10_000) =>
        ReferencePitch.FromReadings(Enumerable.Range((int)(fromMs / 10), (int)((toMs - fromMs) / 10)).Select(f => (f * 10.0, midi)), lengthMs);

    [Fact]
    public void ASteadyNote_IsReadBack_SilenceIsNull()
    {
        var r = Steady(62, 1000, 2000);
        Assert.Equal(62, r.At(1500)!.Value, 3);
        Assert.Null(r.At(500));
        Assert.Null(r.At(2500));
    }

    [Fact]
    public void ASingleFrameSpike_IsRemoved()
    {
        var readings = Enumerable.Range(0, 100).Select(f => (f * 10.0, f == 50 ? 74.0 : 62.0));
        var r = ReferencePitch.FromReadings(readings, 1000);
        Assert.Equal(62, r.At(500)!.Value, 3);
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void FromVocals_ReadsASungTone(int rate)
    {
        var samples = new float[rate * 2];
        double hz = 440 * Math.Pow(2, (57 - 69) / 12.0); // A3
        for (int i = rate / 2; i < rate * 3 / 2; i++) samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / rate));

        var r = ReferencePitch.FromVocals(samples, rate);

        Assert.InRange(r.At(1000)!.Value, 56.8, 57.2);
        Assert.Null(r.At(200));
    }

    // One note: C4 for 8 beats at 100 ms a beat (0..800 ms).
    private static readonly UltraStarVoice Voice = new(new[] { new UltraStarNote(NoteType.Regular, 0, 8, 60, "la") });

    private static int Sing(double sung, ReferencePitch? artist)
    {
        var scorer = new SingScorer(Voice, Difficulty.Medium, lineBonus: false);
        if (artist is not null) scorer.Reference = beat => artist.At(beat * 100);
        for (double beat = 0; beat < 8; beat += 0.25) scorer.AddSample(beat, sung);
        scorer.Finish();
        return scorer.Score.Total;
    }

    [Fact]
    public void SingingWhatTheArtistSang_CountsWhereTheChartIsOff()
    {
        // The chart says C4, the artist sang D4: singing D4 earns nothing against the chart, everything against the artist.
        var artist = Steady(62, 0, 800, 1000);
        Assert.Equal(0, Sing(62, artist: null));
        Assert.Equal(10_000, Sing(62, artist));
    }

    [Fact]
    public void TheArtistFarFromTheNote_IsNotTrusted()
    {
        // 5 semitones from the chart: more likely bleed or a wrong reading than a chart slip.
        var artist = Steady(65, 0, 800, 1000);
        Assert.Equal(0, Sing(65, artist));
    }

    [Fact]
    public void SingingTheChart_StillCounts_WhateverTheArtistDid()
    {
        var artist = Steady(61, 0, 800, 1000);
        Assert.Equal(10_000, Sing(60, artist));
    }

    [Fact]
    public void WrongSinging_GetsNothingFromTheReference()
    {
        var artist = Steady(61, 0, 800, 1000);
        Assert.Equal(Sing(64, artist: null), Sing(64, artist));
    }
}
