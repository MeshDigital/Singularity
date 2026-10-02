"""Deterministic stand-in for the model stages, so the protocol loop and the C# host can be tested
end to end without models or a GPU. Selected with SINGULARITY_INFERENCE_BACKEND=fake.

Test knobs (environment):
  SINGULARITY_FAKE_STAGE_DELAY_MS   time each stage takes; cancellation is checked every 10 ms
  SINGULARITY_FAKE_IGNORE_CANCEL=1  stages sleep without checking, like a long CUDA call that can't be interrupted
"""

from __future__ import annotations

import os
import time
from pathlib import Path

from .. import schemas as s
from ..assemble import PitchTrack
from ..lyrics import AlignedWord, Window, distribute_evenly
from ..pipeline import StageContext, Transcript

FAKE_DURATION_MS = 60_000


class FakeBackend:
    device = "cpu"

    def __init__(self) -> None:
        self._delay_ms = int(os.environ.get("SINGULARITY_FAKE_STAGE_DELAY_MS", "0"))
        self._ignore_cancel = os.environ.get("SINGULARITY_FAKE_IGNORE_CANCEL") == "1"

    def _work(self, ctx: StageContext) -> None:
        if self._ignore_cancel:
            time.sleep(self._delay_ms / 1000)
            return
        steps = max(1, self._delay_ms // 10)
        for i in range(steps):
            if self._delay_ms:
                time.sleep(0.01)
            ctx.check()
            if i in (steps // 2, steps - 1):
                ctx.progress((i + 1) / steps)

    def duration_ms(self, audio_path: Path) -> int:
        return FAKE_DURATION_MS

    def separate(self, audio_path: Path, vocals: Path, instrumental: Path, ctx: StageContext) -> None:
        self._work(ctx)
        vocals.write_bytes(b"fake vocals")
        instrumental.write_bytes(b"fake instrumental")

    def transcribe(self, vocals: Path, language: str | None, prompt: str | None, ctx: StageContext) -> Transcript:
        self._work(ctx)
        text = (prompt or "la la la\nna na na").split()
        words = distribute_evenly(tuple(_w(t) for t in text), 1000, 1000 + 500 * len(text), 0.8)
        half = len(words) // 2 or 1
        segments = [(words[0].start_ms, words[half - 1].end_ms, words[:half])]
        if words[half:]:
            segments.append((words[half].start_ms, words[-1].end_ms, words[half:]))
        return Transcript(language or "en", segments)

    def align(self, vocals: Path, windows: list[Window], language: str | None, ctx: StageContext) -> list[list[AlignedWord]]:
        self._work(ctx)
        return [distribute_evenly(w.words, w.start_ms, w.end_ms, 0.9) for w in windows]

    def track_pitch(self, vocals: Path, ctx: StageContext) -> PitchTrack:
        self._work(ctx)
        times = [float(t) for t in range(0, FAKE_DURATION_MS, 10)]
        # A slow scale between A3 and A4, fully voiced.
        hz = [220.0 * 2 ** ((t // 2000 % 13) / 12) for t in times]
        return PitchTrack(times, hz, [0.95] * len(times))

    def estimate_tempo(self, instrumental: Path, ctx: StageContext) -> float:
        self._work(ctx)
        return 120.0

    def model_names(self) -> dict[str, str]:
        return {"separation": "fake", "transcription": "fake", "alignment": "fake", "pitch": "fake"}

    def missing_models(self, stages: list[s.PipelineStage]) -> list[str]:
        return []


def _w(text: str):
    from ..lyrics import Word, normalize

    return Word(text, normalize(text))
