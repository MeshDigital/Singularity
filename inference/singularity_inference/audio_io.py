"""Audio decoding through ffmpeg, so any format Soulseek delivers (flac, mp3, m4a, opus, …) works.

The app passes its ffmpeg via SINGULARITY_FFMPEG; otherwise ffmpeg/ffprobe must be on PATH.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path


def _tool(name: str) -> str:
    ffmpeg = os.environ.get("SINGULARITY_FFMPEG")
    if ffmpeg:
        candidate = Path(ffmpeg).with_name(name + Path(ffmpeg).suffix)
        if candidate.is_file():
            return str(candidate)
    found = shutil.which(name)
    if not found:
        raise FileNotFoundError(f"{name} not found (set SINGULARITY_FFMPEG or put it on PATH)")
    return found


def duration_ms(path: Path) -> int:
    out = subprocess.run(
        [_tool("ffprobe"), "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
        capture_output=True, text=True, check=True,
    ).stdout.strip()
    return int(round(float(out) * 1000))


def load(path: Path, sample_rate: int, channels: int = 1):
    """float32 numpy array, shape (samples,) for mono or (channels, samples)."""
    import numpy as np

    raw = subprocess.run(
        [_tool("ffmpeg"), "-v", "error", "-nostdin", "-i", str(path),
         "-f", "f32le", "-acodec", "pcm_f32le", "-ac", str(channels), "-ar", str(sample_rate), "-"],
        capture_output=True, check=True,
    ).stdout
    audio = np.frombuffer(raw, dtype=np.float32)
    return audio if channels == 1 else audio.reshape(-1, channels).T.copy()
