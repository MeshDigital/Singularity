using Singularity.Contracts.UltraStar;

namespace Singularity.Tools.ChartBench;

/// <summary>How closely a generated chart matches a human-made one for the same recording.</summary>
/// <param name="Recall100">Share of reference notes with a generated note starting within 100 ms.</param>
/// <param name="Precision100">Share of generated notes starting within 100 ms of a reference note.</param>
/// <param name="Recall50">As <paramref name="Recall100"/>, within 50 ms.</param>
/// <param name="MedianOnsetErrorMs">Median signed onset error of the matches within 150 ms (negative = early).</param>
/// <param name="Coverage">Share of reference sung time overlapped by generated notes.</param>
/// <param name="PitchClass">Time-weighted share of overlaps where both sing the same note name (octave ignored, as UltraStar scores).</param>
/// <param name="PitchWithinSemitone">As <paramref name="PitchClass"/>, allowing one semitone.</param>
/// <param name="Transposition">The whole-chart shift (semitones, -5..+6) that best fits the reference. Non-zero means
/// the reference is in another key than the recording (some human charts are), not that the melody is wrong.</param>
/// <param name="PitchClassTransposed"><paramref name="PitchClass"/> after applying <paramref name="Transposition"/>.</param>
public sealed record ChartComparison(
    int ReferenceNotes,
    int GeneratedNotes,
    double Recall50,
    double Recall100,
    double Precision100,
    double MedianOnsetErrorMs,
    double Coverage,
    double PitchClass,
    double PitchWithinSemitone,
    int Transposition,
    double PitchClassTransposed)
{
    private readonly record struct TimedNote(double StartMs, double EndMs, int Tone);

    /// <summary>Compares sung notes (freestyle excluded from the reference; included as generated onsets).</summary>
    public static ChartComparison Compare(UltraStarSong reference, UltraStarSong generated)
    {
        var r = Notes(reference, includeFreestyle: false);
        var g = Notes(generated, includeFreestyle: false);
        var gAll = Notes(generated, includeFreestyle: true);
        var rStarts = r.Select(n => n.StartMs).Order().ToArray();
        var gStarts = gAll.Select(n => n.StartMs).Order().ToArray();

        var errors = r.Select(n => NearestDelta(gStarts, n.StartMs)).ToArray();
        double Recall(double ms) => r.Count == 0 ? 0 : errors.Count(d => Math.Abs(d) <= ms) / (double)r.Count;
        double precision = gAll.Count == 0 ? 0 : gAll.Count(n => Math.Abs(NearestDelta(rStarts, n.StartMs)) <= 100) / (double)gAll.Count;
        var hits = errors.Where(d => Math.Abs(d) <= 150).Order().ToArray();

        double refTime = r.Sum(n => n.EndMs - n.StartMs), overlapTime = 0, samePc = 0, nearPc = 0;
        var byShift = new double[12]; // overlap time per pitch-class difference (generated - reference)
        foreach (var n in r)
        {
            foreach (var m in g)
            {
                var o = Math.Min(n.EndMs, m.EndMs) - Math.Max(n.StartMs, m.StartMs);
                if (o <= 0) continue;
                overlapTime += o;
                int d = ((m.Tone - n.Tone) % 12 + 12) % 12;
                byShift[d] += o;
                if (d > 6) d -= 12;
                if (d == 0) samePc += o;
                if (Math.Abs(d) <= 1) nearPc += o;
            }
        }
        int bestShift = Array.IndexOf(byShift, byShift.Max());

        return new ChartComparison(
            r.Count, gAll.Count, Recall(50), Recall(100), precision,
            hits.Length > 0 ? hits[hits.Length / 2] : double.NaN,
            refTime > 0 ? Math.Min(1, overlapTime / refTime) : 0,
            overlapTime > 0 ? samePc / overlapTime : 0,
            overlapTime > 0 ? nearPc / overlapTime : 0,
            bestShift > 6 ? bestShift - 12 : bestShift,
            overlapTime > 0 ? byShift[bestShift] / overlapTime : 0);
    }

    private static List<TimedNote> Notes(UltraStarSong song, bool includeFreestyle) =>
        song.Voices.SelectMany(v => v.Notes)
            .Where(n => n.Type != NoteType.LineBreak && (includeFreestyle || n.Type != NoteType.Freestyle))
            .Select(n => new TimedNote(song.BeatToMs(n.StartBeat), song.BeatToMs(n.StartBeat + n.DurationBeats), n.MidiTone))
            .ToList();

    private static double NearestDelta(double[] sortedStarts, double ms)
    {
        if (sortedStarts.Length == 0) return double.MaxValue;
        int i = Array.BinarySearch(sortedStarts, ms);
        if (i >= 0) return 0;
        i = ~i;
        double best = double.MaxValue;
        foreach (var j in new[] { i - 1, i })
            if (j >= 0 && j < sortedStarts.Length && Math.Abs(sortedStarts[j] - ms) < Math.Abs(best))
                best = sortedStarts[j] - ms;
        return best;
    }
}
