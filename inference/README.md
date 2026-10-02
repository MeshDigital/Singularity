# singularity-inference

The Python worker that turns a song's audio into an UltraStar chart: it separates the vocal stem, aligns lyric syllables to it, tracks their pitch and estimates the tempo. The Singularity app launches it as a child process (`Services/Inference/InferenceWorkerHost.cs`). The two talk over newline-delimited JSON: commands go in on stdin and events come out on stdout. Diagnostics go to stderr, which the app only logs.

The message shapes are defined in `Singularity.Contracts` (C#), and `singularity_inference/schemas.py` mirrors them. Both test suites parse the same fixtures in `../Singularity.Contracts/Fixtures`, so if either side drifts its tests fail.

## Setup

```powershell
py -3.11 -m venv .venv
.venv\Scripts\python -m pip install -e .[dev]          # protocol, lyrics, fake backend, tests
.venv\Scripts\python -m pytest
```

Running the real model stages also needs the `ml` extra. Install torch from the CUDA index first; RTX 50-series cards need the CUDA 12.8 wheels:

```powershell
.venv\Scripts\python -m pip install "torch>=2.7" "torchaudio>=2.7,<2.9" --index-url https://download.pytorch.org/whl/cu128
.venv\Scripts\python -m pip install -e .[ml]
```

ffmpeg must be on PATH, or the app passes its own copy in `SINGULARITY_FFMPEG`.

## Models

Weights go to `%LOCALAPPDATA%\Singularity\models`, or to `SINGULARITY_MODEL_DIR` if that is set.

```powershell
.venv\Scripts\python -m singularity_inference.models list    # what's needed, sizes, present/missing
.venv\Scripts\python -m singularity_inference.models fetch   # download everything missing
```

| Model | Stage | Size |
|---|---|---|
| `htdemucs_ft` | separation | ~320 MB |
| `large-v3` (faster-whisper; `SINGULARITY_WHISPER_MODEL=medium` for ~1.5 GB) | transcription, only when there are no synced lyrics | ~3 GB |
| `mms_fa` (torchaudio wav2vec2 forced aligner) | alignment | ~1.2 GB |
| SwiftF0 | pitch | ships in the pip package |

The worker reports missing models in its `ready` event. A task that needs a missing model fails and its error names the fetch command.

## How a track is processed

- **Synced lyrics (LRC from LRCLIB):** the line times are known, so the stages are separation → alignment per line → pitch → tempo. Whisper is not loaded.
- **Plain lyrics:** Whisper transcribes the vocal stem, with the lyrics as its prompt to stop it inventing text. The transcript is diffed against the lyrics to place each line, then each line is force-aligned.
- **No lyrics:** the Whisper transcript is the lyrics, and alignment refines its word times.

Stages run one at a time. Each loads its model, uses it and drops it, and GPU memory is freed before the next stage starts. Only the largest single model has to fit in VRAM, not the sum, which keeps the pipeline within 8 GB.

`--backend fake` (or `SINGULARITY_INFERENCE_BACKEND=fake`) swaps the models for a deterministic stand-in. The protocol tests and the C# host tests use it.
