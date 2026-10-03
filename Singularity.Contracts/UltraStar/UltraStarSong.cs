namespace Singularity.Contracts.UltraStar;

/// <summary>UltraStar note kinds and their line markers in song.txt.</summary>
public enum NoteType
{
    /// <summary>":" — a normal sung note.</summary>
    Regular,
    /// <summary>"*" — scores double.</summary>
    Golden,
    /// <summary>"F" — not scored.</summary>
    Freestyle,
    /// <summary>"R" — rapped: timing scored, pitch ignored.</summary>
    Rap,
    /// <summary>"G" — golden rap.</summary>
    RapGolden,
    /// <summary>"-" — end of a lyric line; only <see cref="UltraStarNote.StartBeat"/> is meaningful.</summary>
    LineBreak,
}

/// <summary>
/// One song.txt event. Pitch is kept as a MIDI note number (60 = C4); UltraStar files store it
/// relative to C4, which <see cref="UltraStarSerializer"/> converts. <see cref="Syllable"/> is
/// written verbatim: a leading space marks the start of a new word, "~" continues the previous syllable.
/// Beats are always absolute here; files in the legacy #RELATIVE mode are converted on read.
/// </summary>
public sealed record UltraStarNote(NoteType Type, int StartBeat, int DurationBeats, int MidiTone, string Syllable)
{
    /// <summary>UltraStar's pitch 0 is C4 (MIDI 60).</summary>
    public const int UltraStarPitchOffset = 60;

    public static UltraStarNote LineBreak(int beat) => new(NoteType.LineBreak, beat, 0, 0, string.Empty);

    public bool IsScored => Type is NoteType.Regular or NoteType.Golden or NoteType.Rap or NoteType.RapGolden;
    public bool IsGolden => Type is NoteType.Golden or NoteType.RapGolden;
    public bool IsRap => Type is NoteType.Rap or NoteType.RapGolden;
}

/// <summary>One singer's part. A normal song has one voice; a duet has two (P1 and P2).</summary>
public sealed record UltraStarVoice(IReadOnlyList<UltraStarNote> Notes, string? SingerName = null);

/// <summary>A song.txt: header fields plus one note stream per voice.</summary>
public sealed record UltraStarSong
{
    public const string FormatVersion = "1.1.0";

    public required string Title { get; init; }
    public required string Artist { get; init; }

    /// <summary>Beats per minute of the note grid; one beat lasts 60000 / (Bpm * 4) ms.</summary>
    public required double Bpm { get; init; }

    /// <summary>Milliseconds from the start of the audio to beat 0.</summary>
    public int GapMs { get; init; }

    /// <summary>Audio file relative to the song folder (#AUDIO, also written as #MP3 for older players).</summary>
    public required string AudioFile { get; init; }

    public string? VocalsFile { get; init; }
    public string? InstrumentalFile { get; init; }
    public string? VideoFile { get; init; }

    /// <summary>Written as #VIDEOGAP in seconds. Video position = audio position + gap.</summary>
    public int VideoGapMs { get; init; }

    public string? CoverFile { get; init; }
    public string? BackgroundFile { get; init; }
    public string? Language { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public string? Edition { get; init; }
    public string? Creator { get; init; }
    public string? Tags { get; init; }

    /// <summary>Playback starts here (#START, seconds in the file).</summary>
    public int? StartMs { get; init; }

    /// <summary>Playback ends here (#END, milliseconds in the file).</summary>
    public int? EndMs { get; init; }

    /// <summary>Where the song-select preview starts (#PREVIEWSTART, seconds in the file).</summary>
    public int? PreviewStartMs { get; init; }

    /// <summary>Medley section (#MEDLEYSTARTBEAT / #MEDLEYENDBEAT).</summary>
    public int? MedleyStartBeat { get; init; }
    public int? MedleyEndBeat { get; init; }

    /// <summary>Headers this model doesn't know, kept so a read/write round trip doesn't lose them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>One voice for a solo song, two for a duet.</summary>
    public IReadOnlyList<UltraStarVoice> Voices { get; init; } = Array.Empty<UltraStarVoice>();

    public bool IsDuet => Voices.Count > 1;

    public double MillisecondsPerBeat => 60000.0 / (Bpm * 4.0);

    /// <summary>Audio position of a beat, in milliseconds.</summary>
    public double BeatToMs(double beat) => GapMs + beat * MillisecondsPerBeat;

    /// <summary>Beat at an audio position (fractional; round as the caller needs).</summary>
    public double MsToBeat(double ms) => (ms - GapMs) / MillisecondsPerBeat;
}
