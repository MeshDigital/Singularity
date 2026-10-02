"""Where model weights live, which ones the pipeline needs, and fetching them.

All downloads go under one cache directory:
  1. $SINGULARITY_MODEL_DIR, if set;
  2. otherwise %LOCALAPPDATA%\\Singularity\\models on Windows (local, not roaming: several GB);
  3. otherwise ~/.cache/singularity/models.

Each library is pointed at a subfolder via its own environment variable (TORCH_HOME, HF_HOME), which
`configure_cache_env` sets before torch or huggingface are imported. A model counts as present once
`fetch` has completed for it, recorded as a marker file. That's simpler and more reliable than
second-guessing each library's internal cache layout.

    python -m singularity_inference.models list
    python -m singularity_inference.models fetch [name ...]
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

MODEL_DIR_ENV = "SINGULARITY_MODEL_DIR"
WHISPER_MODEL_ENV = "SINGULARITY_WHISPER_MODEL"
DEFAULT_WHISPER_MODEL = "large-v3"


def model_dir() -> Path:
    if override := os.environ.get(MODEL_DIR_ENV):
        return Path(override)
    if local := os.environ.get("LOCALAPPDATA"):
        return Path(local) / "Singularity" / "models"
    return Path.home() / ".cache" / "singularity" / "models"


def configure_cache_env(root: Path | None = None) -> Path:
    """Points torch hub and huggingface at the model dir. Call before importing torch/faster_whisper."""
    root = root or model_dir()
    os.environ.setdefault("TORCH_HOME", str(root / "torch"))
    os.environ.setdefault("HF_HOME", str(root / "huggingface"))
    os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
    return root


def whisper_model_name() -> str:
    return os.environ.get(WHISPER_MODEL_ENV) or DEFAULT_WHISPER_MODEL


@dataclass(frozen=True)
class ModelSpec:
    name: str
    role: str
    approx_size: str
    source: str
    fetch: Callable[[Path], None] | None  # None: ships inside a pip package, nothing to download

    def is_present(self, root: Path) -> bool:
        return self.fetch is None or _marker(root, self.name).exists()


def _marker(root: Path, name: str) -> Path:
    return root / ".fetched" / name


def _fetch_demucs(name: str) -> Callable[[Path], None]:
    def fetch(root: Path) -> None:
        from demucs.pretrained import get_model  # TORCH_HOME decides where the checkpoints land

        get_model(name)

    return fetch


def _fetch_whisper(name: str) -> Callable[[Path], None]:
    def fetch(root: Path) -> None:
        from faster_whisper.utils import download_model

        download_model(name, cache_dir=str(root / "whisper"))

    return fetch


def _fetch_mms_fa(root: Path) -> None:
    import torchaudio

    torchaudio.pipelines.MMS_FA.get_model()


def registry() -> dict[str, ModelSpec]:
    whisper = whisper_model_name()
    specs = [
        ModelSpec("htdemucs_ft", "separation", "~320 MB (4 fine-tuned models)", "Demucs v4, facebookresearch", _fetch_demucs("htdemucs_ft")),
        ModelSpec(whisper, "transcription", "~3 GB" if whisper.startswith("large") else "~1.5 GB",
                  f"faster-whisper ({whisper}), Systran", _fetch_whisper(whisper)),
        ModelSpec("mms_fa", "alignment", "~1.2 GB", "torchaudio MMS forced aligner (wav2vec2)", _fetch_mms_fa),
        ModelSpec("swift_f0", "pitch", "bundled", "swift-f0 pip package", None),
    ]
    return {s.name: s for s in specs}


def model_names() -> dict[str, str]:
    """Model name per pipeline role, as reported in the ready event and the result."""
    return {s.role: s.name for s in registry().values()}


def missing_models(root: Path | None = None) -> list[str]:
    root = root or model_dir()
    return [s.name for s in registry().values() if not s.is_present(root)]


def fetch(names: list[str] | None = None, root: Path | None = None, out=sys.stdout) -> list[str]:
    """Downloads the named models (default: every missing one). Returns the names fetched."""
    root = configure_cache_env(root)
    specs = registry()
    unknown = [n for n in names or [] if n not in specs]
    if unknown:
        raise ValueError(f"unknown model(s): {', '.join(unknown)}; known: {', '.join(specs)}")
    todo = [specs[n] for n in names] if names else [s for s in specs.values() if not s.is_present(root)]

    fetched = []
    for spec in todo:
        if spec.fetch is None:
            continue
        print(f"fetching {spec.name} ({spec.role}, {spec.approx_size}) into {root} ...", file=out, flush=True)
        spec.fetch(root)
        marker = _marker(root, spec.name)
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text(spec.source, encoding="utf-8")
        fetched.append(spec.name)
    return fetched


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m singularity_inference.models")
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", help="show models, sizes and whether they are downloaded")
    f = sub.add_parser("fetch", help="download models (default: all missing)")
    f.add_argument("names", nargs="*")
    args = parser.parse_args(argv)

    root = model_dir()
    if args.cmd == "list":
        print(f"model dir: {root}")
        for s in registry().values():
            state = "present" if s.is_present(root) else "missing"
            print(f"  {s.name:<14} {s.role:<14} {s.approx_size:<32} {state}")
        return 0

    fetch(args.names or None)
    return 0


if __name__ == "__main__":
    sys.exit(main())
