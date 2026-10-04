using System;
using System.Threading;
using System.Threading.Tasks;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>Music videos only while the setting is on (read per song, so a change applies at once).</summary>
public sealed class SwitchableVideoFinder(IVideoFinder inner, Func<bool> on) : IVideoFinder
{
    public Task<string?> FindAsync(string artist, string title, int durationMs, string folder, CancellationToken ct) =>
        on() ? inner.FindAsync(artist, title, durationMs, folder, ct) : Task.FromResult<string?>(null);

    public Task<string?> DownloadAsync(string youtubeId, string folder, CancellationToken ct) =>
        on() ? inner.DownloadAsync(youtubeId, folder, ct) : Task.FromResult<string?>(null);
}

/// <summary>Community charts only while the setting is on.</summary>
public sealed class SwitchableCommunityCharts(ICommunityCharts inner, Func<bool> on) : ICommunityCharts
{
    public Task<CommunityChart?> FindAsync(string artist, string title, CancellationToken ct) =>
        on() ? inner.FindAsync(artist, title, ct) : Task.FromResult<CommunityChart?>(null);
}
