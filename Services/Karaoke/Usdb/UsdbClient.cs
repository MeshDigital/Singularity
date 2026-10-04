using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Singularity.Services.Karaoke.Usdb;

/// <summary>A chart in the USDB song list.</summary>
/// <param name="Rating">Stars, 0-5 (0 also when nobody rated it yet).</param>
/// <param name="Views">How often the chart's page was viewed: a rough measure of use.</param>
public sealed record UsdbSong(int Id, string Artist, string Title, IReadOnlyList<string> Languages, int Rating = 0, int Views = 0);

/// <summary>
/// The user's USDB login, encrypted for the current Windows user (like the Soulseek login), in
/// %LOCALAPPDATA%\Singularity\usdb_creds.dat as "user|password".
/// </summary>
public static class UsdbCredentials
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "usdb_creds.dat");

    public static (string User, string Password)? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(FilePath)) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser));
            var parts = payload.Split('|', 2);
            return parts.Length == 2 && parts[0].Length > 0 ? (parts[0], parts[1]) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string user, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The USDB login is stored with Windows data protection.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes($"{user}|{password}"), null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>
/// Client for USDB (usdb.animux.de), the community's UltraStar chart database. It only offers chart
/// text files; audio and video come from elsewhere. Downloading needs an account: this logs in with the
/// user's own (it never registers accounts) and keeps the session. USDB is run by volunteers, so
/// requests are spaced at least <see cref="MinInterval"/> apart and identify the app.
/// </summary>
public sealed class UsdbClient
{
    public const string BaseAddress = "https://usdb.animux.de/";
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.5);
    private const string UserAgent = "Singularity/0.1 (UltraStar karaoke player; personal use)";

    private readonly HttpClient _http;
    private readonly Func<(string User, string Password)?> _credentials;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTime _lastRequest = DateTime.MinValue;
    private bool _loggedIn;

    /// <param name="handler">The HTTP handler; tests pass a fake. It must keep cookies (the session).</param>
    public UsdbClient(Func<(string User, string Password)?> credentials, ILogger logger, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _credentials = credentials;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _http = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary>False when no USDB login is saved.</summary>
    public bool HasLogin => _credentials() is not null;

    public async Task<IReadOnlyList<UsdbSong>> SearchAsync(string artist, string title, CancellationToken ct = default)
    {
        var html = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "?link=list")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["order"] = "rating", ["ud"] = "desc", ["interpret"] = artist.Trim(), ["title"] = title.Trim(), ["limit"] = "30", ["start"] = "0",
            }),
        }, ct);
        return ParseSearch(html);
    }

    /// <summary>The chart's song.txt as text; null when the page has none.</summary>
    public async Task<string?> GetChartAsync(int id, CancellationToken ct = default) =>
        ParseChart(await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"?link=editsongs&id={id}"), ct));

    /// <summary>YouTube video ids people linked on the chart's page (newest first).</summary>
    public async Task<IReadOnlyList<string>> GetYoutubeIdsAsync(int id, CancellationToken ct = default) =>
        ParseYoutubeIds(await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"?link=detail&id={id}"), ct));

    private async Task<string> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loggedIn) await LoginAsync(ct);
            var html = await PacedAsync(request, ct);
            if (LooksLoggedOut(html))
            {
                // The session expired: log in again once.
                _loggedIn = false;
                await LoginAsync(ct);
                html = await PacedAsync(request, ct);
            }
            return html;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var login = _credentials() ?? throw new InvalidOperationException("No USDB login is saved. Add it in Settings to use community charts.");
        var html = await PacedAsync(() => new HttpRequestMessage(HttpMethod.Post, "index.php?link=login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = login.User, ["pass"] = login.Password, ["login"] = "Login" }),
        }, ct);
        if (LooksLoggedOut(html)) throw new InvalidOperationException("USDB didn't accept the saved login.");
        _loggedIn = true;
        _logger.LogInformation("Logged in to USDB");
    }

    private async Task<string> PacedAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        var wait = _lastRequest + MinInterval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await _delay(wait, ct);
        _lastRequest = DateTime.UtcNow;
        using var message = request();
        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Page parsing (the site has no API; these follow UltraStar-CLI's, MIT) ─────────────────────

    /// <summary>A logged-in page offers "logout"; the login form does not.</summary>
    internal static bool LooksLoggedOut(string html) => !html.Contains("logout", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex SearchRow = new(@"<tr class=""list_tr[12].*?>\s*([\s\S]*?)\s*</tr>", RegexOptions.Compiled);
    private static readonly Regex DetailId = new(@"show_detail\((\d+)\)", RegexOptions.Compiled);
    private static readonly Regex Cell = new(@"<td\s+.*?>(?:<a.*?>)?(.*)</td>", RegexOptions.Compiled | RegexOptions.Multiline);

    internal static IReadOnlyList<UsdbSong> ParseSearch(string html)
    {
        var songs = new List<UsdbSong>();
        foreach (Match row in SearchRow.Matches(html))
        {
            var body = row.Groups[1].Value;
            if (!int.TryParse(DetailId.Match(body).Groups[1].Value, out int id)) continue;
            var cells = Cell.Matches(body).Select(m => WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()).ToList();
            if (cells.Count < 2 || cells[0].Length == 0 || cells[1].Length == 0) continue;
            // Columns: artist, title, genre, year, edition, golden notes, language, creator, rating (star
            // images: star.png filled, star2.png empty), views.
            var languages = cells.Count > 6
                ? cells[6].ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            int rating = Regex.Matches(body, @"images/star\.png").Count;
            int views = cells.Count > 9 && int.TryParse(cells[9], out var v) ? v : 0;
            songs.Add(new UsdbSong(id, cells[0], cells[1], languages, rating, views));
        }
        return songs;
    }

    private static readonly Regex TextArea = new(@"<textarea.*?>([\s\S]*)</textarea>", RegexOptions.Compiled);

    internal static string? ParseChart(string html)
    {
        var text = TextArea.Match(html);
        if (!text.Success) return null;
        var chart = WebUtility.HtmlDecode(text.Groups[1].Value).Replace("\r\n", "\n").Trim();
        return chart.Contains("#TITLE", StringComparison.OrdinalIgnoreCase) ? chart + "\n" : null;
    }

    private static readonly Regex YoutubeEmbed = new(@"(?:youtube\.com/(?:embed/|watch\?v=)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

    internal static IReadOnlyList<string> ParseYoutubeIds(string html) =>
        YoutubeEmbed.Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>The YouTube id in a chart's #VIDEO tag, which USDB charts often use for "v=ID,co=..." notes.</summary>
    internal static string? YoutubeIdFromVideoTag(string? video)
    {
        if (string.IsNullOrWhiteSpace(video)) return null;
        var v = Regex.Match(video, @"(?:^|,)v=([A-Za-z0-9_-]{11})(?:,|$)");
        if (v.Success) return v.Groups[1].Value;
        var url = YoutubeEmbed.Match(video);
        return url.Success ? url.Groups[1].Value : null;
    }

    private static string StripTags(string s) => Regex.Replace(s, "<.*?>", "");
}
