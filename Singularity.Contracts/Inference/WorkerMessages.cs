using System.Text.Json.Serialization;

namespace Singularity.Contracts.Inference;

// The app ↔ inference worker protocol: newline-delimited JSON over the worker's stdin (commands)
// and stdout (events). stderr is free-form diagnostics and is never parsed. Every object carries
// its kind in "command" / "event"; the worker may add fields and events in later protocol
// versions, so readers ignore unknown fields and surface unknown events instead of failing.

/// <summary>Pipeline stages the worker reports on, written as "separation", "transcription", ….</summary>
public enum PipelineStage
{
    /// <summary>Vocal / instrumental stem separation (Demucs).</summary>
    Separation,
    /// <summary>Speech recognition, used only when no reference lyrics were supplied.</summary>
    Transcription,
    /// <summary>Forced alignment of lyric words and syllables to the vocal stem.</summary>
    Alignment,
    /// <summary>Per-syllable pitch tracking on the vocal stem.</summary>
    Pitch,
    /// <summary>Tempo and beat-grid estimation.</summary>
    Tempo,
}

/// <summary>What <see cref="ProcessTrackCommand.Lyrics"/> holds, written as "plain" / "synced".</summary>
public enum LyricsKind
{
    /// <summary>Plain text, one lyric line per text line. The worker transcribes to find where lines are.</summary>
    Plain,
    /// <summary>LRC ("[mm:ss.xx] line"). Line times are known, so the worker skips transcription.</summary>
    Synced,
}

public enum TaskOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public enum WorkerLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

// ── Commands (app → worker) ─────────────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(ProcessTrackCommand), "process_track")]
[JsonDerivedType(typeof(SeparateStemsCommand), "separate_stems")]
[JsonDerivedType(typeof(CancelCommand), "cancel")]
[JsonDerivedType(typeof(ShutdownCommand), "shutdown")]
public abstract record WorkerCommand;

/// <summary>Turn a master audio file into stems plus timed, pitched syllables.</summary>
/// <param name="TaskId">Chosen by the app; echoed on every event about this task.</param>
/// <param name="AudioPath">Absolute path of the master audio.</param>
/// <param name="OutputFolder">Where the worker writes vocals.wav / instrumental.wav.</param>
/// <param name="Lyrics">Reference lyrics (e.g. from LRCLIB); null when none were found and the worker must transcribe.</param>
/// <param name="LyricsKind">Whether <paramref name="Lyrics"/> is plain text or LRC.</param>
/// <param name="Language">ISO 639-1 code, e.g. "en"; null lets the worker detect it (only possible when it transcribes).</param>
/// <param name="ReuseStems">Skip separation when stems already exist in <paramref name="OutputFolder"/>.</param>
public sealed record ProcessTrackCommand(
    string TaskId,
    string AudioPath,
    string OutputFolder,
    string? Lyrics = null,
    LyricsKind LyricsKind = LyricsKind.Plain,
    string? Language = null,
    bool ReuseStems = true) : WorkerCommand;

/// <summary>
/// Only the separation stage: writes vocals.wav and instrumental.wav to <paramref name="OutputFolder"/>,
/// e.g. so a song can be sung with the original vocals turned down. Finishes with no result payload.
/// </summary>
public sealed record SeparateStemsCommand(string TaskId, string AudioPath, string OutputFolder) : WorkerCommand;

public sealed record CancelCommand(string TaskId) : WorkerCommand;

/// <summary>Finish or abandon the current task, then exit with code 0.</summary>
public sealed record ShutdownCommand : WorkerCommand;

// ── Events (worker → app) ───────────────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(ReadyEvent), "ready")]
[JsonDerivedType(typeof(StageStartedEvent), "stage_started")]
[JsonDerivedType(typeof(ProgressUpdateEvent), "progress_update")]
[JsonDerivedType(typeof(StageCompletedEvent), "stage_completed")]
[JsonDerivedType(typeof(TaskFinishedEvent), "task_finished")]
[JsonDerivedType(typeof(LogEvent), "log")]
public abstract record WorkerEvent;

/// <summary>First line the worker writes once models are loadable and it accepts commands.</summary>
/// <param name="Device">Compute device in use, e.g. "cuda:0", "directml", "cpu".</param>
/// <param name="Models">Model name per pipeline role, e.g. { "separation": "htdemucs_ft" }.</param>
/// <param name="MissingModels">Models not downloaded yet; a task that needs one fails until they are fetched.</param>
public sealed record ReadyEvent(
    int ProtocolVersion,
    string WorkerVersion,
    string Device,
    IReadOnlyDictionary<string, string> Models,
    IReadOnlyList<string>? MissingModels = null) : WorkerEvent;

public sealed record StageStartedEvent(string TaskId, PipelineStage Stage) : WorkerEvent;

/// <param name="Progress">Fraction of the stage done, in [0, 1].</param>
public sealed record ProgressUpdateEvent(string TaskId, PipelineStage Stage, double Progress, string? Message = null) : WorkerEvent;

public sealed record StageCompletedEvent(string TaskId, PipelineStage Stage, long DurationMs) : WorkerEvent;

/// <summary>Last event for a task. For process_track, <see cref="Result"/> is set exactly when the outcome is
/// <see cref="TaskOutcome.Succeeded"/>; separate_stems never has one (the stems are in its output folder).</summary>
public sealed record TaskFinishedEvent(string TaskId, TaskOutcome Outcome, TrackAnalysisResult? Result = null, string? Error = null) : WorkerEvent;

public sealed record LogEvent(WorkerLogLevel Level, string Message, string? TaskId = null) : WorkerEvent;

/// <summary>An event this build doesn't know (from a newer worker). Never serialized; produced by <see cref="WorkerProtocol"/>.</summary>
public sealed record UnknownWorkerEvent(string Name, string RawJson) : WorkerEvent;

// ── Result payload ──────────────────────────────────────────────────────────────────────────────

/// <summary>What the worker produced for one track. Times are milliseconds in the master audio.</summary>
/// <param name="VocalsPath">Absolute path of the separated vocal stem.</param>
/// <param name="InstrumentalPath">Absolute path of the accompaniment stem.</param>
/// <param name="TempoBpm">Estimated musical tempo (not the UltraStar grid BPM, which the app derives).</param>
/// <param name="Language">Language that was aligned or detected.</param>
/// <param name="Lines">Lyric lines in order; each holds its syllables in order.</param>
/// <param name="Models">Model name per pipeline role that actually ran.</param>
public sealed record TrackAnalysisResult(
    string VocalsPath,
    string InstrumentalPath,
    double TempoBpm,
    string Language,
    IReadOnlyList<LyricLine> Lines,
    IReadOnlyDictionary<string, string> Models);

public sealed record LyricLine(IReadOnlyList<TimedSyllable> Syllables);

/// <param name="Text">The syllable as sung, without leading/trailing spaces.</param>
/// <param name="StartsWord">True for a word's first syllable (UltraStar writes a leading space).</param>
/// <param name="MidiTone">Median pitch over the syllable, as a MIDI note number; null when unvoiced or untracked.</param>
/// <param name="PitchConfidence">Mean periodicity confidence of the pitch track over the syllable, in [0, 1].</param>
/// <param name="AlignmentConfidence">Confidence of the word this syllable belongs to, in [0, 1].</param>
/// <param name="Segments">
/// Set when the syllable is sung over more than one note (a melisma): the notes in order, covering
/// StartMs..EndMs. UltraStar writes the first with the syllable's text and the rest as "~".
/// </param>
public sealed record TimedSyllable(
    string Text,
    int StartMs,
    int EndMs,
    bool StartsWord,
    int? MidiTone,
    double PitchConfidence,
    double AlignmentConfidence,
    IReadOnlyList<PitchSegment>? Segments = null);

/// <summary>One note of a melisma.</summary>
public sealed record PitchSegment(int StartMs, int EndMs, int MidiTone);
