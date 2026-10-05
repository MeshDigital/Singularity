using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Display;

/// <summary>A note bar in lane coordinates: X from 0 (line start) to 1 (line end), Y from 0 (bottom) to 1 (top).</summary>
public readonly record struct LaneNote(double X, double Width, double Y, UltraStarNote Note);

/// <summary>
/// Places one lyric line's notes on the note lane. Time runs left to right across the line. Pitch uses one
/// fixed scale for the whole song (<see cref="Span"/> semitones from bottom to top), so an interval looks
/// the same size on every line and the eye can read the jumps; each line only moves the view's centre
/// (<see cref="Centre"/>), which the stage glides to. A singer's pitch is folded to the octave of the note
/// being sung (<see cref="FoldToTarget"/>), matching the octave-independent scoring.
/// </summary>
public sealed class NoteLaneLayout
{
    /// <summary>The scale's smallest and largest height in semitones: the widest line fits, plus a margin.</summary>
    public const int MinSpan = 14, MaxSpan = 26;

    /// <summary>How far ahead (in beats) the next note already counts as the one being sung.</summary>
    public const double LookaheadBeats = 2;

    private readonly double _startBeat, _beats;

    /// <param name="notes">The whole voice's notes: the scale comes from its widest line.</param>
    /// <param name="span">The scale in semitones; null works it out from <paramref name="notes"/>.</param>
    public NoteLaneLayout(DisplayLine line, IReadOnlyList<UltraStarNote> notes, double? span = null)
    {
        var sung = notes.Where(n => n.Type != NoteType.LineBreak && n.StartBeat >= line.StartBeat && n.StartBeat < line.EndBeat).ToList();
        Notes = sung;
        _startBeat = line.StartBeat;
        _beats = Math.Max(1, line.EndBeat - line.StartBeat);

        Span = span ?? SpanFor(notes);
        int low = sung.Count > 0 ? sung.Min(n => n.MidiTone) : 60, high = sung.Count > 0 ? sung.Max(n => n.MidiTone) : 60;
        Centre = (low + high) / 2.0;
    }

    public IReadOnlyList<UltraStarNote> Notes { get; }

    /// <summary>Semitones from the bottom of the lane to the top; the same for every line of a song.</summary>
    public double Span { get; }

    /// <summary>The pitch (MIDI) in the middle of the lane for this line.</summary>
    public double Centre { get; }

    /// <summary>The scale for a voice: its widest line plus three semitones, within <see cref="MinSpan"/>..<see cref="MaxSpan"/>.</summary>
    public static double SpanFor(IReadOnlyList<UltraStarNote> notes)
    {
        int widest = 0, low = int.MaxValue, high = int.MinValue;
        foreach (var n in notes.Append(null))
        {
            if (n is null || n.Type == NoteType.LineBreak)
            {
                if (high >= low) widest = Math.Max(widest, high - low);
                low = int.MaxValue;
                high = int.MinValue;
                continue;
            }
            low = Math.Min(low, n.MidiTone);
            high = Math.Max(high, n.MidiTone);
        }
        return Math.Clamp(widest + 3, MinSpan, MaxSpan);
    }

    public IEnumerable<LaneNote> Layout() => Layout(Centre);

    /// <summary>The notes with the view centred on <paramref name="centre"/> (the stage glides it between lines).</summary>
    public IEnumerable<LaneNote> Layout(double centre) => Notes.Select(n =>
        new LaneNote(XFor(n.StartBeat), n.DurationBeats / _beats, YFor(n.MidiTone, centre), n));

    public double XFor(double beat) => (beat - _startBeat) / _beats;

    public double YFor(double midi) => YFor(midi, Centre);

    public double YFor(double midi, double centre) => Math.Clamp(0.5 + (midi - centre) / Span, 0, 1);

    /// <summary>
    /// The note the singer is singing at <paramref name="beat"/>: the one under the cursor, else the next one
    /// within <see cref="LookaheadBeats"/>, else the last one sung; null on a line without notes.
    /// </summary>
    public UltraStarNote? TargetAt(double beat)
    {
        UltraStarNote? last = null;
        foreach (var n in Notes)
        {
            if (n.Type == NoteType.Freestyle) continue;
            if (beat < n.StartBeat) return n.StartBeat - beat <= LookaheadBeats || last is null ? n : last;
            if (beat < n.StartBeat + n.DurationBeats) return n;
            last = n;
        }
        return last;
    }

    /// <summary>
    /// The sung pitch moved by whole octaves to the octave of the note being sung (or of the line's centre),
    /// so a bass singing a soprano part lands on the notes and the dot doesn't flip between lane edges.
    /// </summary>
    public double FoldToTarget(double sungMidi, double beat)
    {
        double reference = TargetAt(beat)?.MidiTone ?? Centre;
        return sungMidi + 12 * Math.Round((reference - sungMidi) / 12);
    }

    /// <summary>Where to draw the singer's pitch marker (folded to the note being sung).</summary>
    public double SingerY(double sungMidi, double beat) => YFor(FoldToTarget(sungMidi, beat));
}
