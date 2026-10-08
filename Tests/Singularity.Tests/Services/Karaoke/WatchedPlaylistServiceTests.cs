using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Configuration;
using Singularity.Services.Karaoke.Ingest;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public class WatchedPlaylistServiceTests : IDisposable
{
    private readonly string _ini = Path.Combine(Path.GetTempPath(), $"watched-{Guid.NewGuid():N}.ini");

    public void Dispose()
    {
        if (File.Exists(_ini)) File.Delete(_ini);
    }

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc123", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/intl-nl/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/track/0lvhEsN1zkMzfp2M1o17yy", null)]
    [InlineData("https://open.spotify.com/album/1ATL5GLyefJaxhQzSPVrLX", null)]
    [InlineData("Queen - Bohemian Rhapsody", null)]
    public void OnlyPlaylistLinks_CanBeWatched(string link, string? id) => Assert.Equal(id, WatchedPlaylistService.PlaylistId(link));

    private WatchedPlaylistService Service(AppConfig config) =>
        new(null!, config, new ConfigManager(_ini), NullLogger<WatchedPlaylistService>.Instance); // the ingest service is only used for checks

    [Fact]
    public void Watch_KeepsACleanLinkOnce_AndUnwatchRemovesIt()
    {
        var config = new AppConfig();
        var watched = Service(config);

        Assert.True(watched.Watch("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc123"));
        Assert.False(watched.Watch("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")); // the same playlist
        Assert.False(watched.Watch("https://open.spotify.com/track/0lvhEsN1zkMzfp2M1o17yy"));

        var only = Assert.Single(watched.Playlists);
        Assert.Equal("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", only.Url);
        Assert.DoesNotContain("si=", config.KaraokeWatchedPlaylists);

        watched.Unwatch(only.Url);
        Assert.Empty(watched.Playlists);
    }

    [Fact]
    public void WatchedPlaylists_AreSavedInTheSettings()
    {
        var config = new AppConfig();
        Service(config).Watch("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");
        Service(config).Watch("https://open.spotify.com/playlist/1xh6IzsU5Fs6hRJNE4yTqk");

        var reloaded = new ConfigManager(_ini).Load();
        Assert.Equal(2, Service(reloaded).Playlists.Count);
    }
}
