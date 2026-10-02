"""Lyric text handling: LRC parsing, alignment-friendly normalisation, syllables, line windows.

All functions here are pure; the model-backed stages live in backends/ml.py.
"""

from __future__ import annotations

import difflib
import re
import unicodedata
from dataclasses import dataclass
from functools import lru_cache

_LRC_TIME = re.compile(r"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")
_LRC_WORD_TIME = re.compile(r"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")
_SECTION_HEADER = re.compile(r"^\s*[\[(][^\])]*[\])]\s*$")  # "[Chorus]", "(Verse 2)"


@dataclass(frozen=True)
class LyricLineText:
    text: str
    start_ms: int | None = None  # known for LRC lines
    end_ms: int | None = None


@dataclass(frozen=True)
class Word:
    """A reference word: `text` as displayed, `norm` as the aligner sees it (may be empty)."""

    text: str
    norm: str


@dataclass(frozen=True)
class AlignedWord:
    text: str
    start_ms: int
    end_ms: int
    confidence: float


@dataclass(frozen=True)
class Window:
    """A stretch of audio to align one lyric line in."""

    start_ms: int
    end_ms: int
    words: tuple[Word, ...]


def parse_lrc(text: str, duration_ms: int | None = None) -> list[LyricLineText]:
    """Timed lines in order. Metadata tags and blank (instrumental) lines are dropped, but a blank
    line still ends the line before it. Each line ends where the next timestamp starts."""
    stamped: list[tuple[int, str]] = []
    for raw in text.splitlines():
        stamps = list(_LRC_TIME.finditer(raw))
        if not stamps:
            continue
        lyric = _LRC_WORD_TIME.sub("", raw[stamps[-1].end():]).strip()
        for m in stamps:
            minutes, seconds, frac = int(m.group(1)), int(m.group(2)), m.group(3) or "0"
            frac_ms = int(frac.ljust(3, "0")[:3])
            stamped.append((minutes * 60_000 + seconds * 1000 + frac_ms, lyric))

    stamped.sort(key=lambda x: x[0])
    lines = []
    for i, (start, lyric) in enumerate(stamped):
        if not lyric:
            continue
        end = stamped[i + 1][0] if i + 1 < len(stamped) else duration_ms
        lines.append(LyricLineText(lyric, start, end))
    return lines


def parse_plain(text: str) -> list[LyricLineText]:
    """Non-empty lines, without section headers like "[Chorus]"."""
    return [
        LyricLineText(line.strip())
        for line in text.splitlines()
        if line.strip() and not _SECTION_HEADER.match(line)
    ]


def normalize(word: str) -> str:
    """Lowercase ASCII letters and apostrophes, diacritics stripped: what the MMS aligner's
    tokenizer accepts. Returns "" for words with nothing alignable (numbers, symbols, non-Latin)."""
    decomposed = unicodedata.normalize("NFKD", word.lower().replace("’", "'"))
    return "".join(c for c in decomposed if ("a" <= c <= "z") or c == "'").strip("'")


def words_of(line: str) -> tuple[Word, ...]:
    return tuple(Word(w, normalize(w)) for w in line.split())


def line_windows_from_transcript(
    lines: list[LyricLineText],
    transcript: list[AlignedWord],
    duration_ms: int,
    pad_ms: int = 300,
) -> list[Window]:
    """Places reference lines in time using a (less accurate) transcript, the way
    UltraStarKaraokeMaker reconciles Whisper output with known lyrics: the reference words are
    diffed against the transcribed words, each line spans its matched words, and lines with no
    match are spread over the gap between their neighbours."""
    ref = [(li, w) for li, line in enumerate(lines) for w in words_of(line.text)]
    ref_norm = [w.norm for _, w in ref]
    hyp_norm = [normalize(w.text) for w in transcript]

    line_times: list[list[int]] = [[] for _ in lines]  # matched start/end ms per line
    matcher = difflib.SequenceMatcher(a=ref_norm, b=hyp_norm, autojunk=False)
    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            li = ref[block.a + k][0]
            hw = transcript[block.b + k]
            line_times[li] += [hw.start_ms, hw.end_ms]

    spans: list[tuple[int, int] | None] = [(min(t), max(t)) if t else None for t in line_times]

    # Unmatched lines: share the time between the previous and next matched line evenly.
    i = 0
    while i < len(spans):
        if spans[i] is not None:
            i += 1
            continue
        j = i
        while j < len(spans) and spans[j] is None:
            j += 1
        gap_start = spans[i - 1][1] if i > 0 else 0
        gap_end = spans[j][0] if j < len(spans) else duration_ms
        step = max(1, (gap_end - gap_start) // (j - i))
        for k in range(i, j):
            spans[k] = (gap_start + (k - i) * step, gap_start + (k - i + 1) * step)
        i = j

    windows = []
    for li, line in enumerate(lines):
        start, end = spans[li]  # type: ignore[misc]
        windows.append(Window(max(0, start - pad_ms), min(duration_ms, end + pad_ms), words_of(line.text)))
    return windows


def windows_from_lrc(lines: list[LyricLineText], duration_ms: int, pad_ms: int = 300) -> list[Window]:
    """LRC lines already have times; pad them a little because LRC stamps are often early or late."""
    out = []
    for line in lines:
        start = line.start_ms or 0
        end = line.end_ms if line.end_ms is not None else duration_ms
        out.append(Window(max(0, start - pad_ms), min(duration_ms, end + pad_ms), words_of(line.text)))
    return out


def distribute_evenly(words: tuple[Word, ...], start_ms: int, end_ms: int, confidence: float) -> list[AlignedWord]:
    """Fallback timing when a window can't be aligned: words share it in proportion to their length."""
    weights = [max(1, len(w.norm) or len(w.text)) for w in words]
    total = sum(weights)
    out, t = [], float(start_ms)
    for w, weight in zip(words, weights):
        dur = (end_ms - start_ms) * weight / total
        out.append(AlignedWord(w.text, round(t), round(t + dur), confidence))
        t += dur
    return out


def fill_unaligned(words: tuple[Word, ...], aligned: dict[int, AlignedWord], start_ms: int, end_ms: int) -> list[AlignedWord]:
    """Completes a window's word list when only some words could be aligned (the rest had no
    alignable characters): each run of missing words shares the gap between its aligned neighbours."""
    out: list[AlignedWord] = []
    i = 0
    while i < len(words):
        if i in aligned:
            out.append(aligned[i])
            i += 1
            continue
        j = i
        while j < len(words) and j not in aligned:
            j += 1
        gap_start = out[-1].end_ms if out else start_ms
        gap_end = aligned[j].start_ms if j < len(words) else end_ms
        out += distribute_evenly(words[i:j], gap_start, max(gap_start + 1, gap_end), 0.0)
        i = j
    return out


@lru_cache(maxsize=16)
def _hyphenator(language: str | None):
    if not language:
        return None
    import pyphen

    # pyphen's "en" resolves to en_GB's dictionary, which splits far fewer words than en_US's.
    preferred = {"en": "en_US"}.get(language.lower(), language.replace("-", "_"))
    lang = pyphen.language_fallback(preferred)
    return pyphen.Pyphen(lang=lang) if lang else None


def syllables(word: str, language: str | None) -> list[str]:
    """Splits a word into sung syllables with pyphen; unsplit when the language isn't known."""
    dic = _hyphenator(language)
    if dic is None or len(word) < 4:
        return [word]
    parts = [p for p in dic.inserted(word, hyphen="\x00").split("\x00") if p]
    return parts or [word]


def split_word_timing(word: AlignedWord, language: str | None) -> list[tuple[str, int, int]]:
    """Syllables of an aligned word with their share of its time, proportional to letter count."""
    parts = syllables(word.text, language)
    if len(parts) == 1:
        return [(word.text, word.start_ms, word.end_ms)]
    weights = [max(1, sum(c.isalpha() for c in p)) for p in parts]
    total = sum(weights)
    out, t = [], float(word.start_ms)
    for p, weight in zip(parts, weights):
        dur = (word.end_ms - word.start_ms) * weight / total
        out.append((p, round(t), round(t + dur)))
        t += dur
    return out
