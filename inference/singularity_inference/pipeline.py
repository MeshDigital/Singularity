"""Runs one process_track task: picks the stages from what lyrics came with it, runs them one at a
time, and frees GPU memory between them so the biggest single model (not the sum) has to fit in VRAM.

Lyrics strategy (cheapest that works):
  synced (LRC)  → line times known: separation → alignment → pitch → tempo. No Whisper.
  plain text    → Whisper finds where the lines are (prompted with the lyrics), alignment refines.
  none          → Whisper transcription is the lyrics; alignment refines its word times.
"""

from __future__ import annotations

import gc
import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Protocol

from . import schemas as s
from .assemble import PitchTrack, build_lines
from .lyrics import (
    AlignedWord,
    lrc_offset,
    shift_lines,
    LyricLineText,
    Window,
    line_windows_from_transcript,
    parse_lrc,
    parse_plain,
    windows_from_lrc,
    words_of,
)


# Below this, LRC line times don't describe this recording (another edit or arrangement).
MIN_LRC_FIT = 0.5


class Cancelled(Exception):
    """The task was cancelled (cancel command, shutdown, or the app went away)."""


@dataclass
class Transcript:
    language: str
    segments: list[tuple[int, int, list[AlignedWord]]]  # (start_ms, end_ms, words)

    @property
    def words(self) -> list[AlignedWord]:
        return [w for _, _, words in self.segments for w in words]


class Backend(Protocol):
    """The model-backed operations. `ml` is the real one, `fake` a deterministic stand-in for tests."""

    device: str

    def duration_ms(self, audio_path: Path) -> int: ...
    def separate(self, audio_path: Path, vocals: Path, instrumental: Path, ctx: "StageContext") -> None: ...
    def transcribe(self, vocals: Path, language: str | None, prompt: str | None, ctx: "StageContext") -> Transcript: ...
    def align(self, vocals: Path, windows: list[Window], language: str | None, ctx: "StageContext") -> list[list[AlignedWord]]: ...
    def vocal_activity(self, vocals: Path) -> list[bool]:
        """Whether the vocal stem is sung in each lyrics.ACTIVITY_FRAME_MS frame."""
        ...
    def track_pitch(self, vocals: Path, ctx: "StageContext") -> PitchTrack: ...
    def estimate_tempo(self, instrumental: Path, ctx: "StageContext") -> float: ...
    def model_names(self) -> dict[str, str]: ...
    def missing_models(self, stages: list[s.PipelineStage]) -> list[str]: ...


@dataclass
class StageContext:
    task_id: str
    stage: s.PipelineStage
    emit: Callable[[s.Contract], None]
    is_cancelled: Callable[[], bool]

    def check(self) -> None:
        if self.is_cancelled():
            raise Cancelled()

    def progress(self, fraction: float, message: str | None = None) -> None:
        self.check()
        self.emit(s.ProgressUpdateEvent(task_id=self.task_id, stage=self.stage,
                                        progress=min(1.0, max(0.0, fraction)), message=message))


def release_gpu_memory() -> None:
    """Drops the previous stage's model before the next loads (8 GB cards can't hold two)."""
    gc.collect()
    torch = sys.modules.get("torch")  # no stage imported torch: nothing on the GPU, and importing it costs ~1.5 s
    if torch is not None and torch.cuda.is_available():
        torch.cuda.empty_cache()
        torch.cuda.ipc_collect()


def plan_stages(cmd: s.ProcessTrackCommand) -> list[s.PipelineStage]:
    stages = [s.PipelineStage.SEPARATION]
    if not cmd.lyrics or cmd.lyrics_kind is s.LyricsKind.PLAIN:
        stages.append(s.PipelineStage.TRANSCRIPTION)
    stages += [s.PipelineStage.ALIGNMENT, s.PipelineStage.PITCH, s.PipelineStage.TEMPO]
    return stages


def whisper_prompt(lines: list[LyricLineText], max_chars: int = 800) -> str:
    """Known lyrics as Whisper's initial prompt (it keeps ~224 tokens), to steer it away from hallucinating."""
    return " ".join(l.text for l in lines)[:max_chars]


def run_task(cmd: s.ProcessTrackCommand, backend: Backend, emit: Callable[[s.Contract], None],
             is_cancelled: Callable[[], bool]) -> s.TrackAnalysisResult:
    audio = Path(cmd.audio_path)
    if not audio.is_file():
        raise FileNotFoundError(f"audio file not found: {audio}")
    out_dir = Path(cmd.output_folder)
    out_dir.mkdir(parents=True, exist_ok=True)
    vocals, instrumental = out_dir / "vocals.wav", out_dir / "instrumental.wav"

    stages = plan_stages(cmd)
    missing = backend.missing_models(stages)
    if missing:
        raise RuntimeError(f"models not downloaded: {', '.join(missing)} (run: python -m singularity_inference.models fetch)")

    def run_stage(stage: s.PipelineStage, fn: Callable[[StageContext], object]):
        ctx = StageContext(cmd.task_id, stage, emit, is_cancelled)
        ctx.check()
        emit(s.StageStartedEvent(task_id=cmd.task_id, stage=stage))
        started = time.monotonic()
        try:
            result = fn(ctx)
        finally:
            release_gpu_memory()
        ctx.check()
        emit(s.StageCompletedEvent(task_id=cmd.task_id, stage=stage, duration_ms=int((time.monotonic() - started) * 1000)))
        return result

    duration = backend.duration_ms(audio)

    def separate(ctx: StageContext) -> None:
        if cmd.reuse_stems and vocals.is_file() and instrumental.is_file():
            ctx.progress(1.0, "reusing existing stems")
            return
        backend.separate(audio, vocals, instrumental, ctx)

    run_stage(s.PipelineStage.SEPARATION, separate)

    def log(level: s.WorkerLogLevel, message: str) -> None:
        emit(s.LogEvent(level=level, message=message, task_id=cmd.task_id))

    language = cmd.language
    windows: list[Window] = []
    reference: list[LyricLineText] | None = None
    if cmd.lyrics and cmd.lyrics_kind is s.LyricsKind.SYNCED:
        lines = parse_lrc(cmd.lyrics, duration)
        sync = lrc_offset(lines, backend.vocal_activity(vocals))
        if sync.fit >= MIN_LRC_FIT:
            if sync.offset_ms:
                log(s.WorkerLogLevel.INFO, f"LRC timing shifted by {sync.offset_ms:+} ms to match the vocals (fit {sync.fit:.2f})")
            windows = windows_from_lrc(shift_lines(lines, sync.offset_ms), duration)
        elif backend.missing_models([s.PipelineStage.TRANSCRIPTION]):
            log(s.WorkerLogLevel.WARNING, f"LRC timing doesn't fit the vocals (fit {sync.fit:.2f}) and Whisper isn't downloaded; using it anyway")
            windows = windows_from_lrc(shift_lines(lines, sync.offset_ms), duration)
        else:
            # Probably another arrangement of the song: keep the words, let Whisper find where they go.
            log(s.WorkerLogLevel.WARNING, f"LRC timing doesn't fit the vocals (fit {sync.fit:.2f}); placing its lines by transcription")
            reference = [LyricLineText(l.text) for l in lines]

    if not windows:
        if reference is None:
            reference = parse_plain(cmd.lyrics) if cmd.lyrics else []
        prompt = whisper_prompt(reference) if reference else None
        transcript: Transcript = run_stage(
            s.PipelineStage.TRANSCRIPTION, lambda ctx: backend.transcribe(vocals, language, prompt, ctx))
        language = language or transcript.language
        if reference:
            windows = line_windows_from_transcript(reference, transcript.words, duration)
        else:
            windows = [Window(start, end, words_of(" ".join(w.text for w in words)))
                       for start, end, words in transcript.segments if words]

    if not windows:
        raise RuntimeError("no lyrics to align: none supplied and transcription found no words")

    aligned = run_stage(s.PipelineStage.ALIGNMENT, lambda ctx: backend.align(vocals, windows, language, ctx))
    track = run_stage(s.PipelineStage.PITCH, lambda ctx: backend.track_pitch(vocals, ctx))
    tempo = run_stage(s.PipelineStage.TEMPO, lambda ctx: backend.estimate_tempo(instrumental, ctx))

    return s.TrackAnalysisResult(
        vocals_path=str(vocals.resolve()),
        instrumental_path=str(instrumental.resolve()),
        tempo_bpm=float(tempo),
        language=language or "und",
        lines=build_lines(aligned, track, language),
        models=backend.model_names(),
    )


def run_separation(cmd: s.SeparateStemsCommand, backend: Backend, emit: Callable[[s.Contract], None],
                   is_cancelled: Callable[[], bool]) -> None:
    """Only the separation stage: vocals.wav and instrumental.wav into cmd.output_folder."""
    audio = Path(cmd.audio_path)
    if not audio.is_file():
        raise FileNotFoundError(f"audio file not found: {audio}")
    missing = backend.missing_models([s.PipelineStage.SEPARATION])
    if missing:
        raise RuntimeError(f"models not downloaded: {', '.join(missing)} (run: python -m singularity_inference.models fetch)")
    out_dir = Path(cmd.output_folder)
    out_dir.mkdir(parents=True, exist_ok=True)

    ctx = StageContext(cmd.task_id, s.PipelineStage.SEPARATION, emit, is_cancelled)
    ctx.check()
    emit(s.StageStartedEvent(task_id=cmd.task_id, stage=s.PipelineStage.SEPARATION))
    started = time.monotonic()
    try:
        backend.separate(audio, out_dir / "vocals.wav", out_dir / "instrumental.wav", ctx)
    finally:
        release_gpu_memory()
    ctx.check()
    emit(s.StageCompletedEvent(task_id=cmd.task_id, stage=s.PipelineStage.SEPARATION,
                               duration_ms=int((time.monotonic() - started) * 1000)))


def create_backend(name: str | None = None) -> Backend:
    name = name or os.environ.get("SINGULARITY_INFERENCE_BACKEND") or "ml"
    if name == "fake":
        from .backends.fake import FakeBackend

        return FakeBackend()
    if name == "ml":
        from .backends.ml import MlBackend

        return MlBackend()
    raise ValueError(f"unknown backend {name!r} (expected 'ml' or 'fake')")
