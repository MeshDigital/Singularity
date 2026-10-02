using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Display;

/// <summary>One syllable of a displayed line, with how much of it has been sung (0..1, for the colour wipe).</summary>
public readonly record struct DisplaySyllable(string Text, NoteType Type, int StartBeat, int EndBeat, double Progress);

/// <summary>A lyric line as shown on screen.</summary>
public sealed record DisplayLine(int Index, int StartBeat, int EndBeat, IReadOnlyList<DisplaySyllable> Syllables)
{
    public string Text => string.Concat(Syllables.Select(s => s.Text)).Trim();
}

/// <summary>What the lyrics area shows at one moment.</summary>
/// <param name="Current">The line being sung or about to be sung; null after the last line.</param>
/// <param name="Next">The line after it, shown smaller underneath.</param>
/// <param name="BeatsUntilStart">Positive while <paramref name="Current"/> hasn't started: drives the countdown/lead-in marker.</param>
public sealed record LyricsFrame(DisplayLine? Current, DisplayLine? Next, double BeatsUntilStart);

/// <summary>
/// Splits a voice into display lines at its line breaks and answers "what do I show at beat b?".
/// A line is current from the previous line's end until its own end; between lines the upcoming
/// one is shown with a countdown. "~" syllables continue the previous syllable and are shown as
/// part of it, as UltraStar does.
/// </summary>
public sealed class LyricsTimeline
{
    private readonly List<(int Index, int Start, int End, List<UltraStarNote> Notes)> _lines = new();

    public LyricsTimeline(UltraStarVoice voice)
    {
        var current = new List<UltraStarNote>();
        void Flush()
        {
            if (current.Count == 0) return;
            _lines.Add((_lines.Count, current[0].StartBeat, current.Max(n => n.StartBeat + n.DurationBeats), current));
            current = new List<UltraStarNote>();
        }
        foreach (var n in voice.Notes)
        {
            if (n.Type == NoteType.LineBreak) Flush();
            else current.Add(n);
        }
        Flush();
    }

    public int LineCount => _lines.Count;

    public LyricsFrame At(double beat)
    {
        int i = _lines.FindIndex(l => beat < l.End);
        if (i < 0) return new LyricsFrame(null, null, 0);
        var current = Build(i, beat);
        var next = i + 1 < _lines.Count ? Build(i + 1, double.NegativeInfinity) : null;
        return new LyricsFrame(current, next, Math.Max(0, _lines[i].Start - beat));
    }

    private DisplayLine Build(int index, double beat)
    {
        var (lineIndex, start, end, notes) = _lines[index];
        var syllables = new List<DisplaySyllable>();
        foreach (var n in notes)
        {
            int noteEnd = n.StartBeat + n.DurationBeats;
            double progress = n.DurationBeats <= 0 ? (beat >= n.StartBeat ? 1 : 0) : Math.Clamp((beat - n.StartBeat) / n.DurationBeats, 0, 1);
            bool continuation = n.Syllable.Trim() == "~" && syllables.Count > 0;
            if (continuation)
            {
                // A held syllable: extend the previous one; its wipe spans both notes.
                var prev = syllables[^1];
                double total = noteEnd - prev.StartBeat;
                double sung = Math.Clamp(beat - prev.StartBeat, 0, total);
                syllables[^1] = prev with { EndBeat = noteEnd, Progress = total > 0 ? sung / total : 1 };
                continue;
            }
            syllables.Add(new DisplaySyllable(n.Syllable.Replace("~", ""), n.Type, n.StartBeat, noteEnd, progress));
        }
        return new DisplayLine(lineIndex, start, end, syllables);
    }
}
