using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Scoring;

/// <summary>How far off a sung note may be and still count, in semitones (octave ignored).</summary>
public enum Difficulty
{
    Easy = 2,
    Medium = 1,
    Hard = 0,
}

/// <summary>Per-line verdict, shown when a lyric line has been sung.</summary>
public enum LineRating
{
    Awful,
    Poor,
    Bad,
    NotBad,
    Good,
    Great,
    Awesome,
    Perfect,
}

/// <param name="Perfection">Share of the line's scored weight that was hit, 0..1.</param>
public sealed record LineResult(int LineIndex, double Perfection, LineRating Rating, double Bonus);

/// <summary>Points so far. <see cref="Total"/> is what the game shows: rounded down to tens, as UltraStar does.</summary>
public sealed record ScoreBreakdown(double Notes, double Golden, double LineBonus)
{
    public int Total => (int)(Math.Floor((Notes + Golden + LineBonus) / 10) * 10);
}

/// <summary>
/// Scores one singer on one voice, UltraStar style. A song is worth <see cref="MaxScore"/> points:
/// <see cref="LineBonusPool"/> for line bonuses (when enabled), the rest shared across every scored
/// beat. Golden beats weigh double. A beat is hit when at least half the pitch samples taken during
/// it match. A sample matches when it is the note's pitch class within the difficulty's tolerance,
/// in any octave, so a bass can sing a soprano part. Rap notes only need voice; freestyle notes
/// aren't scored.
///
/// Feed samples in time order with <see cref="AddSample"/>, where the beat already includes mic
/// latency compensation. Beats are judged once they are complete.
/// </summary>
public sealed class SingScorer
{
    public const double MaxScore = 10_000;
    public const double LineBonusPool = 1_000;
    public const double HitShare = 0.5;

    private readonly Difficulty _difficulty;
    private readonly List<ScoredNote> _notes = new();
    private readonly List<LineState> _lines = new();
    private readonly List<LineResult> _completed = new();
    private readonly double _pointsPerWeight;
    private readonly double _bonusPerLine;

    private int _noteCursor;
    private int _lineCursor;
    private int _currentBeat = int.MinValue;
    private int _beatSamples, _beatHits;
    private double _notesScore, _goldenScore, _bonusScore;

    public SingScorer(UltraStarVoice voice, Difficulty difficulty = Difficulty.Medium, bool lineBonus = true)
    {
        _difficulty = difficulty;

        int line = 0;
        foreach (var n in voice.Notes)
        {
            if (n.Type == NoteType.LineBreak)
            {
                if (_notes.Count > 0 && _notes[^1].Line == line) line++;
                continue;
            }
            if (!n.IsScored || n.DurationBeats <= 0) continue;
            _notes.Add(new ScoredNote(n, line));
        }

        double totalWeight = _notes.Sum(n => n.Weight * n.Note.DurationBeats);
        int scoredLines = _notes.Count == 0 ? 0 : _notes[^1].Line + 1;
        for (int i = 0; i < scoredLines; i++)
        {
            var inLine = _notes.Where(n => n.Line == i).ToList();
            _lines.Add(new LineState(i, inLine.Sum(n => n.Weight * n.Note.DurationBeats),
                inLine.Count == 0 ? int.MinValue : inLine.Max(n => n.Note.StartBeat + n.Note.DurationBeats)));
        }

        double notePool = lineBonus ? MaxScore - LineBonusPool : MaxScore;
        _pointsPerWeight = totalWeight > 0 ? notePool / totalWeight : 0;
        int linesWithNotes = _lines.Count(l => l.MaxWeight > 0);
        _bonusPerLine = lineBonus && linesWithNotes > 0 ? LineBonusPool / linesWithNotes : 0;
    }

    public ScoreBreakdown Score => new(_notesScore, _goldenScore, _bonusScore);

    /// <summary>Lines finished so far, in order.</summary>
    public IReadOnlyList<LineResult> CompletedLines => _completed;

    /// <summary>Raised when a line has been sung; the UI shows its rating.</summary>
    public event Action<LineResult>? LineCompleted;

    /// <param name="beat">Song position in (fractional) beats, latency-compensated.</param>
    /// <param name="midi">Sung pitch as a fractional MIDI note, or null for silence.</param>
    public void AddSample(double beat, double? midi)
    {
        int b = (int)Math.Floor(beat);
        if (b < _currentBeat) return; // late sample for a beat already judged
        if (b != _currentBeat)
        {
            CloseBeat();
            _currentBeat = b;
            CompleteLinesBefore(b);
        }

        var note = NoteAt(b);
        if (note is null) return;
        _beatSamples++;
        if (Matches(note.Note, midi)) _beatHits++;
    }

    /// <summary>Judges the last beat and any remaining lines (call when the song ends).</summary>
    public void Finish()
    {
        CloseBeat();
        CompleteLinesBefore(int.MaxValue);
    }

    private bool Matches(UltraStarNote note, double? midi)
    {
        if (midi is not { } sung || double.IsNaN(sung)) return false;
        if (note.IsRap) return true;
        double diff = (sung - note.MidiTone) % 12;
        if (diff > 6) diff -= 12;
        if (diff < -6) diff += 12;
        return Math.Abs(diff) <= (int)_difficulty + 0.5; // +0.5: within the semitone's own band
    }

    private ScoredNote? NoteAt(int beat)
    {
        while (_noteCursor < _notes.Count && _notes[_noteCursor].Note.StartBeat + _notes[_noteCursor].Note.DurationBeats <= beat)
            _noteCursor++;
        if (_noteCursor >= _notes.Count) return null;
        var n = _notes[_noteCursor];
        return beat >= n.Note.StartBeat ? n : null;
    }

    private void CloseBeat()
    {
        if (_beatSamples > 0 && _beatHits >= _beatSamples * HitShare)
        {
            var note = NoteAt(_currentBeat)!;
            double points = note.Weight * _pointsPerWeight;
            if (note.Note.IsGolden) _goldenScore += points;
            else _notesScore += points;
            _lines[note.Line].HitWeight += note.Weight;
        }
        _beatSamples = 0;
        _beatHits = 0;
    }

    private void CompleteLinesBefore(int beat)
    {
        while (_lineCursor < _lines.Count && _lines[_lineCursor].EndBeat <= beat)
        {
            var line = _lines[_lineCursor++];
            if (line.MaxWeight <= 0) continue;
            double perfection = Math.Clamp(line.HitWeight / line.MaxWeight, 0, 1);
            double bonus = _bonusPerLine * perfection;
            _bonusScore += bonus;
            var result = new LineResult(line.Index, perfection, RatingFor(perfection), bonus);
            _completed.Add(result);
            LineCompleted?.Invoke(result);
        }
    }

    public static LineRating RatingFor(double perfection) => perfection switch
    {
        >= 0.95 => LineRating.Perfect,
        >= 0.80 => LineRating.Awesome,
        >= 0.65 => LineRating.Great,
        >= 0.50 => LineRating.Good,
        >= 0.35 => LineRating.NotBad,
        >= 0.20 => LineRating.Bad,
        >= 0.10 => LineRating.Poor,
        _ => LineRating.Awful,
    };

    private sealed record ScoredNote(UltraStarNote Note, int Line)
    {
        public int Weight => Note.IsGolden ? 2 : 1;
    }

    private sealed class LineState(int index, double maxWeight, int endBeat)
    {
        public int Index { get; } = index;
        public double MaxWeight { get; } = maxWeight;
        public int EndBeat { get; } = endBeat;
        public double HitWeight { get; set; }
    }
}
