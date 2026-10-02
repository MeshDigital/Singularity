using System.Globalization;
using System.Text;
using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Library;

/// <summary>A song's medley section, in beats: [StartBeat, EndBeat).</summary>
public readonly record struct MedleyRange(int StartBeat, int EndBeat);

/// <summary>
/// Finds the part of a song to sing in medley mode: the chorus. A chart's own #MEDLEYSTARTBEAT /
/// #MEDLEYENDBEAT win. Otherwise every run of two or more lyric lines that recurs later with the
/// same words is a candidate, and the one with the most repeated material (lines x occurrences)
/// wins, at its first occurrence. The section then grows or shrinks by whole lines to last between
/// <see cref="MinSeconds"/> and <see cref="MaxSeconds"/>.
///
/// Measured on 108 human-tagged charts (tags hidden): a section is found for 103 of them, and 40
/// overlap the human choice by at least half. Many people start a medley where no lyric repeats,
/// so lyrics alone can't do much better. Charts that carry tags always use their own.
/// </summary>
public static class MedleyFinder
{
    public const double MinSeconds = 25;
    public const double MaxSeconds = 40;

    public static MedleyRange? Find(UltraStarSong song)
    {
        if (song.MedleyStartBeat is { } s && song.MedleyEndBeat is { } e && e > s) return new MedleyRange(s, e);
        if (song.IsDuet || song.Voices.Count == 0) return null;

        var lines = Lines(song.Voices[0]);
        var keys = lines.Select(l => Normalize(l.Text)).ToArray();
        bool Same(int a, int b, int k)
        {
            for (int q = 0; q < k; q++)
                if (keys[a + q].Length == 0 || keys[a + q] != keys[b + q]) return false;
            return true;
        }

        (int Start, int Count, int Score)? best = null;
        for (int i = 0; i < lines.Count; i++)
        {
            for (int k = 2; i + k <= lines.Count; k++)
            {
                int occurrences = 1;
                for (int j = i + k; j + k <= lines.Count; j++)
                {
                    if (!Same(i, j, k)) continue;
                    occurrences++;
                    j += k - 1;
                }
                if (occurrences < 2) break; // a longer run can't recur if this one doesn't
                bool seenBefore = false;
                for (int j = 0; j + k <= i && !seenBefore; j++) seenBefore = Same(j, i, k);
                if (seenBefore) break; // only first occurrences are candidates

                int score = k * occurrences;
                if (best is null || score > best.Value.Score) best = (i, k, score);
            }
        }
        if (best is not { } b) return null;

        int last = b.Start + b.Count - 1;
        while (last + 1 < lines.Count && DurationMs(song, lines, b.Start, last) < MinSeconds * 1000) last++;
        while (last > b.Start + 1 && DurationMs(song, lines, b.Start, last) > MaxSeconds * 1000) last--;
        if (DurationMs(song, lines, b.Start, last) < 10_000) return null; // a jingle, not a section
        return new MedleyRange(lines[b.Start].StartBeat, lines[last].EndBeat);
    }

    /// <summary>Sung time from the start of line <paramref name="first"/> to the end of line <paramref name="last"/>.</summary>
    private static double DurationMs(UltraStarSong song, List<Line> lines, int first, int last) =>
        song.BeatToMs(lines[last].EndBeat) - song.BeatToMs(lines[first].StartBeat);

    private sealed record Line(int StartBeat, int EndBeat, string Text);

    private static List<Line> Lines(UltraStarVoice voice)
    {
        var lines = new List<Line>();
        var text = new StringBuilder();
        int start = -1, end = -1;
        void Flush()
        {
            if (start >= 0) lines.Add(new Line(start, end, text.ToString()));
            text.Clear();
            start = end = -1;
        }
        foreach (var n in voice.Notes)
        {
            if (n.Type == NoteType.LineBreak) { Flush(); continue; }
            if (start < 0) start = n.StartBeat;
            end = n.StartBeat + n.DurationBeats;
            text.Append(n.Syllable);
        }
        Flush();
        return lines;
    }

    /// <summary>Letters and digits only, lowercase, without accents: "Hey, Jude!" and "hey jude" match.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
