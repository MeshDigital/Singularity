"""Turns aligned words plus a pitch track into the protocol's TrackAnalysisResult."""

from __future__ import annotations

import bisect
import math
from dataclasses import dataclass, field

from . import schemas as s
from .lyrics import AlignedWord, split_word_timing

# Frames below this periodicity confidence don't count as sung pitch.
VOICED_CONFIDENCE = 0.5


@dataclass
class PitchTrack:
    """Frame-wise pitch of the vocal stem. hz <= 0 means unvoiced."""

    times_ms: list[float] = field(default_factory=list)
    hz: list[float] = field(default_factory=list)
    confidence: list[float] = field(default_factory=list)


def hz_to_midi(hz: float) -> float:
    return 69.0 + 12.0 * math.log2(hz / 440.0)


def syllable_pitch(track: PitchTrack, start_ms: int, end_ms: int) -> tuple[int | None, float]:
    """(median MIDI note over the span's voiced frames, mean confidence over all its frames).
    The median rather than the mean keeps a single octave-jumped frame from dragging the note."""
    lo = bisect.bisect_left(track.times_ms, start_ms)
    hi = bisect.bisect_left(track.times_ms, end_ms)
    if hi <= lo:
        return None, 0.0
    conf = track.confidence[lo:hi]
    voiced = sorted(hz_to_midi(h) for h, c in zip(track.hz[lo:hi], conf) if h > 0 and c >= VOICED_CONFIDENCE)
    mean_conf = sum(conf) / len(conf)
    if not voiced:
        return None, mean_conf
    mid = len(voiced) // 2
    median = voiced[mid] if len(voiced) % 2 else (voiced[mid - 1] + voiced[mid]) / 2
    return int(round(median)), mean_conf


def build_lines(words_per_line: list[list[AlignedWord]], track: PitchTrack, language: str | None) -> list[s.LyricLine]:
    lines = []
    for words in words_per_line:
        syllables = []
        for word in words:
            for i, (text, start, end) in enumerate(split_word_timing(word, language)):
                end = max(end, start + 1)
                tone, conf = syllable_pitch(track, start, end)
                syllables.append(s.TimedSyllable(
                    text=text,
                    start_ms=start,
                    end_ms=end,
                    starts_word=i == 0,
                    midi_tone=tone,
                    pitch_confidence=min(1.0, max(0.0, conf)),
                    alignment_confidence=min(1.0, max(0.0, word.confidence)),
                ))
        if syllables:
            lines.append(s.LyricLine(syllables=syllables))
    return lines
