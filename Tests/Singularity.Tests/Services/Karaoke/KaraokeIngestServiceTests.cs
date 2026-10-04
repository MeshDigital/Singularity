using Singularity.Models;
using Singularity.Services.InputParsers;
using Singularity.Services.Karaoke.Ingest;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public class KaraokeIngestServiceTests
{
    [Theory]
    [InlineData("https://open.spotify.com/track/003vvx7Niy0yvhvHt4a68B?si=abc", "003vvx7Niy0yvhvHt4a68B")]
    [InlineData("https://open.spotify.com/intl-nl/track/003vvx7Niy0yvhvHt4a68B", "003vvx7Niy0yvhvHt4a68B")]
    [InlineData("spotify:track:003vvx7Niy0yvhvHt4a68B", "003vvx7Niy0yvhvHt4a68B")]
    [InlineData("https://open.spotify.com/album/4OHNH3sDzIxnmUADXzv2kT", null)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", null)]
    [InlineData("https://open.spotify.com/track/short", null)]
    public void TrackLinks_AreRecognised(string link, string? id) => Assert.Equal(id, SpotifyInputSource.ExtractTrackId(link));

    [Fact]
    public void ADownloadedSpotifyTrack_BecomesAnIngestSource()
    {
        var track = new PlaylistTrack
        {
            Artist = "The Killers", Title = "Mr. Brightside", Album = "Hot Fuss", ResolvedFilePath = @"C:\music\brightside.flac",
            SpotifyTrackId = "003vvx7Niy0yvhvHt4a68B", ISRC = "USIR20400274", CanonicalDuration = 222_973,
            AlbumArtUrl = "https://i.scdn.co/image/x", ReleaseDate = new DateTime(2004, 6, 7),
        };

        var source = KaraokeIngestService.SourceFor(track);

        Assert.Equal(new IngestSource(@"C:\music\brightside.flac", "The Killers", "Mr. Brightside", "Hot Fuss",
            "spotify:track:003vvx7Niy0yvhvHt4a68B", "USIR20400274", 222_973, "https://i.scdn.co/image/x", null, null, 2004), source);
    }

    [Fact]
    public void DetailsFromTheImport_FillWhatTheDatabaseRowLost()
    {
        var atImport = new PlaylistTrack
        {
            Artist = "The Killers", Title = "Mr. Brightside", Album = "Hot Fuss", SpotifyTrackId = "003vvx7Niy0yvhvHt4a68B",
            ISRC = "USIR20400274", CanonicalDuration = 222_973, AlbumArtUrl = "https://i.scdn.co/image/x", ReleaseDate = new DateTime(2004, 6, 7),
        };
        // What the row looked like after ORBIT re-linked the already-downloaded file.
        var row = new PlaylistTrack
        {
            Artist = "The Killers", Title = "Mr. Brightside", Album = "Hot Fuss", SpotifyTrackId = "003vvx7Niy0yvhvHt4a68B",
            AlbumArtUrl = "https://i.scdn.co/image/x", ResolvedFilePath = @"D:\Music\The Killers - Mr. Brightside.flac",
        };

        var source = KaraokeIngestService.SourceFor(row, KaraokeIngestService.TrackDetails.Of(atImport));

        Assert.Equal(@"D:\Music\The Killers - Mr. Brightside.flac", source.AudioPath);
        Assert.Equal("USIR20400274", source.Isrc);
        Assert.Equal(222_973, source.ExpectedDurationMs);
        Assert.Equal(2004, source.Year);
        Assert.Equal("spotify:track:003vvx7Niy0yvhvHt4a68B", source.TrackId);
    }

    [Fact]
    public void ADurationBackfilledInSeconds_IsNotTakenAsMilliseconds()
    {
        var track = new PlaylistTrack { Artist = "A", Title = "B", ResolvedFilePath = "x.mp3", CanonicalDuration = 223 };
        Assert.Null(KaraokeIngestService.SourceFor(track).ExpectedDurationMs);
    }
}
