"""Parses the fixtures shared with the C# tests (Singularity.Contracts/Fixtures)."""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from singularity_inference import schemas as s

FIXTURES = Path(__file__).resolve().parents[2] / "Singularity.Contracts" / "Fixtures"


def drop_nulls(value):
    """Both encoders omit None/null fields, so a fixture's explicit nulls don't survive a round trip."""
    if isinstance(value, dict):
        return {k: drop_nulls(v) for k, v in value.items() if v is not None}
    if isinstance(value, list):
        return [drop_nulls(v) for v in value]
    return value


def session_lines() -> list[str]:
    return [line for line in (FIXTURES / "worker-session.jsonl").read_text(encoding="utf-8").splitlines() if line]


def test_metadata_fixture_parses_and_round_trips():
    raw = (FIXTURES / "metadata.example.json").read_text(encoding="utf-8")
    m = s.SongPackageMetadata.model_validate_json(raw)

    assert m.quality.tier is s.QualityTier.A_PLUS
    assert m.timing.video_gap_ms == -340
    assert m.fingerprint and m.fingerprint.music_brainz_recording_id == "b1a9c0e9-d987-4042-ae91-78d6a3267d69"
    assert m.provenance.processed_at_utc.utcoffset().total_seconds() == 0

    assert json.loads(m.to_json()) == json.loads(raw)


def test_metadata_rejects_newer_schema():
    raw = json.loads((FIXTURES / "metadata.example.json").read_text(encoding="utf-8"))
    raw["schemaVersion"] = s.METADATA_SCHEMA_VERSION + 1
    with pytest.raises(ValidationError):
        s.SongPackageMetadata.model_validate(raw)


def test_session_fixture_decodes():
    commands, events, unknown = [], [], []
    for line in session_lines():
        obj = json.loads(line)
        if "command" in obj:
            commands.append(s.decode_command(line))
        else:
            try:
                events.append(s.decode_event(line))
            except ValidationError:
                unknown.append(obj["event"])

    assert [type(c) for c in commands] == [s.ProcessTrackCommand, s.CancelCommand, s.ShutdownCommand]
    assert "\n" in commands[0].lyrics
    assert unknown == ["gpu_stats"]
    assert [e.event for e in events] == [
        "ready", "stage_started", "progress_update", "stage_completed", "log", "task_finished", "task_finished",
    ]
    assert events[0].protocol_version == s.PROTOCOL_VERSION
    done = events[5]
    assert done.outcome is s.TaskOutcome.SUCCEEDED
    assert done.result.lines[0].syllables[2].midi_tone is None
    assert events[6].result is None


def test_encoding_matches_csharp_wire_format():
    # The exact strings the C# WorkerProtocolTests expect from its own encoder.
    assert s.CancelCommand(task_id="t").to_json() == '{"command":"cancel","taskId":"t"}'
    assert s.ShutdownCommand().to_json() == '{"command":"shutdown"}'
    line = s.ProgressUpdateEvent(task_id="t", stage=s.PipelineStage.ALIGNMENT, progress=0.25).to_json()
    assert json.loads(line) == {"event": "progress_update", "taskId": "t", "stage": "alignment", "progress": 0.25}


def test_every_fixture_line_round_trips():
    for line in session_lines():
        obj = json.loads(line)
        if obj.get("event") == "gpu_stats":
            continue
        model = s.decode_command(line) if "command" in obj else s.decode_event(line)
        assert json.loads(model.to_json()) == drop_nulls(obj)


@pytest.mark.parametrize(
    "line",
    [
        '{"command":"format_disk"}',
        '{"command":"cancel"}',
        "not json",
    ],
)
def test_bad_commands_are_rejected(line):
    with pytest.raises(ValidationError):
        s.decode_command(line)


def test_out_of_range_progress_is_rejected():
    with pytest.raises(ValidationError):
        s.ProgressUpdateEvent(task_id="t", stage=s.PipelineStage.PITCH, progress=1.5)
