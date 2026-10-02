using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;

namespace Singularity.Services.Lyrics;

/// <summary>One LRCLIB record. <see cref="DurationSeconds"/> is the recording length LRCLIB has on file; some records have none.</summary>
public sealed record LrclibLyrics(
    long Id,
    string TrackName,
    string ArtistName,
    string? AlbumName,
    [property: JsonPropertyName("duration")] double? DurationSeconds,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics)
{
    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasPlain => !string.IsNullOrWhiteSpace(PlainLyrics);
    public bool HasLyrics => HasSynced || HasPlain;
}

/// <summary>A lookup result. <see cref="DurationMatches"/> says whether its timing belongs to this recording.</summary>
public sealed record LrclibMatch(LrclibLyrics Lyrics, bool DurationMatches)
{
    private static readonly Regex Timestamp = new(@"\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]|<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    /// <summary>
    /// The best lyrics for the inference worker. Synced lyrics go as LRC only when they were timed for
    /// this recording; they let the worker skip transcription. Lyrics from another edit (a different
    /// length) still have the right words, so they go as plain text and the worker places the lines
    /// itself. Null for instrumentals and records without lyrics.
    /// </summary>
    public (string Text, LyricsKind Kind)? ForWorker()
    {
        var l = Lyrics;
        if (l.Instrumental) return null;
        if (DurationMatches && l.HasSynced) return (l.SyncedLyrics!, LyricsKind.Synced);
        if (l.HasPlain) return (l.PlainLyrics!, LyricsKind.Plain);
        if (l.HasSynced) return (StripTimestamps(l.SyncedLyrics!), LyricsKind.Plain);
        return null;
    }

    internal static string StripTimestamps(string lrc) =>
        string.Join("\n", lrc.Split('\n').Select(line => Timestamp.Replace(line, "").Trim())
            .Where(line => line.Length > 0 && !(line.StartsWith('[') && line.EndsWith(']')))); // [ar:...] tags
}

/// <summary>
/// Client for lrclib.net, a free lyrics database with no API key. LRCLIB has no ISRC lookup.
/// The exact signature endpoint (artist, title, album, duration) is tried first, then a search,
/// preferring results whose duration is within <see cref="DurationToleranceSeconds"/>. When only
/// other edits have lyrics, the closest one is returned with <see cref="LrclibMatch.DurationMatches"/>
/// false: its words are usable, its timing is not.
/// </summary>
public sealed class LrclibClient
{
    public const string DefaultBaseAddress = "https://lrclib.net/";
    public const double DurationToleranceSeconds = 2.0;

    private const string UserAgent = "Singularity/0.1.0 ( https://github.com/MeshDigital/Singularity )";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Attempts per request when LRCLIB is overloaded or briefly down (429, 502, 503, 504).</summary>
    public const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly ILogger<LrclibClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Waits between retries; tests pass one that doesn't sleep.</param>
    public LrclibClient(HttpClient http, ILogger<LrclibClient> logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _http.BaseAddress ??= new Uri(DefaultBaseAddress);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent); // LRCLIB asks clients to identify themselves
    }

    /// <summary>Lyrics for a recording, or null when LRCLIB has none for the song at all.</summary>
    /// <exception cref="HttpRequestException">LRCLIB returned an error other than "not found".</exception>
    public async Task<LrclibMatch?> FindAsync(string artist, string title, string? album, int? durationMs, CancellationToken ct = default)
    {
        if (album is { Length: > 0 } && durationMs is > 0)
        {
            var exact = await GetJsonAsync<LrclibLyrics>(
                $"api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}" +
                $"&album_name={Uri.EscapeDataString(album)}&duration={(durationMs.Value / 1000.0).ToString("0", CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
            if (exact is not null && Matches(exact, durationMs)) return new LrclibMatch(exact, true);
        }

        var results = await GetJsonAsync<List<LrclibLyrics>>(
            $"api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}", ct).ConfigureAwait(false);
        results ??= new List<LrclibLyrics>();
        if (Pick(results, durationMs) is { } timed) return new LrclibMatch(timed, true);

        // No record for this edit: the closest other edit's words are still right.
        var other = results.Where(r => r.HasLyrics)
            .OrderBy(r => durationMs is null || r.DurationSeconds is null ? double.MaxValue : Math.Abs(r.DurationSeconds.Value - durationMs.Value / 1000.0))
            .FirstOrDefault();
        if (other is null) _logger.LogDebug("LRCLIB has no lyrics for {Artist} - {Title}", artist, title);
        return other is null ? null : new LrclibMatch(other, false);
    }

    /// <summary>Among search results, prefer a duration match, then synced lyrics, then the closest duration.</summary>
    internal static LrclibLyrics? Pick(IEnumerable<LrclibLyrics> results, int? durationMs) =>
        results
            .Where(r => Matches(r, durationMs) && (r.Instrumental || r.HasSynced || r.HasPlain))
            .OrderByDescending(r => r.HasSynced)
            .ThenBy(r => durationMs is null || r.DurationSeconds is null ? 0 : Math.Abs(r.DurationSeconds.Value - durationMs.Value / 1000.0))
            .FirstOrDefault();

    // A record without a duration can't be checked against the recording, so it never matches one.
    private static bool Matches(LrclibLyrics r, int? durationMs) =>
        durationMs is null || (r.DurationSeconds is { } d && Math.Abs(d - durationMs.Value / 1000.0) <= DurationToleranceSeconds);

    private async Task<T?> GetJsonAsync<T>(string relativeUri, CancellationToken ct) where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            using var response = await _http.GetAsync(relativeUri, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (attempt < MaxAttempts && IsTransient(response.StatusCode))
            {
                // A free community service: back off (1 s, then 4 s) or as long as it asks.
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(4, attempt - 1));
                _logger.LogDebug("LRCLIB returned {Status}; retrying in {Wait}", (int)response.StatusCode, wait);
                await _delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
