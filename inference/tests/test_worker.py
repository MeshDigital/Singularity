"""Runs the real worker process (fake backend) and talks the protocol to it over pipes."""

import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

from singularity_inference import schemas as s


class WorkerProcess:
    def __init__(self, tmp_path: Path, **env: str):
        self.proc = subprocess.Popen(
            [sys.executable, "-m", "singularity_inference", "--backend", "fake"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env={**os.environ, "SINGULARITY_MODEL_DIR": str(tmp_path / "models"), **env},
            cwd=Path(__file__).resolve().parents[1],
        )

    def send(self, command: s.Contract) -> None:
        self.proc.stdin.write((command.to_json() + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def next_event(self) -> dict:
        line = self.proc.stdout.readline()
        assert line, f"worker exited: {self.proc.stderr.read().decode(errors='replace')}"
        return json.loads(line)

    def until(self, name: str) -> list[dict]:
        events = []
        while True:
            e = self.next_event()
            events.append(e)
            if e["event"] == name:
                return events

    def close(self) -> int:
        try:
            return self.proc.wait(timeout=10)
        finally:
            if self.proc.poll() is None:
                self.proc.kill()


def process(tmp_path: Path, task_id: str = "t1", **kw) -> s.ProcessTrackCommand:
    audio = tmp_path / "song.mp3"
    audio.write_bytes(b"x")
    return s.ProcessTrackCommand(task_id=task_id, audio_path=str(audio), output_folder=str(tmp_path / task_id),
                                 lyrics="[00:01.00] Ça va très bien\n[00:03.00] fantasy",
                                 lyrics_kind=s.LyricsKind.SYNCED, language="en", **kw)


def test_ready_then_task_then_shutdown(tmp_path):
    w = WorkerProcess(tmp_path)
    ready = w.next_event()
    assert ready["event"] == "ready" and ready["protocolVersion"] == s.PROTOCOL_VERSION
    assert "missingModels" not in ready  # the fake backend needs none

    w.send(process(tmp_path))
    events = w.until("task_finished")
    done = s.decode_event(json.dumps(events[-1]))
    assert done.outcome is s.TaskOutcome.SUCCEEDED
    assert done.result.lines[0].syllables[0].text == "Ça"  # non-ASCII survives both pipes
    for e in events:
        s.decode_event(json.dumps(e))  # every event the worker wrote is valid protocol

    w.send(s.ShutdownCommand())
    assert w.close() == 0


def test_cancel_running_task_then_keep_working(tmp_path):
    w = WorkerProcess(tmp_path, SINGULARITY_FAKE_STAGE_DELAY_MS="2000")
    w.next_event()
    w.send(process(tmp_path, "slow"))
    w.until("stage_started")
    w.send(s.CancelCommand(task_id="slow"))
    started = time.monotonic()
    done = w.until("task_finished")[-1]
    assert done["outcome"] == "cancelled"
    assert time.monotonic() - started < 1.5

    w.send(s.ShutdownCommand())
    assert w.close() == 0


def test_failure_is_reported_and_worker_survives(tmp_path):
    w = WorkerProcess(tmp_path)
    w.next_event()
    w.send(s.ProcessTrackCommand(task_id="bad", audio_path=str(tmp_path / "missing.mp3"), output_folder=str(tmp_path)))
    done = w.until("task_finished")[-1]
    assert done["outcome"] == "failed" and "FileNotFoundError" in done["error"]

    w.send(process(tmp_path, "good"))
    assert w.until("task_finished")[-1]["outcome"] == "succeeded"
    w.send(s.ShutdownCommand())
    assert w.close() == 0


def test_malformed_command_is_logged_not_fatal(tmp_path):
    w = WorkerProcess(tmp_path)
    w.next_event()
    w.proc.stdin.write(b'{"command":"format_disk"}\n')
    w.proc.stdin.flush()
    log = w.next_event()
    assert log["event"] == "log" and log["level"] == "error"
    w.send(s.ShutdownCommand())
    assert w.close() == 0


def test_closing_stdin_exits_cleanly(tmp_path):
    w = WorkerProcess(tmp_path, SINGULARITY_FAKE_STAGE_DELAY_MS="5000")
    w.next_event()
    w.send(process(tmp_path, "orphan"))
    w.until("stage_started")
    w.proc.stdin.close()  # the app died
    events = w.until("task_finished")
    assert events[-1]["outcome"] == "cancelled"
    assert w.close() == 0


def test_loading_a_dll_mid_task_does_not_deadlock_with_the_stdin_reader(tmp_path):
    # Regression: the first real run hung importing scipy (OpenBLAS) in the tempo stage, because
    # OpenBLAS's DllMain waits on the C runtime lock that a CRT read() of stdin holds while blocked.
    pytest.importorskip("scipy")
    w = WorkerProcess(tmp_path, SINGULARITY_FAKE_IMPORT="scipy.linalg")
    w.next_event()
    w.send(process(tmp_path, "dll"))

    result = {}
    reader = threading.Thread(target=lambda: result.update(done=w.until("task_finished")[-1]), daemon=True)
    reader.start()
    reader.join(timeout=60)
    if reader.is_alive():
        w.proc.kill()
        pytest.fail("worker deadlocked loading scipy while the stdin reader was blocked")
    assert result["done"]["outcome"] == "succeeded"
    w.send(s.ShutdownCommand())
    assert w.close() == 0
