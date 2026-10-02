using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Display;

/// <summary>A note bar in lane coordinates: X from 0 (line start) to 1 (line end), Y from 0 (bottom) to 1 (top).</summary>
public readonly record struct LaneNote(double X, double Width, double Y, UltraStarNote Note);

/// <summary>
/// Places one lyric line's notes on the note lane, UltraStar style. Time runs left to right across
/// the line. Pitch maps the line's lowest note to the bottom and its highest to the top, with at
/// least <see cref="MinRangeSemitones"/> of range so flat lines don't stretch. A singer's pitch is
/// folded to the octave nearest the line's notes, matching the octave-independent scoring.
/// </summary>
public sealed class NoteLaneLayout
{
    public const int MinRangeSemitones = 8;

    private readonly double _startBeat, _beats;
    private readonly double _low, _high;

    public NoteLaneLayout(DisplayLine line, IReadOnlyList<UltraStarNote> notes)
    {
        var sung = notes.Where(n => n.Type != NoteType.LineBreak && n.StartBeat >= line.StartBeat && n.StartBeat < line.EndBeat).ToList();
        Notes = sung;
        _startBeat = line.StartBeat;
        _beats = Math.Max(1, line.EndBeat - line.StartBeat);

        int low = sung.Count > 0 ? sung.Min(n => n.MidiTone) : 60, high = sung.Count > 0 ? sung.Max(n => n.MidiTone) : 60;
        double pad = Math.Max(0, MinRangeSemitones - (high - low)) / 2.0;
        _low = low - pad - 0.5; // half a semitone margin so the extreme notes aren't on the edge
        _high = high + pad + 0.5;
    }

    public IReadOnlyList<UltraStarNote> Notes { get; }

    public IEnumerable<LaneNote> Layout() => Notes.Select(n =>
        new LaneNote(XFor(n.StartBeat), n.DurationBeats / _beats, YFor(n.MidiTone), n));

    public double XFor(double beat) => (beat - _startBeat) / _beats;

    public double YFor(double midi) => Math.Clamp((midi - _low) / (_high - _low), 0, 1);

    /// <summary>Where to draw the singer's pitch marker: the sung pitch moved by whole octaves into the line's range.</summary>
    public double SingerY(double sungMidi)
    {
        double centre = (_low + _high) / 2;
        double folded = sungMidi + 12 * Math.Round((centre - sungMidi) / 12);
        return YFor(folded);
    }
}
