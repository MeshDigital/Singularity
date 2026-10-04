using Singularity.Contracts.Song;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class SongClustersTests
{
    private static readonly UltraStarVoice Voice = new(new[] { new UltraStarNote(NoteType.Regular, 0, 4, 0, "la") });

    private static SongEntry Song(string artist, string title, bool duet = false, bool ai = false, string folder = "x") => new(
        $@"D:\songs\{folder}\song.txt",
        new UltraStarSong
        {
            Artist = artist, Title = title, Bpm = 300, AudioFile = "a.mp3",
            Creator = ai ? SongClusters.AiCreator : "someone",
            Voices = duet ? new[] { Voice, Voice } : new[] { Voice },
        },
        $@"D:\songs\{folder}\a.mp3", null, null, null, Array.Empty<string>());

    [Theory]
    [InlineData("Shakira ft. Freshlyground", "Waka Waka (This Time for Africa)", "Shakira", "Waka Waka")]
    [InlineData("The Killers", "Mr. Brightside", "Killers", "Mr Brightside")]
    [InlineData("Beyoncé feat. Jay-Z", "Crazy in Love - Remastered 2011", "BEYONCE", "Crazy In Love")]
    [InlineData("Simon & Garfunkel", "The Boxer [Live]", "Simon", "The Boxer")]
    public void SameSong_SameKey(string artistA, string titleA, string artistB, string titleB) =>
        Assert.Equal(SongClusters.KeyOf(artistA, titleA), SongClusters.KeyOf(artistB, titleB));

    [Theory]
    [InlineData("Sam Smith", "Stay with Me", "Sam Smith", "Stay")]
    [InlineData("Queen", "Bohemian Rhapsody", "Queen", "Radio Ga Ga")]
    [InlineData("Adele", "Hello", "Lionel Richie", "Hello")]
    public void DifferentSongs_DifferentKeys(string artistA, string titleA, string artistB, string titleB) =>
        Assert.NotEqual(SongClusters.KeyOf(artistA, titleA), SongClusters.KeyOf(artistB, titleB));

    [Fact]
    public void Versions_AreOrdered_HumanFirst_ThenAiByTier()
    {
        var songs = new[]
        {
            Song("Shakira", "Waka Waka", ai: true, folder: "ai-b"),
            Song("Shakira ft. Freshlyground", "Waka Waka (This Time for Africa)", folder: "solo"),
            Song("Shakira", "Waka Waka", duet: true, folder: "duet"),
            Song("Shakira", "Waka Waka", ai: true, folder: "ai-aplus"),
            Song("Abba", "Waterloo", folder: "abba"),
        };
        QualityTier? Tier(SongEntry e) => e.Folder.EndsWith("ai-aplus") ? QualityTier.APlus : QualityTier.B;

        var alone = SongClusters.Build(songs, Tier, twoPlayers: false);
        var together = SongClusters.Build(songs, Tier, twoPlayers: true);

        Assert.Equal(new[] { "Abba", "Shakira ft. Freshlyground" }, alone.Select(c => c.Artist));
        string Order(SongCluster c) => string.Join(",", c.Versions.Select(v => Path.GetFileName(v.Entry.Folder)));
        Assert.Equal("solo,duet,ai-aplus,ai-b", Order(alone[1]));
        Assert.Equal("duet,solo,ai-aplus,ai-b", Order(together[1]));
        Assert.Equal("Waka Waka", alone[1].Title);
        Assert.Equal(new[] { "Solo · Community chart", "Duet · Community chart", "Solo · Singularity AI A+", "Solo · Singularity AI B" },
            alone[1].Versions.Select(v => v.Label));
    }

    [Theory]
    [InlineData("Song (Radio Edit)", "Radio Edit")]
    [InlineData("Song - Acoustic Version", "Acoustic Version")]
    [InlineData("Song [Live]", "Live")]
    [InlineData("Waka Waka (This Time for Africa)", null)]
    [InlineData("Song", null)]
    public void VersionNotes_AreRecognised(string title, string? variant) => Assert.Equal(variant, SongClusters.VariantOf(title));
}
