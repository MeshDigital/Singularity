using Singularity.Contracts.Inference;

namespace Singularity.Contracts.UltraStar;

/// <summary>
/// Quantizes the worker's timed, pitched syllables onto an UltraStar beat grid: the step between
/// "AI output" and a playable song.txt. Deterministic, so the same analysis always yields the same chart.
/// </summary>
public static class UltraStarChartBuilder
{
    /// <summary>Share of scored notes marked golden — the longest, most confidently pitched ones.</summary>
    public const double GoldenShare = 0.05;

    /// <summary>Below this the grid is too coarse for syllables; the tempo is doubled until it isn't.</summary>
    public const double MinimumGridBpm = 200;

    /// <summary>Grid BPM for a musical tempo: the tempo doubled until beats are short enough (≤ 75 ms).</summary>
    public static double GridBpmFor(double tempoBpm)
    {
        if (tempoBpm <= 0 || double.IsNaN(tempoBpm)) throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        var bpm = tempoBpm;
        while (bpm < MinimumGridBpm) bpm *= 2;
        return Math.Round(bpm, 2);
    }

    /// <summary>Builds the single voice for <paramref name="lines"/>, with #GAP at the first syllable.</summary>
    /// <returns>The grid BPM, the gap and the notes; the caller fills in title, files and the rest of the headers.</returns>
    public static (double Bpm, int GapMs, UltraStarVoice Voice) Build(double tempoBpm, IReadOnlyList<LyricLine> lines)
    {
        var bpm = GridBpmFor(tempoBpm);
        var msPerBeat = 60000.0 / (bpm * 4.0);
        var allSyllables = lines.SelectMany(l => l.Syllables).ToArray();
        if (allSyllables.Length == 0) return (bpm, 0, new UltraStarVoice(Array.Empty<UltraStarNote>()));

        int gap = allSyllables.Min(s => s.StartMs);
        int fallbackTone = MedianTone(allSyllables) ?? UltraStarNote.UltraStarPitchOffset;

        var notes = new List<UltraStarNote>();
        var sources = new List<TimedSyllable>(); // parallel to notes, null-free: only sung notes
        int nextFreeBeat = 0;
        int? lastTone = null;

        for (int li = 0; li < lines.Count; li++)
        {
            var syllables = lines[li].Syllables;
            if (syllables.Count == 0) continue;

            if (notes.Count > 0)
            {
                // The break sits where the previous line's last note ends; the next line can't start earlier.
                notes.Add(UltraStarNote.LineBreak(nextFreeBeat));
            }

            foreach (var s in syllables)
            {
                var text = s.StartsWord && notes.Count > 0 && notes[^1].Type != NoteType.LineBreak ? " " + s.Text : s.Text;

                // A melisma becomes one note per segment: the first carries the text, the rest are "~".
                var parts = s.Segments is { Count: > 1 } segments
                    ? segments.Select((g, i) => (g.StartMs, g.EndMs, (int?)g.MidiTone, Text: i == 0 ? text : "~")).ToArray()
                    : new[] { (s.StartMs, s.EndMs, s.MidiTone, Text: text) };

                foreach (var (startMs, endMs, midiTone, partText) in parts)
                {
                    int start = Math.Max(nextFreeBeat, (int)Math.Round((startMs - gap) / msPerBeat));
                    int end = (int)Math.Round((endMs - gap) / msPerBeat);
                    int length = Math.Max(1, end - start);

                    // Unvoiced or untracked syllables take the previous pitch so the chart has no wild jumps.
                    int tone = midiTone ?? lastTone ?? fallbackTone;
                    lastTone = tone;

                    notes.Add(new UltraStarNote(midiTone is null ? NoteType.Freestyle : NoteType.Regular, start, length, tone, partText));
                    sources.Add(s);
                    nextFreeBeat = start + length;
                }
            }
        }

        MarkGolden(notes, sources);
        return (bpm, gap, new UltraStarVoice(notes));
    }

    private static void MarkGolden(List<UltraStarNote> notes, List<TimedSyllable> sources)
    {
        var sung = notes.Select((n, i) => (Note: n, Index: i)).Where(x => x.Note.Type == NoteType.Regular).ToList();
        int count = (int)Math.Floor(sung.Count * GoldenShare);
        if (count == 0) return;

        // Map note index → source syllable: sources holds sung notes in order, skipping line breaks.
        var sourceByIndex = new Dictionary<int, TimedSyllable>();
        int si = 0;
        for (int i = 0; i < notes.Count; i++)
            if (notes[i].Type != NoteType.LineBreak) sourceByIndex[i] = sources[si++];

        foreach (var (_, index) in sung
                     .OrderByDescending(x => x.Note.DurationBeats * sourceByIndex[x.Index].PitchConfidence)
                     .ThenBy(x => x.Index)
                     .Take(count))
        {
            notes[index] = notes[index] with { Type = NoteType.Golden };
        }
    }

    private static int? MedianTone(IEnumerable<TimedSyllable> syllables)
    {
        var tones = syllables.Where(s => s.MidiTone is not null).Select(s => s.MidiTone!.Value).Order().ToArray();
        return tones.Length == 0 ? null : tones[tones.Length / 2];
    }
}
