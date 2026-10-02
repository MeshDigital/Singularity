"""The real model stages. Every heavy import is local to its stage and every model is dropped when
its stage returns, so only one model is in VRAM at a time (pipeline.release_gpu_memory runs after each).

Not yet validated end to end on real hardware: the first run with downloaded weights is the check.
"""

from __future__ import annotations

from pathlib import Path

from .. import audio_io
from .. import schemas as s
from ..assemble import PitchTrack
from ..lyrics import AlignedWord, Window, distribute_evenly, fill_unaligned
from ..models import configure_cache_env, missing_models, model_dir, model_names, whisper_model_name
from ..pipeline import StageContext, Transcript

ALIGN_SAMPLE_RATE = 16_000

# Which model each stage needs, for the up-front "are the weights downloaded?" check.
_STAGE_MODELS = {
    s.PipelineStage.SEPARATION: "htdemucs_ft",
    s.PipelineStage.ALIGNMENT: "mms_fa",
}


class MlBackend:
    def __init__(self) -> None:
        self._root = configure_cache_env()
        import torch

        self.device = "cuda" if torch.cuda.is_available() else "cpu"

    # ── bookkeeping ──────────────────────────────────────────────────────────────────────────────

    def duration_ms(self, audio_path: Path) -> int:
        return audio_io.duration_ms(audio_path)

    def model_names(self) -> dict[str, str]:
        return model_names()

    def missing_models(self, stages: list[s.PipelineStage]) -> list[str]:
        needed = {_STAGE_MODELS[st] for st in stages if st in _STAGE_MODELS}
        if s.PipelineStage.TRANSCRIPTION in stages:
            needed.add(whisper_model_name())
        return [m for m in missing_models(self._root) if m in needed]

    # ── stages ───────────────────────────────────────────────────────────────────────────────────

    def separate(self, audio_path: Path, vocals: Path, instrumental: Path, ctx: StageContext) -> None:
        import soundfile as sf
        import torch
        from demucs.apply import apply_model
        from demucs.pretrained import get_model

        model = get_model("htdemucs_ft")
        model.eval()
        ctx.progress(0.05, "model loaded")

        wav = torch.from_numpy(audio_io.load(audio_path, model.samplerate, channels=2))
        ref = wav.mean(0)
        mean, std = ref.mean(), ref.std() + 1e-8
        with torch.no_grad():
            sources = apply_model(model, ((wav - mean) / std)[None], device=self.device,
                                  split=True, overlap=0.25, shifts=1, progress=False)[0]
        sources = sources * std + mean
        ctx.progress(0.9, "writing stems")

        v = model.sources.index("vocals")
        vocal = sources[v]
        accompaniment = sources.sum(0) - vocal
        for path, stem in ((vocals, vocal), (instrumental, accompaniment)):
            sf.write(str(path), stem.clamp(-1, 1).T.cpu().numpy(), model.samplerate, subtype="PCM_16")
        del model, sources, wav

    def transcribe(self, vocals: Path, language: str | None, prompt: str | None, ctx: StageContext) -> Transcript:
        from faster_whisper import WhisperModel

        model = WhisperModel(
            whisper_model_name(),
            device=self.device,
            compute_type="float16" if self.device == "cuda" else "int8",
            download_root=str(model_dir() / "whisper"),
        )
        audio = audio_io.load(vocals, ALIGN_SAMPLE_RATE)
        segments_iter, info = model.transcribe(
            audio, language=language, initial_prompt=prompt, word_timestamps=True,
            vad_filter=True, condition_on_previous_text=False,
        )
        segments = []
        for seg in segments_iter:  # lazy: decoding happens while iterating
            words = [AlignedWord(w.word.strip(), int(w.start * 1000), int(w.end * 1000), float(w.probability))
                     for w in (seg.words or []) if w.word.strip()]
            if words:
                segments.append((int(seg.start * 1000), int(seg.end * 1000), words))
            ctx.progress(seg.end / max(info.duration, 1e-3))
        del model
        return Transcript(info.language, segments)

    def align(self, vocals: Path, windows: list[Window], language: str | None, ctx: StageContext) -> list[list[AlignedWord]]:
        import torch
        import torchaudio

        bundle = torchaudio.pipelines.MMS_FA
        model = bundle.get_model(with_star=False).to(self.device)
        tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()
        vocab = set(bundle.get_dict())
        audio = torch.from_numpy(audio_io.load(vocals, bundle.sample_rate))

        out: list[list[AlignedWord]] = []
        for i, window in enumerate(windows):
            ctx.progress(i / len(windows))
            # Only words with characters the aligner knows are aligned; the rest are filled in between.
            indexed = [(k, "".join(c for c in w.norm if c in vocab)) for k, w in enumerate(window.words)]
            indexed = [(k, t) for k, t in indexed if t]
            a = int(window.start_ms * bundle.sample_rate / 1000)
            b = int(window.end_ms * bundle.sample_rate / 1000)
            segment = audio[a:b]
            if not indexed or segment.numel() < bundle.sample_rate // 10:
                out.append(distribute_evenly(window.words, window.start_ms, window.end_ms, 0.0))
                continue

            with torch.inference_mode():
                emission, _ = model(segment[None].to(self.device))
            try:
                spans = aligner(emission[0], tokenizer([t for _, t in indexed]))
            except RuntimeError:  # more tokens than frames: the window is too short for its text
                out.append(distribute_evenly(window.words, window.start_ms, window.end_ms, 0.0))
                continue

            ms_per_frame = (segment.numel() / bundle.sample_rate * 1000) / emission.size(1)
            aligned = {}
            for (k, _), word_spans in zip(indexed, spans):
                start = window.start_ms + int(word_spans[0].start * ms_per_frame)
                end = window.start_ms + int(word_spans[-1].end * ms_per_frame)
                score = sum(sp.score * len(sp) for sp in word_spans) / max(1, sum(len(sp) for sp in word_spans))
                aligned[k] = AlignedWord(window.words[k].text, start, max(end, start + 1), float(score))
            out.append(fill_unaligned(window.words, aligned, window.start_ms, window.end_ms))
        del model
        return out

    def track_pitch(self, vocals: Path, ctx: StageContext) -> PitchTrack:
        from swift_f0 import SwiftF0

        audio = audio_io.load(vocals, ALIGN_SAMPLE_RATE)
        # swift-f0 >= 0.3: 16 ms frames; confidence is calibrated so a frame is voiced at >= 0.5,
        # which assemble.VOICED_CONFIDENCE applies per syllable. Range covers bass to soprano.
        result = SwiftF0().detect(audio, ALIGN_SAMPLE_RATE, fmin=65.0, fmax=1400.0)
        ctx.progress(1.0)
        return PitchTrack(
            times_ms=[float(t) * 1000 for t in result.timestamps],
            hz=[float(h) for h in result.pitch_hz],
            confidence=[float(c) for c in result.confidence],
        )

    def estimate_tempo(self, instrumental: Path, ctx: StageContext) -> float:
        import librosa
        import numpy as np

        sr = 22_050
        y = audio_io.load(instrumental, sr)
        tempo, _ = librosa.beat.beat_track(y=y, sr=sr)
        ctx.progress(1.0)
        value = float(np.atleast_1d(tempo)[0])
        return value if value > 0 else 120.0

