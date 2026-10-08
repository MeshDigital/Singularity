using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;

namespace Singularity.Karaoke.Sync;

/// <summary>
/// Sets an AI chart's notes from what the original singer sings: each note the singer is heard over (60% of its middle)
/// takes the median of their pitch over the middle 60% of the note (the slide in and out left out), in the note's own
/// octave. Where that median lies between two semitones (more than <see cref="SnapBeyond"/> from the nearer one) and
/// the nearer one is outside the song's key, the in-key neighbour is taken, as a human charter would. The key comes
/// from the notes themselves (Krumhansl-Kessler profiles).
///
/// ChartBench, 89 songs against their human charts: exact pitch class 65.1% (as the worker made them) -> 70.2%;
/// within a semitone 84.9% -> 85.4%. Without the key, 67.5%; snapping from a quarter-tone on, 69.9% but 84.4% within
/// a semitone.
/// </summary>
public static class ChartMelody
{
    public const double SnapBeyond = 0.35;
    private const double MiddleShare = 0.6;
    private const double MinVoiced = 0.6;

    private static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    private static readonly int[] MajorSteps = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] MinorSteps = { 0, 2, 3, 5, 7, 8, 10 };

    /// <summary>The chart with its notes set from the singer; how many notes changed.</summary>
    public static (UltraStarSong Song, int Changed) FitNotes(UltraStarSong chart, ReferencePitch singer, double? snapBeyond = SnapBeyond)
    {
        int changed = 0;
        var voices = chart.Voices.Select(voice =>
        {
            var notes = voice.Notes.ToArray();
            var medians = new double?[notes.Length];
            var histogram = new double[12];
            for (int i = 0; i < notes.Length; i++)
            {
                if (MiddleMedian(chart, notes[i], singer) is not { } m) continue;
                medians[i] = m;
                histogram[PitchClass((int)Math.Round(m))] += notes[i].DurationBeats;
            }
            var scale = snapBeyond is null ? null : KeyScale(histogram);
            for (int i = 0; i < notes.Length; i++)
            {
                if (medians[i] is not { } m) continue;
                int tone = (int)Math.Round(m);
                if (scale is not null && !scale.Contains(PitchClass(tone)))
                {
                    int other = m > tone ? tone + 1 : tone - 1;
                    if (Math.Abs(m - tone) > snapBeyond && scale.Contains(PitchClass(other))) tone = other;
                }
                if (tone == notes[i].MidiTone) continue;
                notes[i] = notes[i] with { MidiTone = tone };
                changed++;
            }
            return new UltraStarVoice(notes);
        }).ToArray();
        return changed == 0 ? (chart, 0) : (chart with { Voices = voices }, changed);
    }

    /// <summary>The singer's median pitch over the middle of a note, in the note's octave; null when too little is sung.</summary>
    internal static double? MiddleMedian(UltraStarSong chart, UltraStarNote note, ReferencePitch singer)
    {
        if (note.Type is NoteType.LineBreak or NoteType.Freestyle || note.IsRap || note.DurationBeats <= 0) return null;
        double from = chart.BeatToMs(note.StartBeat), to = chart.BeatToMs(note.StartBeat + note.DurationBeats);
        double trim = (to - from) * (1 - MiddleShare) / 2;
        var values = new List<double>();
        int frames = 0;
        for (double ms = from + trim; ms < to - trim; ms += ReferencePitch.FrameMs, frames++)
            if (singer.At(ms) is { } m) values.Add(m + 12 * Math.Round((note.MidiTone - m) / 12));
        if (frames == 0 || values.Count < frames * MinVoiced) return null;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>The pitch classes of the key that best fits a duration-weighted pitch-class histogram.</summary>
    internal static HashSet<int> KeyScale(double[] histogram)
    {
        double best = double.MinValue;
        HashSet<int> scale = new();
        for (int tonic = 0; tonic < 12; tonic++)
        foreach (var (profile, steps) in new[] { (Major, MajorSteps), (Minor, MinorSteps) })
        {
            double score = 0;
            for (int pc = 0; pc < 12; pc++) score += histogram[pc] * profile[PitchClass(pc - tonic)];
            if (score <= best) continue;
            best = score;
            scale = steps.Select(s => (s + tonic) % 12).ToHashSet();
        }
        return scale;
    }

    private static int PitchClass(int tone) => ((tone % 12) + 12) % 12;
}
