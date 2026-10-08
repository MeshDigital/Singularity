using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Scoring;

/// <summary>How far off a sung note may be and still earn something; see <see cref="SingScorer.ToleranceFor"/>.</summary>
public enum Difficulty
{
    Easy = 2,
    Medium = 1,
    Hard = 0,
}

/// <summary>One judged beat of a scored note.</summary>
/// <param name="Credit">How well it was sung, 0..1: the share of the beat's points earned.</param>
/// <param name="Offset">The average distance from the note in semitones (positive = sharp) over the sung readings; 0 when silent.</param>
public readonly record struct BeatJudgement(UltraStarNote Note, int Beat, double Credit, double Offset)
{
    /// <summary>Counts as sung (filled in on the stage).</summary>
    public bool Hit => Credit >= 0.5;
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
/// beat. Golden beats weigh double.
///
/// Unlike UltraStar's hit-or-miss, a beat earns credit by how close it was sung. Each pitch sample is
/// compared with the note's pitch class in any octave (so a bass can sing a soprano part): within
/// <see cref="FullCreditSemitones"/> it earns full credit (natural intonation and vibrato), then credit
/// falls off quadratically to nothing at the difficulty's tolerance. A beat's credit is the average over
/// its sung samples, and gaps (consonants, breaths) are forgiven while at least <see cref="HitShare"/> of
/// the beat is voiced. The first <see cref="OnsetGraceMs"/> of a note forgive a scoop onto the pitch:
/// misses there don't count, and a beat with nothing else is judged like the note's next beat.
/// Rap notes only need voice; freestyle notes aren't scored.
///
/// Feed samples in time order with <see cref="AddSample"/>, where the beat already includes mic
/// latency compensation. Beats are judged once they are complete.
/// </summary>
public sealed class SingScorer
{
    public const double MaxScore = 10_000;
    public const double LineBonusPool = 1_000;
    public const double HitShare = 0.5;

    /// <summary>Within this many semitones (20 cents) a sample earns full credit.</summary>
    public const double FullCreditSemitones = 0.2;

    /// <summary>The start of a note where an off-pitch attack isn't held against the singer.</summary>
    public const double OnsetGraceMs = 80;

    /// <summary>The distance in semitones at which a sample stops earning anything.</summary>
    public static double ToleranceFor(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 1.75,
        Difficulty.Hard => 0.65,
        _ => 1.0,
    };

    /// <summary>
    /// The original singer's pitch is only trusted where it is within this many semitones of the chart's note: enough to
    /// cover a chart that is a semitone or two off, not enough to replace the chart where the reference is noise.
    /// </summary>
    public const double ReferenceWithinSemitones = 2;

    /// <summary>
    /// What the original singer sang, by beat (null where they don't sing). When set, a sample earns the better of its
    /// credit against the chart's note and against the singer's pitch, where that is within
    /// <see cref="ReferenceWithinSemitones"/> of the note. Can be set while singing (it's found in the background).
    /// </summary>
    public Func<double, double?>? Reference { get; set; }

    /// <summary>A sample's credit for singing <paramref name="semitonesOff"/> away from the note.</summary>
    public static double CreditFor(double semitonesOff, Difficulty difficulty)
    {
        double off = Math.Abs(semitonesOff) - FullCreditSemitones;
        if (off <= 0) return 1;
        double range = ToleranceFor(difficulty) - FullCreditSemitones;
        return Math.Max(0, 1 - (off / range) * (off / range));
    }

    private readonly Difficulty _difficulty;
    private readonly List<ScoredNote> _notes = new();
    private readonly List<LineState> _lines = new();
    private readonly List<LineResult> _completed = new();
    private readonly double _pointsPerWeight;
    private readonly double _bonusPerLine;

    private int _noteCursor;
    private int _lineCursor;
    private int _currentBeat = int.MinValue;
    private int _beatSamples, _beatVoiced, _beatSkipped;
    private double _beatCredit, _beatOffset;
    private readonly double _graceBeats;
    private readonly List<int> _deferred = new(); // grace-only beats of the current note, judged with its next beat
    private double _notesScore, _goldenScore, _bonusScore;

    /// <param name="beatMs">How long a beat lasts, for the onset grace; 0 turns the grace off.</param>
    public SingScorer(UltraStarVoice voice, Difficulty difficulty = Difficulty.Medium, bool lineBonus = true, double beatMs = 0)
    {
        _difficulty = difficulty;
        _graceBeats = beatMs > 0 ? OnsetGraceMs / beatMs : 0;

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

    /// <summary>Raised for every judged beat of a scored note; the UI paints hit beats over the note bar.</summary>
    public event Action<BeatJudgement>? BeatJudged;

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
        var (credit, offset) = Judge(note.Note, midi, beat);
        // The attack: a miss here is a scoop onto the note, not counted (capped at half the note).
        if (credit <= 0 && beat - note.Note.StartBeat < Math.Min(_graceBeats, note.Note.DurationBeats / 2.0))
        {
            _beatSkipped++;
            return;
        }
        _beatSamples++;
        if (midi is not null && !double.IsNaN(midi.Value))
        {
            _beatVoiced++;
            _beatCredit += credit;
            _beatOffset += offset;
        }
    }

    /// <summary>Judges the last beat and any remaining lines (call when the song ends).</summary>
    public void Finish()
    {
        CloseBeat();
        FlushDeferred(0, 0);
        CompleteLinesBefore(int.MaxValue);
    }

    /// <summary>A sample's credit and its distance from the note's pitch class (positive = sharp).</summary>
    private (double Credit, double Offset) Judge(UltraStarNote note, double? midi, double beat)
    {
        if (midi is not { } sung || double.IsNaN(sung)) return (0, 0);
        if (note.IsRap) return (1, 0);
        double diff = PitchClassDistance(sung, note.MidiTone);
        double credit = CreditFor(diff, _difficulty);
        // Where the chart's note is a little off, singing what the artist sang counts too.
        if (credit < 1 && Reference?.Invoke(beat) is { } artist && Math.Abs(PitchClassDistance(artist, note.MidiTone)) <= ReferenceWithinSemitones)
        {
            double toArtist = PitchClassDistance(sung, artist);
            double artistCredit = CreditFor(toArtist, _difficulty);
            if (artistCredit > credit) return (artistCredit, toArtist);
        }
        return (credit, diff);
    }

    /// <summary>The distance from <paramref name="target"/> to <paramref name="sung"/> in semitones, octaves ignored (-6..6].</summary>
    private static double PitchClassDistance(double sung, double target)
    {
        double diff = (sung - target) % 12;
        if (diff > 6) diff -= 12;
        if (diff < -6) diff += 12;
        return diff;
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
        var judged = _beatSamples + _beatSkipped > 0 ? NoteAt(_currentBeat) : null;
        if (judged is null)
        {
            ResetBeat();
            return;
        }
        // Grace beats from an earlier note never got a follow-up beat: they were missed.
        if (_deferred.Count > 0 && !ReferenceEquals(_deferredNote, judged)) FlushDeferred(0, 0);
        if (_beatSamples == 0)
        {
            _deferredNote = judged;
            _deferred.Add(_currentBeat); // only the attack so far: judged like the note's next beat
            ResetBeat();
            return;
        }

        double coverage = Math.Min(1, _beatVoiced / (_beatSamples * HitShare));
        double credit = _beatVoiced == 0 ? 0 : _beatCredit / _beatVoiced * coverage;
        double offset = _beatVoiced == 0 ? 0 : _beatOffset / _beatVoiced;
        Award(judged, _currentBeat, credit, offset);
        FlushDeferred(credit, offset);
        ResetBeat();
    }

    private ScoredNote? _deferredNote;

    private void FlushDeferred(double credit, double offset)
    {
        if (_deferredNote is { } note)
            foreach (var beat in _deferred) Award(note, beat, credit, offset);
        _deferred.Clear();
        _deferredNote = null;
    }

    private void Award(ScoredNote note, int beat, double credit, double offset)
    {
        BeatJudged?.Invoke(new BeatJudgement(note.Note, beat, credit, offset));
        if (credit <= 0) return;
        double points = note.Weight * _pointsPerWeight * credit;
        if (note.Note.IsGolden) _goldenScore += points;
        else _notesScore += points;
        _lines[note.Line].HitWeight += note.Weight * credit;
    }

    private void ResetBeat()
    {
        _beatSamples = _beatVoiced = _beatSkipped = 0;
        _beatCredit = _beatOffset = 0;
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
