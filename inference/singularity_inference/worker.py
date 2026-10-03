"""The worker process: JSONL commands on stdin, JSONL events on stdout (see Singularity.Contracts).

stdout belongs to the protocol. Anything else, such as library prints or progress bars, is
redirected to stderr, which the app only logs. A reader thread handles stdin so that cancel and
shutdown arrive while a task is running. Tasks themselves run one at a time on the main thread.

    python -m singularity_inference [--backend ml|fake]
"""

from __future__ import annotations

import argparse
import io
import os
import queue
import sys
import threading
import traceback
from typing import Iterable

from pydantic import ValidationError

from . import __version__
from . import schemas as s
from .pipeline import Backend, Cancelled, create_backend, plan_stages, run_separation, run_task


class Worker:
    def __init__(self, backend: Backend, stdin: Iterable[str], stdout: io.TextIOBase) -> None:
        self._backend = backend
        self._stdin = stdin
        self._stdout = stdout
        self._write_lock = threading.Lock()
        self._tasks: queue.Queue[s.ProcessTrackCommand | s.SeparateStemsCommand | None] = queue.Queue()
        self._cancelled: set[str] = set()
        self._cancel_lock = threading.Lock()
        self._shutdown = threading.Event()

    def emit(self, message: s.Contract) -> None:
        line = message.to_json()
        with self._write_lock:
            self._stdout.write(line + "\n")
            self._stdout.flush()

    def log(self, level: s.WorkerLogLevel, message: str, task_id: str | None = None) -> None:
        self.emit(s.LogEvent(level=level, message=message, task_id=task_id))

    def _is_cancelled(self, task_id: str) -> bool:
        if self._shutdown.is_set():
            return True
        with self._cancel_lock:
            return task_id in self._cancelled

    def _read_commands(self) -> None:
        try:
            for line in self._stdin:
                if not line.strip():
                    continue
                try:
                    cmd = s.decode_command(line)
                except ValidationError as e:
                    self.log(s.WorkerLogLevel.ERROR, f"ignored malformed command: {e.errors()[0]['msg']}: {line.strip()[:200]}")
                    continue
                if isinstance(cmd, (s.ProcessTrackCommand, s.SeparateStemsCommand)):
                    self._tasks.put(cmd)
                elif isinstance(cmd, s.CancelCommand):
                    with self._cancel_lock:
                        self._cancelled.add(cmd.task_id)
                elif isinstance(cmd, s.ShutdownCommand):
                    break
        finally:
            # Shutdown command or stdin closed (the app went away): abandon everything.
            self._shutdown.set()
            self._tasks.put(None)

    def run(self) -> int:
        missing = self._backend.missing_models(list(s.PipelineStage))
        self.emit(s.ReadyEvent(worker_version=__version__, device=self._backend.device,
                               models=self._backend.model_names(), missing_models=missing or None))
        threading.Thread(target=self._read_commands, name="stdin-reader", daemon=True).start()

        while True:
            cmd = self._tasks.get()
            if cmd is None:
                return 0
            self._run_one(cmd)

    def _run_one(self, cmd: s.ProcessTrackCommand | s.SeparateStemsCommand) -> None:
        if self._is_cancelled(cmd.task_id):
            self.emit(s.TaskFinishedEvent(task_id=cmd.task_id, outcome=s.TaskOutcome.CANCELLED))
            return
        try:
            if isinstance(cmd, s.SeparateStemsCommand):
                run_separation(cmd, self._backend, self.emit, lambda: self._is_cancelled(cmd.task_id))
                self.emit(s.TaskFinishedEvent(task_id=cmd.task_id, outcome=s.TaskOutcome.SUCCEEDED))
                return
            self.log(s.WorkerLogLevel.INFO, f"stages: {', '.join(st.value for st in plan_stages(cmd))}", cmd.task_id)
            result = run_task(cmd, self._backend, self.emit, lambda: self._is_cancelled(cmd.task_id))
            self.emit(s.TaskFinishedEvent(task_id=cmd.task_id, outcome=s.TaskOutcome.SUCCEEDED, result=result))
        except Cancelled:
            self.emit(s.TaskFinishedEvent(task_id=cmd.task_id, outcome=s.TaskOutcome.CANCELLED))
        except Exception as e:  # noqa: BLE001 — any failure ends this task, never the worker
            traceback.print_exc(file=sys.stderr)
            self.emit(s.TaskFinishedEvent(task_id=cmd.task_id, outcome=s.TaskOutcome.FAILED,
                                          error=f"{type(e).__name__}: {e}"))
        finally:
            with self._cancel_lock:
                self._cancelled.discard(cmd.task_id)


def take_stdin() -> io.TextIOBase:
    """Takes the command pipe for the worker and points the process's standard input at NUL.

    On Windows the pipe is a synchronous handle, and Windows serialises every operation on it: while
    the reader thread waits in ReadFile, any other call on that handle waits too. A DLL that brings
    its own C runtime (numpy's and scipy's OpenBLAS, CUDA libraries, ...) queries the standard handles
    while it loads, under the loader lock. With stdin still pointing at the pipe, that query queues
    behind the pending read and the whole process hangs until the app happens to send another line.
    The first real run hung this way in the tempo stage, importing scipy through librosa. Reading
    from a private duplicate and leaving NUL as standard input keeps those queries off the pipe.
    """
    fd = sys.stdin.fileno()
    if os.name == "nt":
        private = os.dup(fd)
        nul = os.open(os.devnull, os.O_RDONLY)
        os.dup2(nul, fd)  # the CRT also re-points STD_INPUT_HANDLE for fd 0
        os.close(nul)
        fd = private
    sys.stdin = open(os.devnull, encoding="utf-8")
    return io.TextIOWrapper(io.FileIO(fd, "rb"), encoding="utf-8-sig")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m singularity_inference")
    parser.add_argument("--backend", choices=["ml", "fake"], default=None)
    args = parser.parse_args(argv)

    protocol_out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n", line_buffering=True)
    sys.stdout = sys.stderr  # stray prints from libraries must not corrupt the protocol stream

    try:
        backend = create_backend(args.backend)
    except Exception as e:  # noqa: BLE001 — report why we can't start, then exit non-zero
        traceback.print_exc(file=sys.stderr)
        protocol_out.write(s.LogEvent(level=s.WorkerLogLevel.ERROR, message=f"worker failed to start: {e}").to_json() + "\n")
        protocol_out.flush()
        return 2

    return Worker(backend, take_stdin(), protocol_out).run()


if __name__ == "__main__":
    sys.exit(main())
