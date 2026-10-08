using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;

namespace Singularity.Karaoke.Editing;

/// <summary>A lyric line of a voice: its notes are <c>Notes[From..To)</c> (line breaks excluded).</summary>
public readonly record struct ChartLine(int Number, int From, int To)
{
    public int Count => To - From;
}

/// <summary>
/// The corrections the chart editor makes, as plain operations on a chart (each returns a new chart): move the whole
/// chart, move a line, transpose a line, set a note's pitch, and move a line's notes to where the original singer sings
/// them. A line never moves past its neighbours, and line breaks are kept between the lines they separate.
/// </summary>
public static class ChartEdit
{
    /// <summary>The voice's lines, numbered from 1, in order.</summary>
    public static IReadOnlyList<ChartLine> Lines(UltraStarVoice voice)
    {
        var lines = new List<ChartLine>();
        int from = 0;
        for (int i = 0; i <= voice.Notes.Count; i++)
        {
            if (i < voice.Notes.Count && voice.Notes[i].Type != NoteType.LineBreak) continue;
            if (i > from) lines.Add(new ChartLine(lines.Count + 1, from, i));
            from = i + 1;
        }
        return lines;
    }

    /// <summary>The whole chart <paramref name="ms"/> later (negative: earlier).</summary>
    public static UltraStarSong ShiftAll(UltraStarSong song, int ms) => ChartSync.Shift(song, ms);

    /// <summary>
    /// One line moved by <paramref name="beats"/>, as far as it can go without touching the line before or after it.
    /// Returns the chart and how far it actually moved.
    /// </summary>
    public static (UltraStarSong Song, int Moved) ShiftLine(UltraStarSong song, int voice, int lineNumber, int beats)
    {
        var notes = song.Voices[voice].Notes;
        var lines = Lines(song.Voices[voice]);
        var line = lines[lineNumber - 1];
        int start = notes[line.From].StartBeat, end = LineEnd(notes, line);
        int earliest = lineNumber > 1 ? LineEnd(notes, lines[lineNumber - 2]) : int.MinValue;
        int latest = lineNumber < lines.Count ? notes[lines[lineNumber].From].StartBeat : int.MaxValue;
        int lowest = earliest == int.MinValue ? -start : earliest - start; // not before the line before (or beat 0)
        int highest = latest == int.MaxValue ? int.MaxValue / 2 : latest - end; // not into the line after
        int moved = Math.Clamp(beats, Math.Min(lowest, 0), Math.Max(highest, 0));
        if (moved == 0) return (song, 0);

        var edited = notes.ToArray();
        for (int i = line.From; i < line.To; i++) edited[i] = edited[i] with { StartBeat = edited[i].StartBeat + moved };
        return (WithVoice(song, voice, KeepBreaksBetweenLines(edited)), moved);
    }

    /// <summary>Every note of a line moved by <paramref name="semitones"/>.</summary>
    public static UltraStarSong TransposeLine(UltraStarSong song, int voice, int lineNumber, int semitones)
    {
        var line = Lines(song.Voices[voice])[lineNumber - 1];
        var edited = song.Voices[voice].Notes.ToArray();
        for (int i = line.From; i < line.To; i++) edited[i] = edited[i] with { MidiTone = edited[i].MidiTone + semitones };
        return WithVoice(song, voice, edited);
    }

    /// <summary>One note's pitch.</summary>
    public static UltraStarSong SetTone(UltraStarSong song, int voice, int noteIndex, int tone)
    {
        var edited = song.Voices[voice].Notes.ToArray();
        if (edited[noteIndex].Type == NoteType.LineBreak) return song;
        edited[noteIndex] = edited[noteIndex] with { MidiTone = tone };
        return WithVoice(song, voice, edited);
    }

    /// <summary>
    /// A line's notes moved to where the original singer sings them (only notes they sing clearly and steadily
    /// elsewhere). Returns the chart and how many notes changed.
    /// </summary>
    public static (UltraStarSong Song, int Changed) UseSingerPitch(UltraStarSong song, int voice, int lineNumber, ReferencePitch singer)
    {
        var line = Lines(song.Voices[voice])[lineNumber - 1];
        var edited = song.Voices[voice].Notes.ToArray();
        int changed = 0;
        for (int i = line.From; i < line.To; i++)
        {
            int move = ChartNoteCheck.CorrectionFor(song, edited[i], singer);
            if (move == 0) continue;
            edited[i] = edited[i] with { MidiTone = edited[i].MidiTone + move };
            changed++;
        }
        return changed == 0 ? (song, 0) : (WithVoice(song, voice, edited), changed);
    }

    private static int LineEnd(IReadOnlyList<UltraStarNote> notes, ChartLine line)
    {
        int end = int.MinValue;
        for (int i = line.From; i < line.To; i++) end = Math.Max(end, notes[i].StartBeat + notes[i].DurationBeats);
        return end;
    }

    /// <summary>Each line break between the end of the line before it and the start of the line after it.</summary>
    private static UltraStarNote[] KeepBreaksBetweenLines(UltraStarNote[] notes)
    {
        for (int i = 0; i < notes.Length; i++)
        {
            if (notes[i].Type != NoteType.LineBreak) continue;
            int before = int.MinValue, after = int.MaxValue;
            for (int k = i - 1; k >= 0 && notes[k].Type != NoteType.LineBreak; k--) before = Math.Max(before, notes[k].StartBeat + notes[k].DurationBeats);
            if (i + 1 < notes.Length && notes[i + 1].Type != NoteType.LineBreak) after = notes[i + 1].StartBeat;
            int beat = notes[i].StartBeat;
            if (before != int.MinValue && beat < before) beat = before;
            if (after != int.MaxValue && beat > after) beat = after;
            notes[i] = notes[i] with { StartBeat = beat };
        }
        return notes;
    }

    private static UltraStarSong WithVoice(UltraStarSong song, int voice, UltraStarNote[] notes) => song with
    {
        Voices = song.Voices.Select((v, i) => i == voice ? new UltraStarVoice(notes) : v).ToArray(),
    };
}
