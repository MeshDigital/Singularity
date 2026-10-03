using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Singularity.Services.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public class SongPreviewPlayerTests
{
    private static SongEntry Entry(UltraStarSong song) =>
        new("x.txt", song, "x.mp3", null, null, null, Array.Empty<string>());

    private static UltraStarSong Song(int? previewMs = null, int? medleyStart = null, int? medleyEnd = null) => new()
    {
        Title = "t", Artist = "a", AudioFile = "x.mp3", Bpm = 300, GapMs = 1000, // 50 ms per beat
        PreviewStartMs = previewMs, MedleyStartBeat = medleyStart, MedleyEndBeat = medleyEnd,
        Voices = new[] { new UltraStarVoice(new[] { new UltraStarNote(NoteType.Regular, 0, 4, 60, "la") }) },
    };

    [Fact]
    public void ChartPreviewStart_Wins() =>
        Assert.Equal(45_000, SongPreviewPlayer.PreviewStartMs(Entry(Song(previewMs: 45_000, medleyStart: 100, medleyEnd: 900)), 200_000));

    [Fact]
    public void ThenTheMedleySection() =>
        Assert.Equal(1000 + 600 * 50, SongPreviewPlayer.PreviewStartMs(Entry(Song(medleyStart: 600, medleyEnd: 1200)), 200_000));

    [Fact]
    public void ElseAThirdOfTheWayIn() =>
        Assert.Equal(60_000, SongPreviewPlayer.PreviewStartMs(Entry(Song()), 180_000));
}
