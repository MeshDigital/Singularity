using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Contracts.Inference;
using Singularity.Services.Inference;
using Xunit;

namespace Singularity.Tests.Services.Inference;

/// <summary>Skips when the repo's inference\.venv isn't set up (see inference/README.md).</summary>
public sealed class RequiresInferenceWorkerFactAttribute : FactAttribute
{
    public RequiresInferenceWorkerFactAttribute()
    {
        if (InferenceWorkerOptions.Discover() is null)
            Skip = "inference\\.venv not found; create it as described in inference/README.md";
    }
}

/// <summary>Drives the real Python worker process with its fake (model-free) backend.</summary>
public sealed class InferenceWorkerHostTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("singularity-worker-");

    public void Dispose() => _temp.Delete(recursive: true);

    private InferenceWorkerHost CreateHost(Dictionary<string, string>? env = null, TimeSpan? grace = null) =>
        new(InferenceWorkerOptions.Discover()! with
        {
            Backend = "fake",
            ModelDirectory = Path.Combine(_temp.FullName, "models"),
            Environment = env ?? new Dictionary<string, string>(),
            CancelGracePeriod = grace ?? TimeSpan.FromSeconds(10),
        }, NullLogger<InferenceWorkerHost>.Instance);

    private ProcessTrackCommand Command(string taskId = "t1", string? audio = null)
    {
        var audioPath = audio ?? Path.Combine(_temp.FullName, "audio.flac");
        if (audio is null) File.WriteAllBytes(audioPath, new byte[] { 1 });
        return new ProcessTrackCommand(taskId, audioPath, Path.Combine(_temp.FullName, taskId),
            "[00:01.00] Is this the real life\n[00:04.00] Is this just fantasy", LyricsKind.Synced, "en");
    }

    /// <summary>Reports synchronously on the pump thread (Progress&lt;T&gt; would post asynchronously).</summary>
    private sealed class Recorder(Action<WorkerEvent>? onEvent = null) : IProgress<WorkerEvent>
    {
        public ConcurrentQueue<WorkerEvent> Events { get; } = new();

        public void Report(WorkerEvent value)
        {
            Events.Enqueue(value);
            onEvent?.Invoke(value);
        }
    }

    private static bool HasExited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [RequiresInferenceWorkerFact]
    public async Task Start_ReportsReady_InKillOnCloseJob()
    {
        await using var host = CreateHost();
        var ready = await host.StartAsync();

        Assert.Equal(WorkerProtocol.Version, ready.ProtocolVersion);
        Assert.Equal("cpu", ready.Device);
        Assert.True(host.WorkerIsInKillOnCloseJob);
    }

    [RequiresInferenceWorkerFact]
    public async Task ProcessTrack_ReturnsResult_AndReportsStages()
    {
        await using var host = CreateHost();
        var recorder = new Recorder();

        var result = await host.ProcessTrackAsync(Command(), recorder);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(new[] { "fan", "ta", "sy" }, result.Lines[1].Syllables.Skip(3).Select(s => s.Text));
        Assert.True(File.Exists(result.VocalsPath));
        var stages = recorder.Events.OfType<StageStartedEvent>().Select(e => e.Stage).ToArray();
        Assert.Equal(new[] { PipelineStage.Separation, PipelineStage.Alignment, PipelineStage.Pitch, PipelineStage.Tempo }, stages);
    }

    [RequiresInferenceWorkerFact]
    public async Task WorkerFailure_Throws_AndWorkerKeepsRunning()
    {
        await using var host = CreateHost();
        var ex = await Assert.ThrowsAsync<InferenceTaskFailedException>(() =>
            host.ProcessTrackAsync(Command("bad", Path.Combine(_temp.FullName, "missing.mp3"))));
        Assert.Contains("FileNotFoundError", ex.Error);

        var pid = host.WorkerProcessId;
        await host.ProcessTrackAsync(Command("good"));
        Assert.Equal(pid, host.WorkerProcessId);
    }

    [RequiresInferenceWorkerFact]
    public async Task Cancel_StopsCooperatively_SameWorkerContinues()
    {
        await using var host = CreateHost(new() { ["SINGULARITY_FAKE_STAGE_DELAY_MS"] = "400" });
        await host.StartAsync();
        var pid = host.WorkerProcessId;

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.ProcessTrackAsync(Command("slow"), new Recorder(e => { if (e is StageStartedEvent) cts.Cancel(); }), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {sw.Elapsed}");

        await host.ProcessTrackAsync(Command("next"));
        Assert.Equal(pid, host.WorkerProcessId);
    }

    [RequiresInferenceWorkerFact]
    public async Task Cancel_KillsUncooperativeWorker_NextCallStartsAFreshOne()
    {
        await using var host = CreateHost(
            new() { ["SINGULARITY_FAKE_STAGE_DELAY_MS"] = "30000", ["SINGULARITY_FAKE_IGNORE_CANCEL"] = "1" },
            grace: TimeSpan.FromMilliseconds(300));
        await host.StartAsync();
        var stuckPid = host.WorkerProcessId!.Value;

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.ProcessTrackAsync(Command("stuck"), new Recorder(e => { if (e is StageStartedEvent) cts.Cancel(); }), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"cancel took {sw.Elapsed}");
        Assert.True(HasExited(stuckPid));

        await host.StartAsync();
        Assert.NotEqual(stuckPid, host.WorkerProcessId);
    }

    [RequiresInferenceWorkerFact]
    public async Task WorkerCrash_DuringTask_ThrowsWorkerException()
    {
        await using var host = CreateHost(new() { ["SINGULARITY_FAKE_STAGE_DELAY_MS"] = "30000" });
        await host.StartAsync();
        var pid = host.WorkerProcessId!.Value;

        var ex = await Assert.ThrowsAsync<InferenceWorkerException>(() => host.ProcessTrackAsync(Command("doomed"),
            new Recorder(e => { if (e is StageStartedEvent) Process.GetProcessById(pid).Kill(entireProcessTree: true); })));
        Assert.Contains("exited", ex.Message);
    }

    [RequiresInferenceWorkerFact]
    public async Task Dispose_StopsTheWorker()
    {
        var host = CreateHost();
        await host.StartAsync();
        var pid = host.WorkerProcessId!.Value;

        await host.DisposeAsync();

        Assert.True(HasExited(pid));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
    }

    [Fact]
    public async Task MissingInterpreter_ThrowsWorkerException()
    {
        await using var host = new InferenceWorkerHost(new InferenceWorkerOptions
        {
            PythonExecutable = Path.Combine(_temp.FullName, "no-python.exe"),
            WorkerDirectory = _temp.FullName,
        }, NullLogger<InferenceWorkerHost>.Instance);

        var ex = await Assert.ThrowsAsync<InferenceWorkerException>(() => host.StartAsync());
        Assert.Contains("Cannot start", ex.Message);
    }

    [Fact]
    public void Discover_FindsVenvInParentFolders()
    {
        var python = Path.Combine(_temp.FullName, "inference", ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllBytes(python, Array.Empty<byte>());
        var nested = Directory.CreateDirectory(Path.Combine(_temp.FullName, "bin", "Debug", "net9.0"));

        var options = InferenceWorkerOptions.Discover(nested.FullName);

        if (Environment.GetEnvironmentVariable(InferenceWorkerOptions.PythonEnvironmentVariable) is { Length: > 0 }) return; // overridden on this machine
        Assert.Equal(python, options!.PythonExecutable);
        Assert.Equal(Path.Combine(_temp.FullName, "inference"), options.WorkerDirectory);
    }
}
