import math
from pathlib import Path

import pytest

from singularity_inference import schemas as s
from singularity_inference.assemble import PitchTrack, build_lines, hz_to_midi, syllable_pitch
from singularity_inference.backends.fake import FakeBackend
from singularity_inference.lyrics import AlignedWord
from singularity_inference.models import model_dir, missing_models, registry
from singularity_inference.pipeline import Cancelled, plan_stages, run_task, whisper_prompt
from singularity_inference.lyrics import LyricLineText

STAGES = s.PipelineStage


def cmd(tmp_path: Path, **kw) -> s.ProcessTrackCommand:
    audio = tmp_path / "audio.flac"
    audio.write_bytes(b"x")
    return s.ProcessTrackCommand(task_id="t", audio_path=str(audio), output_folder=str(tmp_path / "out"), **kw)


def test_plan_skips_whisper_only_for_synced_lyrics(tmp_path):
    synced = cmd(tmp_path, lyrics="[00:01.00] hi", lyrics_kind=s.LyricsKind.SYNCED)
    plain = cmd(tmp_path, lyrics="hi")
    none = cmd(tmp_path)
    assert STAGES.TRANSCRIPTION not in plan_stages(synced)
    assert STAGES.TRANSCRIPTION in plan_stages(plain)
    assert STAGES.TRANSCRIPTION in plan_stages(none)
    assert plan_stages(synced)[0] is STAGES.SEPARATION


def run(c, backend=None, cancelled=lambda: False):
    events = []
    result = run_task(c, backend or FakeBackend(), events.append, cancelled)
    return result, events


def test_synced_run_emits_stages_in_order_and_builds_lines(tmp_path):
    result, events = run(cmd(tmp_path, lyrics="[00:01.00] Is this the real life\n[00:04.00] fantasy",
                             lyrics_kind=s.LyricsKind.SYNCED, language="en"))
    started = [e.stage for e in events if isinstance(e, s.StageStartedEvent)]
    completed = [e.stage for e in events if isinstance(e, s.StageCompletedEvent)]
    assert started == completed == [STAGES.SEPARATION, STAGES.ALIGNMENT, STAGES.PITCH, STAGES.TEMPO]

    assert len(result.lines) == 2
    assert [x.text for x in result.lines[1].syllables] == ["fan", "ta", "sy"]
    assert [x.starts_word for x in result.lines[1].syllables] == [True, False, False]
    assert result.language == "en"
    assert Path(result.vocals_path).is_file()
    assert all(x.midi_tone is not None for line in result.lines for x in line.syllables)


def test_plain_lyrics_go_through_transcription(tmp_path):
    result, events = run(cmd(tmp_path, lyrics="la la\nna na"))
    assert STAGES.TRANSCRIPTION in [e.stage for e in events if isinstance(e, s.StageStartedEvent)]
    assert [[x.text for x in l.syllables] for l in result.lines] == [["la", "la"], ["na", "na"]]
    assert result.language == "en"  # detected by the (fake) transcription


def test_no_lyrics_uses_transcript_segments_as_lines(tmp_path):
    result, _ = run(cmd(tmp_path))
    assert len(result.lines) == 2


def test_existing_stems_are_reused(tmp_path):
    c = cmd(tmp_path, lyrics="[00:01.00] a", lyrics_kind=s.LyricsKind.SYNCED)
    out = Path(c.output_folder)
    out.mkdir()
    (out / "vocals.wav").write_bytes(b"mine")
    (out / "instrumental.wav").write_bytes(b"mine")
    _, events = run(c)
    assert (out / "vocals.wav").read_bytes() == b"mine"
    assert any(isinstance(e, s.ProgressUpdateEvent) and e.message == "reusing existing stems" for e in events)


def test_missing_audio_fails_before_any_stage(tmp_path):
    c = s.ProcessTrackCommand(task_id="t", audio_path=str(tmp_path / "nope.mp3"), output_folder=str(tmp_path))
    with pytest.raises(FileNotFoundError):
        run(c)


def test_missing_models_fail_with_fetch_hint(tmp_path):
    class NoModels(FakeBackend):
        def missing_models(self, stages):
            return ["htdemucs_ft"]

    with pytest.raises(RuntimeError, match="models fetch"):
        run(cmd(tmp_path), NoModels())


def test_cancellation_stops_between_stages(tmp_path):
    calls = {"n": 0}

    def cancelled():
        calls["n"] += 1
        return calls["n"] > 3

    with pytest.raises(Cancelled):
        run(cmd(tmp_path, lyrics="[00:01.00] a", lyrics_kind=s.LyricsKind.SYNCED), cancelled=cancelled)


def test_gpu_memory_is_released_after_every_stage(tmp_path, monkeypatch):
    released = []
    monkeypatch.setattr("singularity_inference.pipeline.release_gpu_memory", lambda: released.append(1))
    run(cmd(tmp_path, lyrics="[00:01.00] a", lyrics_kind=s.LyricsKind.SYNCED))
    assert len(released) == 4


def test_whisper_prompt_is_bounded():
    lines = [LyricLineText("word " * 50)] * 10
    assert len(whisper_prompt(lines)) == 800


def test_syllable_pitch_uses_median_of_voiced_frames():
    a4, a5 = 440.0, 880.0
    track = PitchTrack(times_ms=[0, 10, 20, 30, 40], hz=[a4, a4, a5, a4, 0.0], confidence=[0.9, 0.9, 0.9, 0.9, 0.1])
    tone, conf = syllable_pitch(track, 0, 50)
    assert tone == 69  # one octave-jumped frame doesn't move it
    assert conf == pytest.approx(0.74)
    assert syllable_pitch(track, 100, 200) == (None, 0.0)
    assert round(hz_to_midi(261.63)) == 60


def test_unvoiced_syllable_has_no_tone():
    track = PitchTrack([0, 10], [0.0, 0.0], [0.2, 0.2])
    lines = build_lines([[AlignedWord("hm", 0, 20, 0.7)]], track, None)
    syl = lines[0].syllables[0]
    assert syl.midi_tone is None and syl.alignment_confidence == 0.7


def test_model_registry_and_dir(monkeypatch, tmp_path):
    monkeypatch.setenv("SINGULARITY_MODEL_DIR", str(tmp_path))
    assert model_dir() == tmp_path
    assert {spec.role for spec in registry().values()} == {"separation", "transcription", "alignment", "pitch"}
    assert set(missing_models()) == {"htdemucs_ft", "large-v3", "mms_fa"}  # swift_f0 ships in its package

    (tmp_path / ".fetched").mkdir()
    (tmp_path / ".fetched" / "mms_fa").write_text("x")
    assert "mms_fa" not in missing_models()


def test_default_model_dir_is_local_appdata(monkeypatch, tmp_path):
    monkeypatch.delenv("SINGULARITY_MODEL_DIR", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    assert model_dir() == tmp_path / "Singularity" / "models"


class ShiftedVocals(FakeBackend):
    """Vocals sung 15 s later than the LRC says, as with a longer album intro."""

    def vocal_activity(self, vocals):
        return [10 * 16 <= i < 10 * 19 or 10 * 25 <= i < 10 * 28 for i in range(600)]


def test_lrc_is_shifted_to_match_the_vocals(tmp_path):
    lrc = "[00:01.00] Is this the real life\n[00:04.00]\n[00:10.00] fantasy\n[00:13.00]"
    result, events = run(cmd(tmp_path, lyrics=lrc, lyrics_kind=s.LyricsKind.SYNCED, language="en"), ShiftedVocals())
    assert result.lines[0].syllables[0].start_ms >= 15_000 - 300  # window padding
    assert any(isinstance(e, s.LogEvent) and "+15000 ms" in e.message for e in events)
    assert STAGES.TRANSCRIPTION not in [e.stage for e in events if isinstance(e, s.StageStartedEvent)]


class SilentVocals(FakeBackend):
    def vocal_activity(self, vocals):
        return [i % 50 == 0 for i in range(600)]  # nothing lines up with the LRC


def test_lrc_that_fits_nowhere_falls_back_to_transcription(tmp_path):
    lrc = "[00:01.00] la la\n[00:04.00] na na"
    result, events = run(cmd(tmp_path, lyrics=lrc, lyrics_kind=s.LyricsKind.SYNCED, language="en"), SilentVocals())
    assert STAGES.TRANSCRIPTION in [e.stage for e in events if isinstance(e, s.StageStartedEvent)]
    assert [[x.text for x in l.syllables] for l in result.lines] == [["la", "la"], ["na", "na"]]


def test_octave_errors_are_folded_back_without_changing_note_names():
    from singularity_inference.assemble import smooth_octaves

    tones = [57, 59, 71, 57, None, 45, 60]  # 71 and 45 are octave errors around ~58
    assert smooth_octaves(tones) == [57, 59, 59, 57, None, 57, 60]
    assert all(a is None or (a - b) % 12 == 0 for a, b in zip(smooth_octaves(tones), tones) if b is not None)


def test_sustained_vowel_extends_the_note_up_to_the_next_syllable():
    track = PitchTrack(times_ms=[float(t) for t in range(0, 2000, 10)], hz=[220.0] * 200, confidence=[0.9] * 200)
    track.hz[150:] = [0.0] * 50  # silence from 1500 ms
    words = [[AlignedWord("one", 0, 200, 0.9), AlignedWord("two", 1000, 1100, 0.9)]]
    a, b = build_lines(words, track, None)[0].syllables
    assert a.end_ms == 1000 - 20      # held until just before "two"
    assert b.end_ms == 1490           # held until the voice stops


def _track(notes_ms: list[tuple[int, int, float]], step: int = 16) -> PitchTrack:
    """A track that sings the given (start, end, midi) notes."""
    end = max(e for _, e, _ in notes_ms)
    times = [float(t) for t in range(0, end, step)]
    hz = [next((440.0 * 2 ** ((m - 69) / 12) for a, b, m in notes_ms if a <= t < b), 0.0) for t in times]
    return PitchTrack(times, hz, [0.9 if h > 0 else 0.1 for h in hz])


def test_melisma_is_split_into_its_notes():
    from singularity_inference.assemble import pitch_segments

    track = _track([(0, 300, 64), (300, 600, 62), (600, 1000, 60)])
    assert pitch_segments(track, 0, 1000, 62) == [(0, 304, 64), (304, 608, 62), (608, 1000, 60)]


def test_single_note_and_short_wobbles_are_not_split():
    from singularity_inference.assemble import pitch_segments

    assert pitch_segments(_track([(0, 800, 60)]), 0, 800, 60) is None
    # A 64 ms scoop up into the note is part of it, not a note of its own.
    assert pitch_segments(_track([(0, 64, 58), (64, 800, 60)]), 0, 800, 60) is None


def test_segments_follow_the_syllable_octave():
    from singularity_inference.assemble import pitch_segments

    segs = pitch_segments(_track([(0, 400, 76), (400, 800, 74)]), 0, 800, 62)  # tracked an octave high
    assert [t for _, _, t in segs] == [64, 62]


def test_build_lines_attaches_segments():
    track = _track([(0, 400, 64), (400, 800, 60)])
    syl = build_lines([[AlignedWord("oh", 0, 800, 0.9)]], track, None)[0].syllables[0]
    assert [(x.start_ms, x.end_ms, x.midi_tone) for x in syl.segments] == [(0, 400, 64), (400, 800, 60)]


def test_vibrato_across_a_semitone_boundary_stays_one_note():
    from singularity_inference.assemble import pitch_segments
    import math

    # 6 Hz vibrato of +-0.6 semitone around 60.4: crosses the 60/61 boundary every cycle.
    times = [float(t) for t in range(0, 1200, 16)]
    midi = [60.4 + 0.6 * math.sin(2 * math.pi * 6 * t / 1000) for t in times]
    track = PitchTrack(times, [440.0 * 2 ** ((m - 69) / 12) for m in midi], [0.9] * len(times))
    assert pitch_segments(track, 0, 1200, 60) is None
