using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Singularity.Contracts.Song;

namespace Singularity.Karaoke.Library;

/// <summary>One chart of a song: a community (human) chart or one Singularity made, solo or duet.</summary>
/// <param name="IsAi">Made by Singularity's AI (its #CREATOR), rather than charted by a person.</param>
/// <param name="Tier">The AI chart's quality tier from its metadata.json; null for human charts.</param>
public sealed record SongVersion(SongEntry Entry, bool IsAi, QualityTier? Tier)
{
    public bool IsDuet => Entry.Song.IsDuet;

    /// <summary>E.g. "Duet · Community chart" or "Solo · Singularity AI A+ · Radio Edit".</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { IsDuet ? "Duet" : "Solo" };
            parts.Add(IsAi ? "Singularity AI" + (Tier is { } t ? " " + SongClusters.TierText(t) : "") : "Community chart");
            if (SongClusters.VariantOf(Entry.Song.Title) is { } variant) parts.Add(variant);
            else if (!string.IsNullOrWhiteSpace(Entry.Song.Edition)) parts.Add(Entry.Song.Edition!.Trim());
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>All charts of one song, best first (see <see cref="SongClusters.Build"/>).</summary>
public sealed record SongCluster(string Key, IReadOnlyList<SongVersion> Versions)
{
    public SongVersion Primary => Versions[0];
    public string Artist => Primary.Entry.Song.Artist;

    /// <summary>The title without a version note such as "(Radio Edit)".</summary>
    public string Title => SongClusters.BaseTitle(Primary.Entry.Song.Title);
}

/// <summary>
/// Groups a song collection by song, so the same song charted more than once (a community chart and
/// an AI one, solo and duet, album and radio edit) is one entry with versions to choose from, rather
/// than five neighbours in the list. Songs match on artist and title with case, accents, featured
/// artists, a leading "The" and version notes in brackets left out.
/// </summary>
public static class SongClusters
{
    /// <summary>The #CREATOR Singularity writes into the charts it makes.</summary>
    public const string AiCreator = "Singularity AI";

    private static readonly Regex Brackets = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s+-\s+.*$", RegexOptions.Compiled);
    private static readonly Regex Featuring = new(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSeparators = new(@"\s*(,|&|\+|\s+x\s+|\s+vs\.?\s+)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <param name="tierOf">The AI chart's quality tier (from its metadata.json); null when unknown.</param>
    /// <param name="twoPlayers">Two singers are set up: a human duet chart then comes first.</param>
    public static IReadOnlyList<SongCluster> Build(IEnumerable<SongEntry> songs, Func<SongEntry, QualityTier?> tierOf, bool twoPlayers)
    {
        return songs
            .Select(e =>
            {
                bool ai = string.Equals(e.Song.Creator?.Trim(), AiCreator, StringComparison.OrdinalIgnoreCase);
                return new SongVersion(e, ai, ai ? tierOf(e) : null);
            })
            .GroupBy(v => KeyOf(v.Entry.Song.Artist, v.Entry.Song.Title))
            .Select(g => new SongCluster(g.Key, g
                .OrderBy(v => Rank(v, twoPlayers))
                .ThenBy(v => v.Entry.IsPlayable ? 0 : 1)
                .ThenBy(v => v.Entry.TxtPath, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .OrderBy(c => c.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Human duet (with two singers), human solo, human duet (alone), then AI charts by tier.</summary>
    private static int Rank(SongVersion v, bool twoPlayers)
    {
        if (!v.IsAi) return v.IsDuet == twoPlayers ? 0 : 1;
        return 2 + v.Tier switch
        {
            QualityTier.APlus => 0,
            QualityTier.A => 1,
            QualityTier.B => 2,
            QualityTier.ReviewRequired => 3,
            _ => 4,
        };
    }

    /// <summary>"{artist}::{title}", both reduced to what identifies the song.</summary>
    public static string KeyOf(string artist, string title)
    {
        var primary = ArtistSeparators.Split(Featuring.Replace(artist, "")).FirstOrDefault(a => a.Trim().Length > 0) ?? artist;
        var a = Clean(primary);
        if (a.StartsWith("the ", StringComparison.Ordinal)) a = a[4..];
        return a + "::" + Clean(BaseTitle(title));
    }

    /// <summary>The title without bracketed notes, " - Remastered 2011" style suffixes or featured artists.</summary>
    public static string BaseTitle(string title)
    {
        var t = Featuring.Replace(DashSuffix.Replace(Brackets.Replace(title, ""), ""), "").Trim();
        return t.Length > 0 ? t : title.Trim();
    }

    /// <summary>A version note in the title, e.g. "Radio Edit" from "Song (Radio Edit)"; null for none or a subtitle.</summary>
    public static string? VariantOf(string title)
    {
        var notes = Regex.Matches(title, @"[\(\[]([^\)\]]+)[\)\]]|\s-\s(.+)$")
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim())
            .Where(IsVersionNote)
            .ToList();
        return notes.Count > 0 ? string.Join(", ", notes) : null;
    }

    private static readonly string[] VersionWords =
    {
        "edit", "version", "mix", "remix", "acoustic", "live", "remaster", "remastered", "unplugged", "instrumental", "extended", "radio", "single", "album", "demo", "duet", "maxi",
    };

    /// <summary>"Radio Edit" is a version note; "This Time for Africa" is part of the song's name.</summary>
    private static bool IsVersionNote(string note) =>
        Clean(note).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => VersionWords.Contains(w));

    internal static string TierText(QualityTier tier) => tier switch
    {
        QualityTier.APlus => "A+",
        QualityTier.A => "A",
        QualityTier.B => "B",
        _ => "(needs checking)",
    };

    private static string Clean(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return NonWord.Replace(sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace("&", " and "), " ").Trim();
    }
}
