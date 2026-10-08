using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;

namespace Singularity.Karaoke.Sync;

/// <summary>A note the original singer clearly sings elsewhere: <see cref="Tone"/> is where they sing it.</summary>
public sealed record NoteCorrection(int Voice, int Index, int Tone);

/// <summary>How a chart's notes compare with what the original singer sang.</summary>
/// <param name="Judged">Notes the singer sings enough of to judge (60% of the note voiced).</param>
/// <param name="Agreeing">Of those, the ones sung within half a semitone of the note (any octave).</param>
/// <param name="LinesToCheck">1-based line numbers (of voice 1) where fewer than half the judged notes agree.</param>
/// <param name="Corrections">Notes the singer sings steadily at another pitch.</param>
public sealed record ChartNoteResult(int Judged, int Agreeing, int Lines, IReadOnlyList<int> LinesToCheck, IReadOnlyList<NoteCorrection> Corrections)
{
    /// <summary>Share of judged notes that agree; 1 when nothing could be judged.</summary>
    public double Agreement => Judged == 0 ? 1 : (double)Agreeing / Judged;

    /// <summary>The chart doesn't fit this recording at all (misplaced, another edit or another song).</summary>
    public bool Mismatch => Judged >= ChartNoteCheck.MinJudged && Agreement < ChartNoteCheck.MismatchBelow;
}

/// <summary>
/// Compares every note of a chart with the original singer's pitch over that note (<see cref="ReferencePitch"/>,
/// from the separated vocals). A note agrees when the singer's median pitch is within half a semitone of it, octaves
/// ignored. A note the singer sings steadily (half the readings within <see cref="SteadySemitones"/>) a semitone or
/// more away is a correction: the note belongs where they sing it.
///
/// Measured on 8 songs with their own vocals: AI and human charts agree on 68-90% of notes, 3-7% are steadily off
/// (human charters write the intended melody, so only AI charts are corrected), and a community chart placed 3.6 s
/// late agreed on 17%, with 72 of its 75 lines to check.
/// </summary>
public static class ChartNoteCheck
{
    /// <summary>A note is judged when the singer is heard over this share of it.</summary>
    public const double MinVoicedShare = 0.6;

    /// <summary>Within this distance (semitones) the singer sings the note.</summary>
    public const double AgreeSemitones = 0.5;

    /// <summary>Half the singer's readings over a note within this spread: a held pitch, not a slide or vibrato.</summary>
    public const double SteadySemitones = 0.6;

    /// <summary>Below this agreement the chart doesn't fit the recording.</summary>
    public const double MismatchBelow = 0.4;

    /// <summary>Fewer judged notes than this is too little to call a mismatch.</summary>
    public const int MinJudged = 40;

    public static ChartNoteResult Check(UltraStarSong chart, ReferencePitch singer)
    {
        int judged = 0, agreeing = 0, lines = 0;
        var linesToCheck = new List<int>();
        var corrections = new List<NoteCorrection>();
        for (int v = 0; v < chart.Voices.Count; v++)
        {
            var notes = chart.Voices[v].Notes;
            int lineJudged = 0, lineAgree = 0, line = 0;
            for (int i = 0; i <= notes.Count; i++)
            {
                if (i == notes.Count || notes[i].Type == NoteType.LineBreak)
                {
                    if (lineJudged > 0)
                    {
                        line++;
                        if (v == 0)
                        {
                            lines++;
                            if (lineJudged >= 2 && lineAgree * 2 < lineJudged) linesToCheck.Add(line);
                        }
                    }
                    lineJudged = lineAgree = 0;
                    continue;
                }
                var n = notes[i];
                if (!n.IsScored || n.IsRap || n.DurationBeats <= 0) continue;
                if (Sung(chart, n, singer) is not { } offsets) continue;

                judged++;
                lineJudged++;
                offsets.Sort();
                double median = offsets[offsets.Count / 2];
                if (Math.Abs(median) < AgreeSemitones)
                {
                    agreeing++;
                    lineAgree++;
                }
                else if (offsets[offsets.Count * 3 / 4] - offsets[offsets.Count / 4] < SteadySemitones)
                {
                    corrections.Add(new NoteCorrection(v, i, n.MidiTone + (int)Math.Round(median)));
                }
            }
        }
        return new ChartNoteResult(judged, agreeing, lines, linesToCheck, corrections);
    }

    /// <summary>The singer's distance from the note (semitones, -6..6) every 10 ms, or null when too little is sung.</summary>
    private static List<double>? Sung(UltraStarSong chart, UltraStarNote note, ReferencePitch singer)
    {
        double from = chart.BeatToMs(note.StartBeat), to = chart.BeatToMs(note.StartBeat + note.DurationBeats);
        int frames = Math.Max(1, (int)((to - from) / ReferencePitch.FrameMs));
        var offsets = new List<double>(frames);
        for (double ms = from; ms < to; ms += ReferencePitch.FrameMs)
        {
            if (singer.At(ms) is not { } midi) continue;
            double d = (midi - note.MidiTone) % 12;
            if (d > 6) d -= 12;
            if (d < -6) d += 12;
            offsets.Add(d);
        }
        return offsets.Count >= frames * MinVoicedShare ? offsets : null;
    }

    /// <summary>The chart with <paramref name="corrections"/> applied.</summary>
    public static UltraStarSong Apply(UltraStarSong chart, IReadOnlyList<NoteCorrection> corrections)
    {
        if (corrections.Count == 0) return chart;
        var byVoice = corrections.ToLookup(c => c.Voice);
        return chart with
        {
            Voices = chart.Voices.Select((voice, v) =>
            {
                var fixes = byVoice[v].ToDictionary(c => c.Index, c => c.Tone);
                return fixes.Count == 0 ? voice : new UltraStarVoice(voice.Notes
                    .Select((n, i) => fixes.TryGetValue(i, out var tone) ? n with { MidiTone = tone } : n).ToArray());
            }).ToArray(),
        };
    }
}
