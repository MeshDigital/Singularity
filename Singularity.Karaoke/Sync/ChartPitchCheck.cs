using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Pitch;

namespace Singularity.Karaoke.Sync;

/// <summary>How well a chart fits the original singer, and the correction that would fit it best.</summary>
/// <param name="Share">Of the singer's voiced time, the share that lands on a note within half a semitone (any octave), as placed.</param>
/// <param name="BestShare">The same share after the best correction below.</param>
/// <param name="GapCorrectionMs">Add this to the chart's #GAP for the best fit (0 = the timing is right).</param>
/// <param name="Transpose">Add this many semitones to every note for the best fit (0 = the key is right).</param>
public sealed record ChartFit(double Share, double BestShare, int GapCorrectionMs, int Transpose)
{
    /// <summary>The chart sits on the singer as it is.</summary>
    public bool Fits => Share >= ChartPitchCheck.GoodShare;

    /// <summary>The chart doesn't fit, but a timing or key correction makes it fit clearly.</summary>
    public bool Fixable => !Fits && BestShare >= ChartPitchCheck.GoodShare && BestShare - Share >= ChartPitchCheck.FixGain
        && BestShare >= Share * ChartPitchCheck.FixRatio;
}

/// <summary>
/// Checks a chart against the original singer: the separated vocals are run through the same pitch detector as
/// the microphones, and every voiced reading is compared with the note the chart has at that moment. A chart
/// made for this recording puts most of the singing on its notes; one that is a few seconds out (or made for
/// another edit, or in another key) scatters it over every semitone. It also searches timing shifts
/// (±<see cref="SearchMs"/>) and transpositions for the correction that fits best. This catches what
/// <see cref="ChartSync"/>'s loudness matching can't: the notes themselves.
///
/// Calibrated on singers' own vocals (7 songs): charts that fit put 48-70% of the voiced singing on their notes
/// (ad-libs, slides and backing vocals make up the rest); a community chart placed 3.6 s late put 6% there, and
/// 36% once the search had found the 3.6 s.
/// </summary>
public static class ChartPitchCheck
{
    public const int SampleRate = 16_000;

    /// <summary>A reading this close to the note's pitch class counts as on it.</summary>
    public const double OnNoteSemitones = 0.5;

    /// <summary>A chart that puts this much of the singing on its notes fits.</summary>
    public const double GoodShare = 0.30;

    /// <summary>A correction is only taken when it then fits, gains this much and at least multiplies the share by <see cref="FixRatio"/>.</summary>
    public const double FixGain = 0.15, FixRatio = 1.5;

    public const int SearchMs = 10_000;
    private const int CoarseStepMs = 50, FineStepMs = 10;

    /// <summary>Voiced pitch readings (time in ms, MIDI) of a vocal recording.</summary>
    public static IReadOnlyList<(double Ms, double Midi)> Readings(ReadOnlySpan<float> vocals, int sampleRate)
    {
        var readings = new List<(double, double)>();
        var stream = new PitchStream(sampleRate);
        stream.Frame += (ms, est) => { if (est.IsVoiced) readings.Add((ms, est.Midi)); };
        stream.Push(vocals, 0);
        return readings;
    }

    public static ChartFit Check(UltraStarSong chart, ReadOnlySpan<float> vocals, int sampleRate) =>
        Check(chart, Readings(vocals, sampleRate));

    public static ChartFit Check(UltraStarSong chart, IReadOnlyList<(double Ms, double Midi)> readings)
    {
        var notes = chart.Voices.SelectMany(v => v.Notes)
            .Where(n => n.IsScored && !n.IsRap && n.DurationBeats > 0)
            .Select(n => (Start: chart.BeatToMs(n.StartBeat), End: chart.BeatToMs(n.StartBeat + n.DurationBeats), Tone: n.MidiTone))
            .OrderBy(n => n.Start).ToArray();
        if (notes.Length == 0 || readings.Count == 0) return new ChartFit(0, 0, 0, 0);

        var asPlaced = OnNote(notes, readings, 0);
        double share = asPlaced[0];

        // Coarse search over timing, every transposition at once; then refine the best timing.
        (double Share, int Shift, int Transpose) best = (share, 0, 0);
        for (int shift = -SearchMs; shift <= SearchMs; shift += CoarseStepMs)
            Consider(shift);
        int coarse = best.Shift;
        for (int shift = coarse - CoarseStepMs; shift <= coarse + CoarseStepMs; shift += FineStepMs)
            Consider(shift);

        // Reading at t matches the chart at t + shift: the chart is `shift` late, so its gap comes down by it.
        return new ChartFit(share, best.Share, -best.Shift, best.Transpose);

        void Consider(int shift)
        {
            var byTranspose = OnNote(notes, readings, shift);
            for (int t = 0; t < 12; t++)
            {
                // Prefer no correction when it is as good: a tie keeps the chart as it is.
                bool better = byTranspose[t] > best.Share + 1e-9;
                if (better) best = (byTranspose[t], shift, t > 6 ? t - 12 : t);
            }
        }
    }

    /// <summary>
    /// For each transposition 0..11 (semitones added to the notes), the share of readings on a note when the
    /// chart is read <paramref name="shiftMs"/> later than the singing.
    /// </summary>
    private static double[] OnNote((double Start, double End, int Tone)[] notes, IReadOnlyList<(double Ms, double Midi)> readings, int shiftMs)
    {
        var counts = new double[12];
        int j = 0;
        foreach (var (ms, midi) in readings)
        {
            double t = ms + shiftMs;
            while (j < notes.Length && notes[j].End <= t) j++;
            if (j >= notes.Length) break;
            if (t < notes[j].Start) continue;
            // The transposition that puts this reading on the note, if it is within half a semitone of one.
            double diff = midi - notes[j].Tone;
            int nearest = (int)Math.Round(diff);
            if (Math.Abs(diff - nearest) <= OnNoteSemitones) counts[((nearest % 12) + 12) % 12]++;
        }
        for (int t = 0; t < 12; t++) counts[t] /= readings.Count;
        return counts;
    }

    /// <summary>The chart with every note moved by <paramref name="semitones"/>.</summary>
    public static UltraStarSong Transpose(UltraStarSong chart, int semitones) => semitones == 0 ? chart : chart with
    {
        Voices = chart.Voices.Select(v => new UltraStarVoice(v.Notes
            .Select(n => n.Type == NoteType.LineBreak ? n : n with { MidiTone = n.MidiTone + semitones }).ToArray())).ToArray(),
    };
}
