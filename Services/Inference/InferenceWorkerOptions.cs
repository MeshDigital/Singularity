using System;
using System.Collections.Generic;
using System.IO;
using Singularity.Services.AudioAnalysis;

namespace Singularity.Services.Inference;

/// <summary>How to launch the Python inference worker (inference/ in the repo).</summary>
public sealed record InferenceWorkerOptions
{
    public const string PythonEnvironmentVariable = "SINGULARITY_INFERENCE_PYTHON";
    public const string WorkerDirectoryEnvironmentVariable = "SINGULARITY_INFERENCE_DIR";

    /// <summary>The interpreter that has singularity_inference installed, normally inference\.venv\Scripts\python.exe.</summary>
    public required string PythonExecutable { get; init; }

    /// <summary>The inference/ folder; the worker's working directory.</summary>
    public required string WorkerDirectory { get; init; }

    /// <summary>Where model weights are downloaded (several GB, so local rather than roaming app data).</summary>
    public string ModelDirectory { get; init; } = DefaultModelDirectory;

    /// <summary>"ml" (default) or "fake", the deterministic test backend.</summary>
    public string? Backend { get; init; }

    public string? FfmpegPath { get; init; }

    /// <summary>Startup includes importing torch, which takes a while on a cold disk.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a cancelled task may take to stop before the worker's process tree is killed.</summary>
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra environment variables for the worker (tests use these for the fake backend's knobs).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public static string DefaultModelDirectory => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Singularity", "models");

    /// <summary>
    /// Finds the worker. <see cref="PythonEnvironmentVariable"/> (plus optionally
    /// <see cref="WorkerDirectoryEnvironmentVariable"/>) wins; otherwise an inference\ folder with a
    /// .venv is looked for next to the app and in its parent folders, which finds the repo's copy in a
    /// development checkout. Null when there is none.
    /// </summary>
    /// <summary>The worker folder chosen in Settings (Karaoke:InferenceFolder); null when none was chosen.</summary>
    public static string? ChosenDirectory { get; set; }

    /// <summary>Where a worker is found without configuration: %LOCALAPPDATA%\Singularity\inference.</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Singularity", "inference");

    public static InferenceWorkerOptions? Discover(string? startDirectory = null)
    {
        var ffmpeg = AudioIngestionPipeline.ResolveFfmpegPath();
        string? Ffmpeg() => File.Exists(ffmpeg) ? ffmpeg : null;

        if (System.Environment.GetEnvironmentVariable(PythonEnvironmentVariable) is { Length: > 0 } python && File.Exists(python))
        {
            var dir = System.Environment.GetEnvironmentVariable(WorkerDirectoryEnvironmentVariable) is { Length: > 0 } d
                ? d
                : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(python)))!; // …\inference\.venv\Scripts\python.exe
            return new InferenceWorkerOptions { PythonExecutable = python, WorkerDirectory = dir, FfmpegPath = Ffmpeg() };
        }

        // An installed app doesn't sit next to the source checkout: also try the folder chosen in Settings and
        // %LOCALAPPDATA%\Singularity\inference (a worker set up there is found without any configuration).
        foreach (var workerDir in new[] { ChosenDirectory, DefaultInstallDirectory }.OfType<string>())
        {
            var workerPython = OperatingSystem.IsWindows()
                ? Path.Combine(workerDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(workerDir, ".venv", "bin", "python");
            if (File.Exists(workerPython))
                return new InferenceWorkerOptions { PythonExecutable = workerPython, WorkerDirectory = workerDir, FfmpegPath = Ffmpeg() };
        }

        for (var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var workerDir = Path.Combine(dir.FullName, "inference");
            var venvPython = OperatingSystem.IsWindows()
                ? Path.Combine(workerDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(workerDir, ".venv", "bin", "python");
            if (File.Exists(venvPython))
                return new InferenceWorkerOptions { PythonExecutable = venvPython, WorkerDirectory = workerDir, FfmpegPath = Ffmpeg() };
        }
        return null;
    }
}
