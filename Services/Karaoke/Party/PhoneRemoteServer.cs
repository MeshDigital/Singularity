using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using Microsoft.Extensions.Logging;
using Singularity.Karaoke.Party;
using Singularity.ViewModels.Karaoke;

namespace Singularity.Services.Karaoke.Party;

/// <summary>
/// Lets guests' phones browse the songs and queue themselves, in the browser: a small web server on this computer,
/// reached by scanning the QR code on the projector. Nothing to install on the phone.
///
/// Only for the people in the room: it listens only while switched on (Settings), answers only addresses on the local
/// network (private ranges), and every request but the page itself needs the key that is in the QR code, new each time
/// the server starts. Bodies are capped at 4 KB; names are cleaned by <see cref="PartyQueue"/>. EmbedIO's own listener
/// is used, so no administrator rights or URL reservations are needed (Windows asks once whether to allow it on the
/// network).
/// </summary>
public sealed class PhoneRemoteServer : IDisposable
{
    public const int Port = 8765;
    private const int MaxBodyBytes = 4096;
    private const int MaxResults = 60;

    private readonly PartyQueue _queue;
    private readonly Func<IReadOnlyList<SongCardViewModel>> _songs;
    private readonly ILogger<PhoneRemoteServer> _logger;
    private WebServer? _server;
    private CancellationTokenSource? _cts;

    public PhoneRemoteServer(PartyQueue queue, SongSelectViewModel songs, ILogger<PhoneRemoteServer> logger)
        : this(queue, () => songs.AllSongs, logger)
    {
        songs.Phones = this; // the projector shows the QR code from there (song select can't take the server: it needs song select)
    }

    internal PhoneRemoteServer(PartyQueue queue, Func<IReadOnlyList<SongCardViewModel>> songs, ILogger<PhoneRemoteServer> logger)
    {
        _queue = queue;
        _songs = songs;
        _logger = logger;
    }

    /// <summary>The key a request must carry; new each time the server starts.</summary>
    public string Key { get; private set; } = NewKey();

    /// <summary>The address phones open (the QR code), or null while off or without a network.</summary>
    public string? Url { get; private set; }

    public bool IsRunning => _server is not null;

    /// <summary>Raised when the server starts or stops (the projector shows or hides the QR code).</summary>
    public event Action? StateChanged;

    public void Start(bool localOnly = false)
    {
        if (_server is not null) return;
        var address = localOnly ? "127.0.0.1" : LanAddress();
        if (address is null)
        {
            _logger.LogWarning("Phones: no local network found; not starting");
            return;
        }
        Key = NewKey();
        try
        {
            // EmbedIO logs to the console through Swan; Singularity has its own log.
            try { Swan.Logging.Logger.UnregisterLogger<Swan.Logging.ConsoleLogger>(); }
            catch (InvalidOperationException) { } // not registered (already removed)
            _cts = new CancellationTokenSource();
            _server = new WebServer(o => o.WithUrlPrefix(localOnly ? $"http://127.0.0.1:{Port}/" : $"http://*:{Port}/").WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, HandleAsync));
            _ = _server.RunAsync(_cts.Token).ContinueWith(t =>
            {
                if (t.Exception is { } ex) _logger.LogWarning(ex.InnerException ?? ex, "Phones: the server stopped");
            }, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or HttpListenerException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Phones: couldn't start on port {Port}", Port);
            _server?.Dispose();
            _server = null;
            return;
        }
        Url = $"http://{address}:{Port}/?k={Key}";
        if (localOnly) _logger.LogInformation("Phones (this computer only, for testing): {Url}", Url);
        else _logger.LogInformation("Phones: open http://{Address}:{Port}/ (key in the QR code)", address, Port);
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_server is null) return;
        _cts?.Cancel();
        _server.Dispose();
        _server = null;
        _cts = null;
        Url = null;
        _logger.LogInformation("Phones: stopped");
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();

    private async Task HandleAsync(IHttpContext ctx)
    {
        if (!IsLocal(ctx.RemoteEndPoint.Address))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        var path = ctx.RequestedPath.TrimEnd('/');
        if (path.Length == 0 && ctx.Request.HttpVerb == HttpVerbs.Get)
        {
            await ctx.SendStringAsync(PhonePage.Html, "text/html", Utf8);
            return;
        }
        if (!KeyMatches(ctx.Request.Headers["X-Key"] ?? ctx.GetRequestQueryData()["k"]))
        {
            ctx.Response.StatusCode = 403;
            await ctx.SendStringAsync("{\"error\":\"Scan the QR code on the screen again.\"}", "application/json", Utf8);
            return;
        }

        switch (path, ctx.Request.HttpVerb)
        {
            case ("/api/songs", HttpVerbs.Get):
                await Json(ctx, Search(ctx.GetRequestQueryData()["q"]));
                break;
            case ("/api/queue", HttpVerbs.Get):
                await Json(ctx, QueueView());
                break;
            case ("/api/queue", HttpVerbs.Post):
                await Json(ctx, await AddAsync(ctx));
                break;
            default:
                if (path.StartsWith("/api/cover/", StringComparison.Ordinal) && ctx.Request.HttpVerb == HttpVerbs.Get)
                    await CoverAsync(ctx, path["/api/cover/".Length..]);
                else
                    ctx.Response.StatusCode = 404;
                break;
        }
    }

    private static Task Json(IHttpContext ctx, object value) =>
        ctx.SendStringAsync(JsonSerializer.Serialize(value, JsonOptions), "application/json", Utf8);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>UTF-8 without a byte-order mark: JSON parsers outside browsers choke on one.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>A song's id for the phone: a short hash of its folder, stable across rescans.</summary>
    internal static string IdOf(string folder) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..12].ToLowerInvariant();

    internal object Search(string? query)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _songs().Where(c => c.Entry.IsPlayable && words.All(c.SearchKey.Contains)).Take(MaxResults);
        return hits.Select(c => new
        {
            id = IdOf(c.Entry.Folder),
            title = c.Title,
            artist = c.Artist,
            duet = c.Version.IsDuet,
            video = c.Entry.VideoPath is not null,
            cover = c.Entry.CoverPath is not null,
        }).ToList();
    }

    internal object QueueView() => _queue.Items.Select((q, i) => new { place = i + 1, singer = q.Singer, title = q.Title, artist = q.Artist }).ToList();

    private async Task<object> AddAsync(IHttpContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBodyBytes) return new { ok = false, message = "Too much." };
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes];
        int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        try
        {
            var body = JsonSerializer.Deserialize<AddRequest>(new string(buffer, 0, read), JsonOptions);
            return Add(body?.Id, body?.Singer);
        }
        catch (JsonException)
        {
            return new { ok = false, message = "Something went wrong; try again." };
        }
    }

    internal object Add(string? id, string? singer)
    {
        var card = _songs().FirstOrDefault(c => IdOf(c.Entry.Folder) == id);
        if (card is null) return new { ok = false, message = "That song isn't there any more." };
        var (added, refused) = _queue.Add(card.Entry.Folder, card.Title, card.Artist, singer ?? "", from: "phone");
        if (added is null) return new { ok = false, message = refused };
        int place = _queue.Items.ToList().FindIndex(q => q.Id == added.Id) + 1;
        _logger.LogInformation("Phones: {Singer} queued {Title} ({Place} in the queue)", added.Singer, added.Title, place);
        return new { ok = true, message = $"You're number {place}: {added.Title}." };
    }

    private sealed record AddRequest(string? Id, string? Singer);

    private async Task CoverAsync(IHttpContext ctx, string id)
    {
        var card = _songs().FirstOrDefault(c => IdOf(c.Entry.Folder) == id);
        if (card?.Entry.CoverPath is not { } cover || !File.Exists(cover))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        ctx.Response.ContentType = Path.GetExtension(cover).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";
        ctx.Response.Headers["Cache-Control"] = "max-age=3600";
        await using var file = File.OpenRead(cover);
        await file.CopyToAsync(ctx.Response.OutputStream);
    }

    private bool KeyMatches(string? key) =>
        key is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Key));

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>Loopback, private (10/8, 172.16/12, 192.168/16), link-local, or an IPv6 local address.</summary>
    internal static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>This computer's address on the local network: an active Wi-Fi or Ethernet adapter with a gateway.</summary>
    internal static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsLocal(a) && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
