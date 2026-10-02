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


# Octave smoothing looks this many syllables either side.
OCTAVE_CONTEXT = 8
# A sustained note may run on until this close to the next syllable.
MIN_GAP_MS = 20


def voiced_until(track: PitchTrack, from_ms: int, limit_ms: int) -> int:
    """End of the voiced run that is still going at `from_ms`, capped at `limit_ms`.
    Aligners mark where a syllable starts well but end it early; singers hold the vowel."""
    i = bisect.bisect_left(track.times_ms, from_ms)
    end = from_ms
    while i < len(track.times_ms) and track.times_ms[i] < limit_ms:
        if not (track.hz[i] > 0 and track.confidence[i] >= VOICED_CONFIDENCE):
            return max(end, from_ms)
        end = int(track.times_ms[i])
        i += 1
    # Still voiced at the limit (or the track ran out while voiced): hold all the way.
    return limit_ms if end > from_ms else from_ms


def smooth_octaves(tones: list[int | None], context: int = OCTAVE_CONTEXT) -> list[int | None]:
    """Moves each tone by whole octaves to the one nearest the median of its neighbours, fixing the
    pitch tracker's octave errors without changing any note name."""
    out = list(tones)
    for i, tone in enumerate(tones):
        if tone is None:
            continue
        around = sorted(t for t in tones[max(0, i - context):i + context + 1] if t is not None)
        median = around[len(around) // 2]
        out[i] = tone + 12 * round((median - tone) / 12)
    return out


# A melisma note must last this long; shorter wobbles (vibrato, scoops) stay part of a neighbour.
MIN_SEGMENT_MS = 120
# Median filter width over voiced frames before quantising, against frame-to-frame jitter.
SMOOTH_FRAMES = 5


def pitch_segments(track: PitchTrack, start_ms: int, end_ms: int, base_tone: int) -> list[tuple[int, int, int]] | None:
    """Splits a syllable into the notes it is sung on, as (start_ms, end_ms, midi_tone).

    Voiced frames are median-smoothed, rounded to semitones and folded to the octave of `base_tone`
    (the syllable's own, octave-smoothed tone). Runs of equal notes shorter than MIN_SEGMENT_MS merge
    into their longer neighbour. Returns None when the syllable stays on a single note.
    """
    lo = bisect.bisect_left(track.times_ms, start_ms)
    hi = bisect.bisect_left(track.times_ms, end_ms)
    frames = [(track.times_ms[i], hz_to_midi(track.hz[i])) for i in range(lo, hi)
              if track.hz[i] > 0 and track.confidence[i] >= VOICED_CONFIDENCE]
    if len(frames) < 2:
        return None

    half = SMOOTH_FRAMES // 2
    values = [m for _, m in frames]
    runs: list[list] = []  # [start_ms, tone]
    for k, (t, _) in enumerate(frames):
        window = sorted(values[max(0, k - half):k + half + 1])
        tone = int(round(window[len(window) // 2]))
        tone += 12 * round((base_tone - tone) / 12)
        if not runs or runs[-1][1] != tone:
            runs.append([t, tone])

    # Each run lasts until the next starts; the first starts at the syllable, the last ends with it.
    bounds = [start_ms] + [int(r[0]) for r in runs[1:]] + [end_ms]
    segs = [[bounds[i], bounds[i + 1], r[1]] for i, r in enumerate(runs)]

    while len(segs) > 1:
        k = min(range(len(segs)), key=lambda i: segs[i][1] - segs[i][0])
        if segs[k][1] - segs[k][0] >= MIN_SEGMENT_MS:
            break
        # Merge the shortest into its longer neighbour, keeping the neighbour's note.
        left = segs[k - 1] if k > 0 else None
        right = segs[k + 1] if k + 1 < len(segs) else None
        if right is None or (left is not None and left[1] - left[0] >= right[1] - right[0]):
            left[1] = segs[k][1]
        else:
            right[0] = segs[k][0]
        del segs[k]
        # Neighbours that now share a note become one.
        merged = [segs[0]]
        for seg in segs[1:]:
            if seg[2] == merged[-1][2]:
                merged[-1][1] = seg[1]
            else:
                merged.append(seg)
        segs = merged

    return [(a, b, t) for a, b, t in segs] if len(segs) > 1 else None


def build_lines(words_per_line: list[list[AlignedWord]], track: PitchTrack, language: str | None) -> list[s.LyricLine]:
    # Flatten to (line index, word, syllable index in word, text, start, end) so sustain can look at the next syllable.
    flat = []
    for li, words in enumerate(words_per_line):
        for word in words:
            for si, (text, start, end) in enumerate(split_word_timing(word, language)):
                flat.append((li, word, si, text, start, max(end, start + 1)))

    pitched = []
    for k, (li, word, si, text, start, end) in enumerate(flat):
        next_start = flat[k + 1][4] if k + 1 < len(flat) else end + 2000
        end = max(end, voiced_until(track, end, max(end, next_start - MIN_GAP_MS)))
        tone, conf = syllable_pitch(track, start, end)
        pitched.append((li, word, si, text, start, end, tone, conf))

    tones = smooth_octaves([p[6] for p in pitched])
    lines: list[list[s.TimedSyllable]] = [[] for _ in words_per_line]
    for (li, word, si, text, start, end, _, conf), tone in zip(pitched, tones):
        segments = pitch_segments(track, start, end, tone) if tone is not None else None
        lines[li].append(s.TimedSyllable(
            text=text,
            start_ms=start,
            end_ms=end,
            starts_word=si == 0,
            midi_tone=tone,
            pitch_confidence=min(1.0, max(0.0, conf)),
            alignment_confidence=min(1.0, max(0.0, word.confidence)),
            segments=[s.PitchSegment(start_ms=a, end_ms=b, midi_tone=t) for a, b, t in segments] if segments else None,
        ))
    return [s.LyricLine(syllables=syl) for syl in lines if syl]
