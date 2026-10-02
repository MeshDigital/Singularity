using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;

namespace Singularity.Services.Inference;

/// <summary>The worker exited, never became ready, or speaks another protocol version.</summary>
public sealed class InferenceWorkerException(string message, string? stderrTail = null, Exception? inner = null)
    : Exception(stderrTail is { Length: > 0 } ? $"{message}{System.Environment.NewLine}{stderrTail}" : message, inner)
{
    public string? StderrTail { get; } = stderrTail;
}

/// <summary>The worker ran the task and reported a failure (missing model, unreadable audio, …).</summary>
public sealed class InferenceTaskFailedException(string taskId, string error)
    : Exception($"Inference task {taskId} failed: {error}")
{
    public string TaskId { get; } = taskId;
    public string Error { get; } = error;
}

/// <summary>
/// Owns the Python inference worker process: starts it lazily, runs one task at a time over the
/// JSONL protocol, and makes sure it dies. A cancelled task gets <see cref="InferenceWorkerOptions.CancelGracePeriod"/>
/// to stop cooperatively; after that the whole process tree is killed (a CUDA call can't be interrupted)
/// and the next task starts a fresh worker. The process also sits in a kill-on-close job object, so
/// it goes down with the app even if the app crashes.
/// </summary>
public sealed class InferenceWorkerHost : IAsyncDisposable
{
    private const int StderrTailLines = 40;

    private readonly InferenceWorkerOptions _options;
    private readonly ILogger<InferenceWorkerHost> _logger;
    private readonly SemaphoreSlim _taskGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateLock = new();

    private WorkerProcess? _worker;
    private RunningTask? _current;
    private bool _disposed;

    public InferenceWorkerHost(InferenceWorkerOptions options, ILogger<InferenceWorkerHost> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>What the running worker reported at startup; null when no worker is running.</summary>
    public ReadyEvent? Ready => _worker?.Ready.Task.IsCompletedSuccessfully == true ? _worker.Ready.Task.Result : null;

    /// <summary>Process id of the running worker, for diagnostics and tests.</summary>
    public int? WorkerProcessId => _worker?.Process.Id;

    internal bool WorkerIsInKillOnCloseJob => _worker is { Job: { } job } w && job.Contains(w.Process);

    /// <summary>Starts the worker if it isn't running and waits for its ready event.</summary>
    public async Task<ReadyEvent> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var worker = _worker;
            if (worker is null || worker.Process.HasExited)
            {
                worker = Launch();
                _worker = worker;
            }

            ReadyEvent ready;
            try
            {
                ready = await worker.Ready.Task.WaitAsync(_options.ReadyTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await KillAsync(worker, "it did not become ready in time").ConfigureAwait(false);
                throw new InferenceWorkerException($"Inference worker did not become ready within {_options.ReadyTimeout}.", worker.StderrTail());
            }

            if (ready.ProtocolVersion != WorkerProtocol.Version)
            {
                await KillAsync(worker, "protocol version mismatch").ConfigureAwait(false);
                throw new InferenceWorkerException(
                    $"Inference worker speaks protocol {ready.ProtocolVersion}, this app speaks {WorkerProtocol.Version}. Reinstall the worker.");
            }
            return ready;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Runs one track through the worker. Tasks are queued: one runs at a time.</summary>
    /// <param name="progress">Receives stage, progress and log events for this task.</param>
    /// <exception cref="InferenceTaskFailedException">The worker reported a failure.</exception>
    /// <exception cref="InferenceWorkerException">The worker crashed or couldn't start.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<TrackAnalysisResult> ProcessTrackAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _taskGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartAsync(ct).ConfigureAwait(false);
            var worker = _worker!;
            var task = new RunningTask(command.TaskId, progress);
            lock (_stateLock) _current = task;

            await SendAsync(worker, command).ConfigureAwait(false);
            TaskFinishedEvent finished;
            await using (ct.Register(() => _ = CancelAsync(worker, task)))
            {
                finished = await task.Finished.Task.ConfigureAwait(false);
            }

            return finished.Outcome switch
            {
                TaskOutcome.Succeeded when finished.Result is not null => finished.Result,
                TaskOutcome.Cancelled => throw new OperationCanceledException($"Inference task {command.TaskId} was cancelled.", ct),
                _ => throw new InferenceTaskFailedException(command.TaskId, finished.Error ?? "no error message"),
            };
        }
        finally
        {
            lock (_stateLock) _current = null;
            _taskGate.Release();
        }
    }

    private async Task CancelAsync(WorkerProcess worker, RunningTask task)
    {
        try
        {
            await SendAsync(worker, new CancelCommand(task.TaskId)).ConfigureAwait(false);
            var stopped = await Task.WhenAny(task.Finished.Task, Task.Delay(_options.CancelGracePeriod)).ConfigureAwait(false);
            if (stopped == task.Finished.Task) return;

            // Kill before reporting, so the caller never starts the next GPU job while this one still holds VRAM.
            task.KillRequested = true; // the resulting exit then reads as a cancellation, not a crash
            await KillAsync(worker, $"task {task.TaskId} did not stop within {_options.CancelGracePeriod}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling inference task {TaskId} failed; killing the worker", task.TaskId);
            task.KillRequested = true;
            await KillAsync(worker, "cancel could not be delivered").ConfigureAwait(false);
        }
        task.Finished.TrySetResult(new TaskFinishedEvent(task.TaskId, TaskOutcome.Cancelled));
    }

    private async Task SendAsync(WorkerProcess worker, WorkerCommand command)
    {
        var line = WorkerProtocol.Encode(command);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await worker.Process.StandardInput.WriteAsync(line + "\n").ConfigureAwait(false);
            await worker.Process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InferenceWorkerException("Inference worker is not accepting commands (it has exited).", worker.StderrTail(), ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private WorkerProcess Launch()
    {
        var psi = new ProcessStartInfo(_options.PythonExecutable)
        {
            WorkingDirectory = _options.WorkerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("singularity_inference");
        if (_options.Backend is { Length: > 0 } backend)
        {
            psi.ArgumentList.Add("--backend");
            psi.ArgumentList.Add(backend);
        }
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["SINGULARITY_MODEL_DIR"] = _options.ModelDirectory;
        if (_options.FfmpegPath is { Length: > 0 } ffmpeg) psi.Environment["SINGULARITY_FFMPEG"] = ffmpeg;
        foreach (var (key, value) in _options.Environment) psi.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InferenceWorkerException("Inference worker process did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InferenceWorkerException($"Cannot start the inference worker '{_options.PythonExecutable}': {ex.Message}", inner: ex);
        }

        var job = WindowsJobObject.TryCreateKillOnClose();
        if (job is not null && !job.TryAssign(process))
        {
            job.Dispose();
            job = null;
        }
        if (job is null) _logger.LogWarning("Inference worker {Pid} is not in a kill-on-close job; it may outlive a crash", process.Id);

        var worker = new WorkerProcess(process, job);
        worker.StdoutPump = Task.Run(() => PumpStdoutAsync(worker));
        worker.StderrPump = Task.Run(() => PumpStderrAsync(worker));
        _logger.LogInformation("Started inference worker {Pid} ({Python}, backend {Backend})",
            process.Id, _options.PythonExecutable, _options.Backend ?? "ml");
        return worker;
    }

    private async Task PumpStdoutAsync(WorkerProcess worker)
    {
        var reader = worker.Process.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                WorkerEvent? evt;
                try
                {
                    evt = WorkerProtocol.DecodeEvent(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Inference worker wrote a non-protocol line ({Error}): {Line}", ex.Message, Truncate(line));
                    continue;
                }
                if (evt is not null) Dispatch(worker, evt);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Pipe closed under us: handled as an exit below.
        }

        await OnExitedAsync(worker).ConfigureAwait(false);
    }

    private async Task PumpStderrAsync(WorkerProcess worker)
    {
        try
        {
            while (await worker.Process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                worker.AddStderr(line);
                _logger.LogDebug("[inference] {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void Dispatch(WorkerProcess worker, WorkerEvent evt)
    {
        RunningTask? current;
        lock (_stateLock) current = _current;

        switch (evt)
        {
            case ReadyEvent ready:
                if (ready.MissingModels is { Count: > 0 } missing)
                    _logger.LogWarning("Inference worker is missing models: {Models}", string.Join(", ", missing));
                worker.Ready.TrySetResult(ready);
                return;
            case LogEvent log:
                _logger.Log(log.Level switch
                {
                    WorkerLogLevel.Debug => LogLevel.Debug,
                    WorkerLogLevel.Info => LogLevel.Information,
                    WorkerLogLevel.Warning => LogLevel.Warning,
                    _ => LogLevel.Error,
                }, "[inference{Task}] {Message}", log.TaskId is null ? "" : " " + log.TaskId, log.Message);
                if (log.TaskId is not null && log.TaskId == current?.TaskId) current.Report(evt);
                return;
            case UnknownWorkerEvent unknown:
                _logger.LogDebug("Ignoring unknown inference event {Name}", unknown.Name);
                return;
        }

        var taskId = evt switch
        {
            StageStartedEvent e => e.TaskId,
            ProgressUpdateEvent e => e.TaskId,
            StageCompletedEvent e => e.TaskId,
            TaskFinishedEvent e => e.TaskId,
            _ => null,
        };
        if (current is null || taskId != current.TaskId)
        {
            _logger.LogDebug("Ignoring inference event for task {TaskId} (not the running task)", taskId);
            return;
        }

        if (evt is TaskFinishedEvent finished) current.Finished.TrySetResult(finished);
        else current.Report(evt);
    }

    private async Task OnExitedAsync(WorkerProcess worker)
    {
        try
        {
            await worker.Process.WaitForExitAsync().ConfigureAwait(false);
            if (worker.StderrPump is { } pump) await pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Waiting for the inference worker to exit failed");
        }

        int? code = null;
        try { code = worker.Process.ExitCode; } catch (InvalidOperationException) { }

        var error = new InferenceWorkerException($"Inference worker exited (code {code?.ToString() ?? "unknown"}).", worker.StderrTail());
        worker.Ready.TrySetException(error);

        RunningTask? current;
        lock (_stateLock) current = _current;
        if (current is { KillRequested: false } && ReferenceEquals(_worker, worker)) current.Finished.TrySetException(error);

        if (code is not 0) _logger.LogWarning("Inference worker {Pid} exited with code {Code}", worker.Process.Id, code);
        worker.Job?.Dispose();
    }

    private async Task KillAsync(WorkerProcess worker, string reason)
    {
        _logger.LogWarning("Killing inference worker {Pid}: {Reason}", worker.Process.Id, reason);
        try
        {
            worker.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Killing the inference worker tree failed; closing its job object instead");
        }
        worker.Job?.Dispose(); // KILL_ON_JOB_CLOSE takes anything Kill missed
        try
        {
            await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogError("Inference worker {Pid} did not exit after being killed", worker.Process.Id);
        }
    }

    /// <summary>Asks the worker to shut down, and kills it if it doesn't within <see cref="InferenceWorkerOptions.ShutdownTimeout"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var worker = _worker;
        _worker = null;
        if (worker is null) return;

        if (!worker.Process.HasExited)
        {
            try
            {
                await SendAsync(worker, new ShutdownCommand()).ConfigureAwait(false);
                worker.Process.StandardInput.Close();
                await worker.Process.WaitForExitAsync().WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InferenceWorkerException or IOException)
            {
                await KillAsync(worker, "it did not shut down in time").ConfigureAwait(false);
            }
        }

        if (worker.StdoutPump is { } pump)
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        worker.Job?.Dispose();
        worker.Process.Dispose();
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed class WorkerProcess(Process process, WindowsJobObject? job)
    {
        private readonly Queue<string> _stderr = new();

        public Process Process { get; } = process;
        public WindowsJobObject? Job { get; } = job;
        public TaskCompletionSource<ReadyEvent> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StdoutPump { get; set; }
        public Task? StderrPump { get; set; }

        public void AddStderr(string line)
        {
            lock (_stderr)
            {
                _stderr.Enqueue(line);
                while (_stderr.Count > StderrTailLines) _stderr.Dequeue();
            }
        }

        public string StderrTail()
        {
            lock (_stderr) return string.Join(System.Environment.NewLine, _stderr);
        }
    }

    private sealed class RunningTask(string taskId, IProgress<WorkerEvent>? progress)
    {
        public string TaskId { get; } = taskId;
        public TaskCompletionSource<TaskFinishedEvent> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool KillRequested;
        public void Report(WorkerEvent evt) => progress?.Report(evt);
    }
}
