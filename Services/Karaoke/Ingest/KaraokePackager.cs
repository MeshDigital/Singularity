using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Contracts.Inference;
using Singularity.Contracts.Quality;
using Singularity.Contracts.Song;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Sync;

namespace Singularity.Services.Karaoke.Ingest;

/// <summary>A downloaded recording to turn into a song, with what's known about it.</summary>
/// <param name="AudioPath">The master audio. Copied, never moved or changed.</param>
/// <param name="TrackId">Stable identity, e.g. "spotify:track:…"; re-importing the same id replaces the earlier package.</param>
/// <param name="ExpectedDurationMs">The catalogue's length of the recording, to check the download against.</param>
/// <param name="CoverUrl">Album art to download as the cover.</param>
/// <param name="VideoPath">A music video to sync to the master; dropped when its arrangement differs.</param>
/// <param name="Language">ISO code if known; otherwise the worker detects it.</param>
public sealed record IngestSource(
    string AudioPath,
    string Artist,
    string Title,
    string? Album = null,
    string? TrackId = null,
    string? Isrc = null,
    int? ExpectedDurationMs = null,
    string? CoverUrl = null,
    string? VideoPath = null,
    string? Language = null,
    int? Year = null);

public enum IngestStage { Preparing, FetchingLyrics, GeneratingChart, SyncingVideo, Finishing }

/// <param name="Fraction">Progress within the stage, 0..1.</param>
public sealed record IngestProgress(IngestStage Stage, double Fraction);

/// <param name="Notes">What was left out and why (no lyrics found, video dropped, …), for the queue's detail line.</param>
public sealed record IngestResult(string PackageFolder, QualityAssessment Quality, bool HasVideo, IReadOnlyList<string> Notes);

/// <summary>Finds lyrics for a recording; the LRCLIB client in the app.</summary>
public interface ILyricsLookup
{
    Task<(string Text, LyricsKind Kind)?> FindAsync(string artist, string title, string? album, int durationMs, CancellationToken ct);
}

/// <summary>Separation, alignment and pitch for one recording; the inference worker in the app.</summary>
public interface ITrackAnalyzer
{
    Task<TrackAnalysisResult> AnalyzeAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct);
}

/// <summary>The media work the packager needs: ffmpeg and a download in the app.</summary>
public interface IIngestMedia
{
    Task<int> ProbeDurationMsAsync(string path, CancellationToken ct);

    /// <summary>The file's audio as mono floats at <paramref name="sampleRate"/>; empty when it has no audio track.</summary>
    Task<float[]> DecodeMonoAsync(string path, int sampleRate, CancellationToken ct);

    /// <summary>Copies the video stream only (the master audio is the song's sound).</summary>
    Task StripAudioAsync(string videoIn, string videoOut, CancellationToken ct);

    Task<byte[]?> DownloadAsync(string url, CancellationToken ct);
}

/// <summary>
/// Turns a downloaded recording into a finished song folder, USMaker-style: lyrics from LRCLIB, the
/// inference worker's stems, alignment and pitch, an UltraStar chart, the cover, the music video when
/// it syncs, and metadata.json with the quality score.
///
/// Everything is built in a private staging folder. Only when every stage has succeeded are the
/// package's files gathered in "{output}\.incoming\{id}" (on the output drive, which the song scanner
/// skips) and renamed into place in one step, so the library never sees a half-built song and a
/// failure leaves nothing behind. Importing the same track again replaces its earlier package.
/// </summary>
public sealed class KaraokePackager
{
    public const string IncomingFolderName = ".incoming";
    public const string Creator = "Singularity AI";
    private const int SyncSampleRate = 8000;

    private readonly ITrackAnalyzer _analyzer;
    private readonly ILyricsLookup _lyrics;
    private readonly IIngestMedia _media;
    private readonly string _stagingRoot;
    private readonly ILogger _logger;

    public KaraokePackager(ITrackAnalyzer analyzer, ILyricsLookup lyrics, IIngestMedia media, string stagingRoot, ILogger logger)
    {
        _analyzer = analyzer;
        _lyrics = lyrics;
        _media = media;
        _stagingRoot = stagingRoot;
        _logger = logger;
    }

    /// <summary>The default staging root: %LOCALAPPDATA%\Singularity\staging.</summary>
    public static string DefaultStagingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "staging");

    public async Task<IngestResult> BuildAsync(IngestSource source, string outputRoot, IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(source.AudioPath)) throw new FileNotFoundException("The downloaded audio file is missing.", source.AudioPath);
        var sw = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        var staging = Directory.CreateDirectory(Path.Combine(_stagingRoot, id)).FullName;
        var notes = new List<string>();
        try
        {
            // Preparing: our own copy of the master, checked against the catalogue length.
            progress?.Report(new IngestProgress(IngestStage.Preparing, 0));
            var audioName = SongPackage.AudioFileName(Path.GetExtension(source.AudioPath));
            var audio = Path.Combine(staging, audioName);
            File.Copy(source.AudioPath, audio);
            int durationMs = await _media.ProbeDurationMsAsync(audio, ct);
            var audioMatch = source.ExpectedDurationMs is not { } expected || QualityScoring.IsDurationMatch(durationMs, expected)
                ? AudioMatchKind.DurationOnly
                : AudioMatchKind.Mismatch;
            if (audioMatch == AudioMatchKind.Mismatch)
                notes.Add($"The download is {Seconds(durationMs)} long, the recording {Seconds(source.ExpectedDurationMs!.Value)}.");

            // Lyrics: LRCLIB, or none (then the worker transcribes).
            progress?.Report(new IngestProgress(IngestStage.FetchingLyrics, 0));
            var lyrics = await _lyrics.FindAsync(source.Artist, source.Title, source.Album, durationMs, ct);
            if (lyrics is null) notes.Add("No lyrics found online; the words were transcribed.");

            // The chart: stems, alignment and pitch from the worker.
            progress?.Report(new IngestProgress(IngestStage.GeneratingChart, 0));
            var workerProgress = new InlineProgress<WorkerEvent>(e =>
            {
                if (e is ProgressUpdateEvent p) progress?.Report(new IngestProgress(IngestStage.GeneratingChart, StageShare(p)));
            });
            var analysis = await _analyzer.AnalyzeAsync(
                new ProcessTrackCommand($"ingest-{id}", audio, staging, lyrics?.Text, lyrics?.Kind ?? LyricsKind.Plain, source.Language, ReuseStems: false),
                workerProgress, ct);
            var (bpm, gapMs, voice) = UltraStarChartBuilder.Build(analysis.TempoBpm, analysis.Lines);
            if (voice.Notes.Count == 0) throw new InvalidOperationException("No singing was found in the recording.");
            var vocals = TakeInto(staging, analysis.VocalsPath, SongPackage.VocalsFileName);
            var instrumental = TakeInto(staging, analysis.InstrumentalPath, SongPackage.InstrumentalFileName);

            string? cover = null;
            if (source.CoverUrl is { Length: > 0 } coverUrl)
            {
                try
                {
                    if (await _media.DownloadAsync(coverUrl, ct) is { Length: > 0 } bytes)
                    {
                        cover = SongPackage.CoverFileName;
                        await File.WriteAllBytesAsync(Path.Combine(staging, cover), bytes, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Ingest: cover download failed for {Artist} - {Title}", source.Artist, source.Title);
                }
                if (cover is null) notes.Add("The cover couldn't be downloaded.");
            }

            // The video, if it follows the album arrangement.
            string? video = null;
            VideoGapResult videoGap = new(false, 0);
            if (source.VideoPath is { } videoSource)
            {
                progress?.Report(new IngestProgress(IngestStage.SyncingVideo, 0));
                var videoSound = await _media.DecodeMonoAsync(videoSource, SyncSampleRate, ct);
                if (videoSound.Length == 0)
                {
                    notes.Add("The video has no sound to sync by, so the cover is shown instead.");
                }
                else
                {
                    var master = await _media.DecodeMonoAsync(audio, SyncSampleRate, ct);
                    var sync = VideoSync.Measure(master, videoSound, SyncSampleRate);
                    if (sync.Gap.IsValid)
                    {
                        videoGap = sync.Gap;
                        video = "video" + Path.GetExtension(videoSource).ToLowerInvariant();
                        await _media.StripAudioAsync(videoSource, Path.Combine(staging, video), ct);
                    }
                    else
                    {
                        notes.Add("The video is a different version of the song, so the cover is shown instead.");
                    }
                }
            }

            progress?.Report(new IngestProgress(IngestStage.Finishing, 0));
            var song = new UltraStarSong
            {
                Title = source.Title,
                Artist = source.Artist,
                AudioFile = audioName,
                Bpm = bpm,
                GapMs = gapMs,
                VocalsFile = vocals,
                InstrumentalFile = instrumental,
                CoverFile = cover,
                VideoFile = video,
                VideoGapMs = videoGap.VideoGapMs,
                Language = LanguageName(analysis.Language),
                Year = source.Year,
                Creator = Creator,
                Voices = new[] { voice },
            };
            await File.WriteAllTextAsync(Path.Combine(staging, SongPackage.UltraStarFileName), UltraStarSerializer.Write(song), new UTF8Encoding(false), ct);

            var syllables = analysis.Lines.SelectMany(l => l.Syllables).ToList();
            var quality = QualityScoring.Assess(new QualityMetrics(
                QualityScoring.AudioMatchScore(audioMatch),
                QualityScoring.LyricScore(syllables.Where(s => s.StartsWord).Select(s => s.AlignmentConfidence)),
                QualityScoring.PitchScore(syllables.Where(s => s.MidiTone is not null).Select(s => s.PitchConfidence)),
                videoGap.Score,
                QualityScoring.MetadataScore(source.Isrc is { Length: > 0 }, analysis.TempoBpm > 0, cover is not null)));
            var trackId = source.TrackId ?? LocalTrackId(source);
            await SongPackage.WriteMetadataAsync(staging, new SongPackageMetadata
            {
                TrackId = trackId,
                Isrc = source.Isrc,
                Title = source.Title,
                Artist = source.Artist,
                Album = source.Album,
                ReleaseYear = source.Year,
                Language = analysis.Language,
                DurationMs = durationMs,
                Timing = new TimingDescriptor(bpm, gapMs, videoGap.VideoGapMs, videoGap.IsValid),
                Quality = quality,
                Provenance = new PipelineProvenance(
                    Generator, InferenceEngine: null, analysis.Models, DateTime.UtcNow, sw.ElapsedMilliseconds),
            }, ct);

            var files = new[] { audioName, SongPackage.UltraStarFileName, SongPackage.MetadataFileName, vocals, instrumental, cover, video }
                .OfType<string>().ToList();
            var folder = Publish(staging, files, outputRoot, SongPackage.FolderName(source.Artist, source.Title), trackId, id);
            _logger.LogInformation("Ingest: {Folder} ready, quality {Score} ({Tier}), video {Video}, in {Seconds:0} s",
                folder, quality.OverallScore, quality.Tier, video is not null, sw.Elapsed.TotalSeconds);
            return new IngestResult(folder, quality, video is not null, notes);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static string Generator => "Singularity/" + (typeof(KaraokePackager).Assembly.GetName().Version?.ToString(3) ?? "0");

    /// <summary>
    /// Gathers the package files in "{output}\.incoming\{id}", then renames that to the song folder. A
    /// folder of the same name that holds this track's earlier package is replaced; any other (someone
    /// else's song of the same name) is left alone and the new one gets a numbered name.
    /// </summary>
    internal static string Publish(string staging, IReadOnlyList<string> files, string outputRoot, string folderName, string trackId, string id)
    {
        var incomingRoot = Directory.CreateDirectory(Path.Combine(outputRoot, IncomingFolderName)).FullName;
        var incoming = Path.Combine(incomingRoot, id);
        Directory.CreateDirectory(incoming);
        try
        {
            foreach (var file in files)
            {
                // Same drive: a rename. Otherwise a copy; the staging folder is deleted afterwards anyway.
                if (SameVolume(staging, incoming)) File.Move(Path.Combine(staging, file), Path.Combine(incoming, file));
                else File.Copy(Path.Combine(staging, file), Path.Combine(incoming, file));
            }

            var target = Path.Combine(outputRoot, folderName);
            for (int n = 2; Directory.Exists(target) && !IsPackageOf(target, trackId); n++)
                target = Path.Combine(outputRoot, $"{folderName} ({n})");

            if (Directory.Exists(target))
            {
                var old = Path.Combine(incomingRoot, id + ".old");
                Directory.Move(target, old);
                Directory.Move(incoming, target);
                TryDelete(old);
            }
            else
            {
                Directory.Move(incoming, target);
            }
            return target;
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }
    }

    private static bool IsPackageOf(string folder, string trackId)
    {
        try
        {
            var path = Path.Combine(folder, SongPackage.MetadataFileName);
            return File.Exists(path) && SongPackage.Deserialize(File.ReadAllText(path)).TrackId == trackId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The worker writes its stems where it likes; the package names them by convention.</summary>
    private static string TakeInto(string staging, string path, string name)
    {
        var target = Path.Combine(staging, name);
        if (!string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(staging, StringComparison.OrdinalIgnoreCase)) File.Move(path, target, overwrite: true);
            else File.Copy(path, target, overwrite: true);
        }
        return name;
    }

    /// <summary>The worker reports each stage from 0 to 1; separation is the bulk of the time.</summary>
    private static double StageShare(ProgressUpdateEvent p) => p.Stage switch
    {
        PipelineStage.Separation => 0.6 * p.Progress,
        _ => 0.6 + 0.4 * p.Progress,
    };

    private static string LocalTrackId(IngestSource source)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{source.Artist}\n{source.Title}".ToLowerInvariant()));
        return "local:" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string Seconds(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");

    /// <summary>UltraStar's #LANGUAGE holds the English name, e.g. "German". Null when the worker couldn't tell ("und").</summary>
    internal static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Equals("und", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(code.Trim());
            // A code .NET doesn't know still gets a culture, named "Unknown language" or just the code.
            if (culture.EnglishName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
                || culture.EnglishName.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            return culture.EnglishName.Split(" (")[0];
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open (antivirus, a preview) only leaves litter in the staging folder.
        }
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so nothing arrives after the task ends.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
