"""Pydantic mirror of Singularity.Contracts.

Field names are snake_case here and camelCase on the wire, which matches the C# ContractJson settings.
Enum values are snake_case strings, and None fields are left out when encoding. The shared fixtures in
Singularity.Contracts/Fixtures are parsed by both test suites, so the two sides can't drift silently.
"""

from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Annotated, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, TypeAdapter
from pydantic.alias_generators import to_camel

PROTOCOL_VERSION = 1
METADATA_SCHEMA_VERSION = 1


class Contract(BaseModel):
    """Base for every message: camelCase aliases, unknown fields ignored (newer peers may add some)."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore", frozen=True)

    def to_json(self) -> str:
        """One compact JSON object, the form used for a JSONL line."""
        return self.model_dump_json(by_alias=True, exclude_none=True)


# ── Song package metadata (metadata.json) ────────────────────────────────────────────────────────


class QualityTier(str, Enum):
    A_PLUS = "a_plus"
    A = "a"
    B = "b"
    REVIEW_REQUIRED = "review_required"


class AudioFingerprint(Contract):
    chromaprint: str
    acoust_id: str | None = Field(default=None, alias="acoustId")
    music_brainz_recording_id: str | None = Field(default=None, alias="musicBrainzRecordingId")


class TimingDescriptor(Contract):
    bpm: float
    gap_ms: int
    video_gap_ms: int
    video_structure_valid: bool


class QualityMetrics(Contract):
    audio_match: float = Field(ge=0, le=1)
    lyric_alignment: float = Field(ge=0, le=1)
    pitch_confidence: float = Field(ge=0, le=1)
    video_match: float = Field(ge=0, le=1)
    metadata_confidence: float = Field(ge=0, le=1)


class QualityAssessment(Contract):
    overall_score: float = Field(ge=0, le=1)
    tier: QualityTier
    metrics: QualityMetrics


class PipelineProvenance(Contract):
    generator: str
    inference_engine: str | None = None
    models: dict[str, str]
    processed_at_utc: datetime
    processing_duration_ms: int


class SongPackageMetadata(Contract):
    schema_version: int = Field(default=METADATA_SCHEMA_VERSION, le=METADATA_SCHEMA_VERSION)
    track_id: str
    isrc: str | None = None
    fingerprint: AudioFingerprint | None = None
    title: str
    artist: str
    album: str | None = None
    release_year: int | None = None
    language: str | None = None
    duration_ms: int
    timing: TimingDescriptor
    quality: QualityAssessment
    provenance: PipelineProvenance


# ── Worker protocol ──────────────────────────────────────────────────────────────────────────────


class PipelineStage(str, Enum):
    SEPARATION = "separation"
    TRANSCRIPTION = "transcription"
    ALIGNMENT = "alignment"
    PITCH = "pitch"
    TEMPO = "tempo"


class TaskOutcome(str, Enum):
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"


class WorkerLogLevel(str, Enum):
    DEBUG = "debug"
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"


class LyricsKind(str, Enum):
    PLAIN = "plain"
    SYNCED = "synced"


class ProcessTrackCommand(Contract):
    command: Literal["process_track"] = "process_track"
    task_id: str
    audio_path: str
    output_folder: str
    lyrics: str | None = None
    lyrics_kind: LyricsKind = LyricsKind.PLAIN
    language: str | None = None
    reuse_stems: bool = True


class CancelCommand(Contract):
    command: Literal["cancel"] = "cancel"
    task_id: str


class ShutdownCommand(Contract):
    command: Literal["shutdown"] = "shutdown"


WorkerCommand = Annotated[Union[ProcessTrackCommand, CancelCommand, ShutdownCommand], Field(discriminator="command")]


class TimedSyllable(Contract):
    text: str
    start_ms: int
    end_ms: int
    starts_word: bool
    midi_tone: int | None
    pitch_confidence: float = Field(ge=0, le=1)
    alignment_confidence: float = Field(ge=0, le=1)


class LyricLine(Contract):
    syllables: list[TimedSyllable]


class TrackAnalysisResult(Contract):
    vocals_path: str
    instrumental_path: str
    tempo_bpm: float
    language: str
    lines: list[LyricLine]
    models: dict[str, str]


class ReadyEvent(Contract):
    event: Literal["ready"] = "ready"
    protocol_version: int = PROTOCOL_VERSION
    worker_version: str
    device: str
    models: dict[str, str]
    missing_models: list[str] | None = None


class StageStartedEvent(Contract):
    event: Literal["stage_started"] = "stage_started"
    task_id: str
    stage: PipelineStage


class ProgressUpdateEvent(Contract):
    event: Literal["progress_update"] = "progress_update"
    task_id: str
    stage: PipelineStage
    progress: float = Field(ge=0, le=1)
    message: str | None = None


class StageCompletedEvent(Contract):
    event: Literal["stage_completed"] = "stage_completed"
    task_id: str
    stage: PipelineStage
    duration_ms: int


class TaskFinishedEvent(Contract):
    event: Literal["task_finished"] = "task_finished"
    task_id: str
    outcome: TaskOutcome
    result: TrackAnalysisResult | None = None
    error: str | None = None


class LogEvent(Contract):
    event: Literal["log"] = "log"
    level: WorkerLogLevel
    message: str
    task_id: str | None = None


WorkerEvent = Annotated[
    Union[ReadyEvent, StageStartedEvent, ProgressUpdateEvent, StageCompletedEvent, TaskFinishedEvent, LogEvent],
    Field(discriminator="event"),
]

_commands: TypeAdapter[WorkerCommand] = TypeAdapter(WorkerCommand)
_events: TypeAdapter[WorkerEvent] = TypeAdapter(WorkerEvent)


def decode_command(line: str) -> ProcessTrackCommand | CancelCommand | ShutdownCommand:
    """Parse one stdin line. Raises pydantic.ValidationError for unknown or malformed commands."""
    return _commands.validate_json(line)


def decode_event(line: str):
    """Parse one stdout line (used by tests and tools; the app does this in C#)."""
    return _events.validate_json(line)
