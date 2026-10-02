using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Contracts.Inference;
using Singularity.Services.Lyrics;
using Xunit;

namespace Singularity.Tests.Services.Lyrics;

public class LrclibClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Record(long id, double duration, string? synced, string? plain = "plain text", bool instrumental = false) =>
        $$"""{"id":{{id}},"trackName":"Bohemian Rhapsody","artistName":"Queen","albumName":"A Night at the Opera","duration":{{duration.ToString(CultureInfo.InvariantCulture)}},"instrumental":{{(instrumental ? "true" : "false")}},"plainLyrics":{{(plain is null ? "null" : $"\"{plain}\"")}},"syncedLyrics":{{(synced is null ? "null" : $"\"{synced}\"")}}}""";

    private static (LrclibClient, StubHandler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new LrclibClient(new HttpClient(handler), NullLogger<LrclibClient>.Instance), handler);
    }

    [Fact]
    public async Task ExactSignatureMatch_IsUsed()
    {
        var (client, handler) = Create(_ => Json(Record(1, 354, "[00:01.00] Is this the real life")));

        var lyrics = await client.FindAsync("Queen", "Bohemian Rhapsody", "A Night at the Opera", 354_320);

        Assert.Equal(1, lyrics!.Id);
        Assert.Equal(("[00:01.00] Is this the real life", LyricsKind.Synced), lyrics.ForWorker());
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("/api/get?artist_name=Queen&track_name=Bohemian%20Rhapsody&album_name=A%20Night%20at%20the%20Opera&duration=354", request);
    }

    [Fact]
    public async Task NotFound_FallsBackToSearch_PreferringSyncedWithinTolerance()
    {
        var (client, handler) = Create(req => req.RequestUri!.AbsolutePath == "/api/get"
            ? Json("{}", HttpStatusCode.NotFound)
            : Json($"[{Record(1, 354, null)},{Record(2, 300, "[00:01.00] wrong edit")},{Record(3, 355.5, "[00:01.00] right")}]"));

        var lyrics = await client.FindAsync("Queen", "Bohemian Rhapsody", "A Night at the Opera", 354_320);

        Assert.Equal(3, lyrics!.Id); // synced and within 2 s; the 300 s edit is excluded
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WithoutAlbum_GoesStraightToSearch_PlainIsAcceptable()
    {
        var (client, handler) = Create(_ => Json($"[{Record(7, 354, null)}]"));

        var lyrics = await client.FindAsync("Queen", "Bohemian Rhapsody", null, 354_000);

        Assert.Equal(("plain text", LyricsKind.Plain), lyrics!.ForWorker());
        Assert.StartsWith("/api/search?", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task OnlyDurationMismatches_ReturnsNull()
    {
        var (client, _) = Create(_ => Json($"[{Record(1, 200, "[00:01.00] x")}]"));
        Assert.Null(await client.FindAsync("Queen", "Bohemian Rhapsody", null, 354_000));
    }

    [Fact]
    public async Task Instrumental_HasNothingForTheWorker()
    {
        var (client, _) = Create(_ => Json($"[{Record(1, 354, null, plain: null, instrumental: true)}]"));
        var lyrics = await client.FindAsync("Queen", "Bohemian Rhapsody", null, 354_000);
        Assert.True(lyrics!.Instrumental);
        Assert.Null(lyrics.ForWorker());
    }

    [Fact]
    public async Task ServerError_Throws()
    {
        var (client, _) = Create(_ => Json("{}", HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindAsync("a", "b", null, null));
    }

    [Fact]
    public void Pick_WithoutDuration_TakesSyncedFirst()
    {
        var plain = new LrclibLyrics(1, "t", "a", null, 100, false, "p", null);
        var synced = new LrclibLyrics(2, "t", "a", null, 300, false, "p", "[00:01.00] s");
        Assert.Equal(2, LrclibClient.Pick(new[] { plain, synced }, null)!.Id);
    }
}
