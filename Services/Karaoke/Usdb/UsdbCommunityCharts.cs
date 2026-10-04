using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Library;
using Singularity.Services.Karaoke.Ingest;

namespace Singularity.Services.Karaoke.Usdb;

/// <summary>
/// Community charts from USDB for the song import. A hit is used only when its artist and title reduce to
/// the same song as the request (the rules that group versions in the library) with the same version note
/// (an "Alternate Version" is another arrangement), it has at least <see cref="MinimumRating"/> stars, and
/// its chart parses with a real number of notes. Better rated and more used charts are tried first. Whether it fits our recording is
/// decided afterwards, by placing it on the vocals.
/// </summary>
public sealed class UsdbCommunityCharts : ICommunityCharts
{
    /// <summary>
    /// Stars a chart needs. Anyone can upload to USDB; an unrated or poorly rated chart is left to the AI,
    /// which at least knows it isn't sure.
    /// </summary>
    public const int MinimumRating = 3;

    /// <summary>Charts with fewer sung notes are stubs or broken uploads.</summary>
    public const int MinimumNotes = 30;

    /// <summary>Hits checked per request (each costs a page load on a volunteer-run site).</summary>
    public const int MaxCandidates = 3;

    private static readonly Regex Featuring = new(@"\s+(feat\.?|ft\.?|featuring)\s+.*$|\s*(,|&|\+)\s*.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly UsdbClient _usdb;
    private readonly ILogger _logger;

    public UsdbCommunityCharts(UsdbClient usdb, ILogger logger)
    {
        _usdb = usdb;
        _logger = logger;
    }

    public async Task<CommunityChart?> FindAsync(string artist, string title, CancellationToken ct)
    {
        if (!_usdb.HasLogin) return null;
        var key = SongClusters.KeyOf(artist, title);
        var searchArtist = Featuring.Replace(artist, "").Trim();
        var hits = await _usdb.SearchAsync(searchArtist.Length > 0 ? searchArtist : artist, SongClusters.BaseTitle(title), ct);

        var variant = SongClusters.VariantOf(title);
        var candidates = hits
            .Where(h => SongClusters.KeyOf(h.Artist, h.Title) == key && SongClusters.VariantOf(h.Title) == variant && h.Rating >= MinimumRating)
            .OrderByDescending(h => h.Rating).ThenByDescending(h => h.Views)
            .Take(MaxCandidates);
        foreach (var hit in candidates)
        {
            var text = await _usdb.GetChartAsync(hit.Id, ct);
            if (text is null) continue;
            UltraStarSong chart;
            try
            {
                chart = UltraStarSerializer.Read(text);
            }
            catch (FormatException ex)
            {
                _logger.LogInformation("USDB chart {Id} for {Artist} - {Title} doesn't parse: {Error}", hit.Id, artist, title, ex.Message);
                continue;
            }
            if (chart.Voices.Sum(v => v.Notes.Count(n => n.Type != NoteType.LineBreak)) < MinimumNotes) continue;

            _logger.LogInformation("USDB chart {Id} found for {Artist} - {Title}", hit.Id, artist, title);
            return new CommunityChart(chart, $"usdb:{hit.Id}", UsdbClient.YoutubeIdFromVideoTag(chart.VideoFile));
        }
        return null;
    }
}
