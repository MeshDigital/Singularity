using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Services.Karaoke.Usdb;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

/// <summary>Against made-up pages shaped like USDB's; no real site data or accounts.</summary>
public class UsdbClientTests
{
    private const string LoggedIn = "<html><a href=\"?link=logout\">Logout</a>";
    private const string LoginForm = "<html><form><input name=\"user\"><input name=\"pass\"></form>";

    private static string SearchPage(params (int Id, string Artist, string Title, string Languages)[] songs) =>
        LoggedIn + "<br>There are 2 results on 1 page<table>" + string.Concat(songs.Select((s, i) =>
            $"<tr class=\"list_tr{i % 2 + 1}\">\n" +
            $"<td onclick=\"show_detail({s.Id})\">{s.Artist}</td>\n<td onclick=\"show_detail({s.Id})\"><a href=\"#\">{s.Title}</a></td>\n<td class=\"c\">2010</td>\n<td class=\"c\">x</td>\n" +
            $"<td class=\"c\">4.5</td>\n<td class=\"c\">99</td>\n<td class=\"c\">{s.Languages}</td>\n" +
            $"<td onclick=\"show_detail({s.Id})\">Someone</td>\n<td onclick=\"show_detail({s.Id})\">{Stars(5 - i * 5)}</td>\n" +
            $"<td onclick=\"show_detail({s.Id})\">{4418 - i * 3990}</td>\n</tr>")) + "</table>";

    /// <summary>The rating cell: filled stars are star.png, empty ones star2.png.</summary>
    private static string Stars(int filled) =>
        string.Concat(Enumerable.Range(0, 5).Select(i => i < filled ? "<img src=\"images/star.png\"> " : "<img src=\"images/star2.png\"> "));

    [Fact]
    public void Search_ParsesRows()
    {
        var songs = UsdbClient.ParseSearch(SearchPage((295, "The Killers", "Mr. Brightside", "English"), (29484, "Killers &amp; Co", "Mr. Brightside", "English, German")));

        Assert.Equal(new[] { 295, 29484 }, songs.Select(s => s.Id));
        Assert.Equal("Killers & Co", songs[1].Artist);
        Assert.Equal(new[] { "english", "german" }, songs[1].Languages);
        Assert.Equal((5, 4418), (songs[0].Rating, songs[0].Views));
        Assert.Equal((0, 428), (songs[1].Rating, songs[1].Views));
    }

    [Fact]
    public void Chart_IsTheDecodedTextArea()
    {
        var chart = UsdbClient.ParseChart(LoggedIn + "<textarea name=\"txt\">#TITLE:Rock &amp; Roll\r\n#ARTIST:A\r\n: 0 4 0 la\r\nE</textarea>");

        Assert.Equal("#TITLE:Rock & Roll\n#ARTIST:A\n: 0 4 0 la\nE\n", chart);
        Assert.Null(UsdbClient.ParseChart(LoggedIn + "<textarea></textarea>"));
    }

    [Theory]
    [InlineData("v=dQw4w9WgXcQ,co=cover.jpg", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("Some Song.mp4", null)]
    [InlineData(null, null)]
    public void YoutubeId_FromTheVideoTag(string? tag, string? id) => Assert.Equal(id, UsdbClient.YoutubeIdFromVideoTag(tag));

    [Fact]
    public void YoutubeIds_FromTheDetailPage() =>
        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" },
            UsdbClient.ParseYoutubeIds("<iframe src=\"https://www.youtube.com/embed/aaaaaaaaaaa\"></iframe> <a href=\"https://youtu.be/bbbbbbbbbbb\">x</a> https://www.youtube.com/embed/aaaaaaaaaaa"));

    [Fact]
    public async Task LogsInOnce_PacesRequests_AndLogsInAgainWhenTheSessionExpires()
    {
        var pages = new Queue<string>(new[] { LoggedIn, SearchPage((1, "A", "B", "English")), LoginForm, LoggedIn, LoggedIn + "<textarea>#TITLE:B\n#ARTIST:A</textarea>" });
        var handler = new FakeHandler(_ => pages.Dequeue());
        var delays = new List<TimeSpan>();
        var client = new UsdbClient(() => ("me", "secret"), NullLogger.Instance, handler, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var songs = await client.SearchAsync("A", "B");
        var chart = await client.GetChartAsync(1);

        Assert.Single(songs);
        Assert.StartsWith("#TITLE:B", chart);
        Assert.Equal(new[] { "index.php?link=login", "?link=list", "?link=editsongs&id=1", "index.php?link=login", "?link=editsongs&id=1" },
            handler.Requests.Select(r => r.RequestUri!.PathAndQuery.TrimStart('/')));
        Assert.Contains("user=me", handler.Bodies[0]);
        Assert.All(delays, d => Assert.True(d <= UsdbClient.MinInterval));
        Assert.Equal(4, delays.Count); // every request after the first waited its turn
    }

    [Fact]
    public async Task WithoutALogin_ItSaysSo()
    {
        var client = new UsdbClient(() => null, NullLogger.Instance, new FakeHandler(_ => LoggedIn));

        Assert.False(client.HasLogin);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("A", "B"));
        Assert.Contains("login", ex.Message);
    }

    [Fact]
    public async Task ARejectedLogin_IsReported()
    {
        var client = new UsdbClient(() => ("me", "wrong"), NullLogger.Instance, new FakeHandler(_ => LoginForm), (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("A", "B"));
        Assert.Contains("didn't accept", ex.Message);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) };
        }
    }
}
