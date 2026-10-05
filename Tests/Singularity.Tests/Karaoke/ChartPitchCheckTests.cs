using Singularity.Karaoke.Sync;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class ChartPitchCheckTests
{
    private const int Rate = ChartSyncTests.VocalsRate;

    [Fact]
    public void AChartOnTheSinger_Fits()
    {
        var chart = ChartSyncTests.Chart();
        var fit = ChartPitchCheck.Check(chart, ChartSyncTests.Vocals(chart, 0), Rate);

        Assert.True(fit.Fits, $"{fit.Share:P0}");
        Assert.InRange(fit.GapCorrectionMs, -30, 30);
        Assert.Equal(0, fit.Transpose);
    }

    [Fact]
    public void AChartSecondsLate_IsFoundAndMovedBack()
    {
        var chart = ChartSyncTests.Chart();
        var late = ChartSync.Shift(chart, 3600);

        var fit = ChartPitchCheck.Check(late, ChartSyncTests.Vocals(chart, 0), Rate);

        Assert.False(fit.Fits);
        Assert.True(fit.Fixable, $"{fit.Share:P0} -> {fit.BestShare:P0}");
        Assert.InRange(fit.GapCorrectionMs, -3630, -3570);
    }

    [Fact]
    public void AnotherSongsChart_IsNotFixable()
    {
        var fit = ChartPitchCheck.Check(ChartSyncTests.Chart(seed: 1), ChartSyncTests.Vocals(ChartSyncTests.Chart(seed: 2), 0), Rate);
        Assert.False(fit.Fits || fit.Fixable, $"{fit.Share:P0} -> {fit.BestShare:P0}");
    }

    [Fact]
    public void Transpose_MovesEveryNote_NotTheLineBreaks()
    {
        var chart = ChartSyncTests.Chart();
        var up = ChartPitchCheck.Transpose(chart, 2);
        Assert.All(chart.Voices[0].Notes.Zip(up.Voices[0].Notes), p =>
            Assert.Equal(p.First.Type == Singularity.Contracts.UltraStar.NoteType.LineBreak ? p.First.MidiTone : p.First.MidiTone + 2, p.Second.MidiTone));
    }
}
