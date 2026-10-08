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
using Singularity.Karaoke.Scoring;
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
    int? Year = null,
    bool PreferAi = false);

/// <summary>Where a song is in the making. <see cref="SeparatingVocals"/> and <see cref="PlacingChart"/> are the community chart's path;
/// <see cref="FetchingLyrics"/> and <see cref="GeneratingChart"/> the AI chart's.</summary>
public enum IngestStage { Preparing, FindingChart, FetchingLyrics, GeneratingChart, DownloadingVideo, SyncingVideo, Finishing, SeparatingVocals, PlacingChart }

/// <param name="Fraction">Progress within the stage, 0..1.</param>
public sealed record IngestProgress(IngestStage Stage, double Fraction);

/// <param name="HasVideo">The music video is synced to the song.</param>
/// <param name="Notes">What was left out and why (no lyrics found, video not synced, …), for the queue's detail line.</param>
/// <param name="AmbientVideo">A video is there but not synced; it plays dimmed as a backdrop.</param>
/// <param name="ChartSource">Where a community chart came from (e.g. "usdb:295"); null for an AI chart.</param>
public sealed record IngestResult(string PackageFolder, QualityAssessment Quality, bool HasVideo, IReadOnlyList<string> Notes, bool AmbientVideo = false,
    string? ChartSource = null);

/// <summary>A chart a person made for the song (from USDB), not yet placed on our recording.</summary>
/// <param name="SourceId">E.g. "usdb:295".</param>
/// <param name="YoutubeId">The video the chart names, tried first for the music video.</param>
public sealed record CommunityChart(UltraStarSong Chart, string SourceId, string? YoutubeId);

/// <summary>Finds community charts; USDB in the app.</summary>
public interface ICommunityCharts
{
    Task<CommunityChart?> FindAsync(string artist, string title, CancellationToken ct);
}

/// <summary>Finds lyrics for a recording; the LRCLIB client in the app.</summary>
public interface ILyricsLookup
{
    Task<(string Text, LyricsKind Kind)?> FindAsync(string artist, string title, string? album, int durationMs, CancellationToken ct);
}

/// <summary>Separation, alignment and pitch for one recording; the inference worker in the app.</summary>
public interface ITrackAnalyzer
{
    Task<TrackAnalysisResult> AnalyzeAsync(ProcessTrackCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct);

    /// <summary>Only the vocal separation: vocals.wav and instrumental.wav into the command's folder.</summary>
    Task SeparateAsync(SeparateStemsCommand command, IProgress<WorkerEvent>? progress, CancellationToken ct);
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

    /// <summary>The pitch check needs at least 3 s of singing to judge a chart; with less it is skipped.</summary>
    private const int MinPitchReadings = 300;

    private readonly ITrackAnalyzer _analyzer;
    private readonly ILyricsLookup _lyrics;
    private readonly IIngestMedia _media;
    private readonly string _stagingRoot;
    private readonly ILogger _logger;
    private readonly IVideoFinder? _videos;
    private readonly ICommunityCharts? _community;

    /// <param name="videos">Finds a music video when the source brings none; null: songs without one get the cover.</param>
    /// <param name="community">Community charts to use before making an AI one; null: always AI.</param>
    public KaraokePackager(ITrackAnalyzer analyzer, ILyricsLookup lyrics, IIngestMedia media, string stagingRoot, ILogger logger, IVideoFinder? videos = null,
        ICommunityCharts? community = null)
    {
        _videos = videos;
        _community = community;
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
        Task<string?>? videoDownload = null;
        using var videoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

            // A chart a person made beats an AI one, when there is one and it fits this recording.
            CommunityChart? community = null;
            if (_community is not null && !source.PreferAi)
            {
                progress?.Report(new IngestProgress(IngestStage.FindingChart, 0));
                community = await FindCommunityChartAsync(source, ct);
            }

            // The music video downloads while the GPU works on the chart (network and GPU don't compete).
            if (source.VideoPath is null && _videos is not null)
                videoDownload = FindVideoAsync(source, durationMs, staging, community?.YoutubeId, videoCts.Token);

            // With a community chart the worker only separates the vocals (to place the chart); "Generating AI chart"
            // is only said when an AI chart is really being made.
            var chartStage = community is null ? IngestStage.GeneratingChart : IngestStage.SeparatingVocals;
            progress?.Report(new IngestProgress(chartStage, 0));
            var workerProgress = new InlineProgress<WorkerEvent>(e =>
            {
                if (e is ProgressUpdateEvent p) progress?.Report(new IngestProgress(chartStage, StageShare(p)));
            });

            UltraStarSong? chart = null;
            string? chartSource = null, languageCode = null;
            double lyricScore = 1, pitchScore = 1;
            bool hasTempo = true;
            IReadOnlyDictionary<string, string> models = new Dictionary<string, string>();
            string vocals = SongPackage.VocalsFileName, instrumental = SongPackage.InstrumentalFileName;

            if (community is not null)
            {
                // Place the community chart on our recording by its vocals (which the song needs anyway).
                await _analyzer.SeparateAsync(new SeparateStemsCommand($"ingest-{id}", audio, staging), workerProgress, ct);
                progress?.Report(new IngestProgress(IngestStage.PlacingChart, 0));
                var sung = await _media.DecodeMonoAsync(Path.Combine(staging, SongPackage.VocalsFileName), SyncSampleRate, ct);
                var fit = ChartSync.Measure(community.Chart, sung, SyncSampleRate);
                var placed = fit.Gap.IsValid ? ChartSync.Shift(community.Chart, fit.Gap.VideoGapMs) : null;
                _logger.LogInformation("Ingest: {Source} for {Artist} - {Title}: #GAP {Gap} ms, placement {Valid} {Offset:+0;-0} ms",
                    community.SourceId, source.Artist, source.Title, community.Chart.GapMs, fit.Gap.IsValid, fit.Gap.VideoGapMs);
                string? correction = null;
                if (placed is not null)
                {
                    // Then the notes themselves: does the singer actually sing them, there and in that key?
                    var sungPitch = ChartPitchCheck.Readings(
                        await _media.DecodeMonoAsync(Path.Combine(staging, SongPackage.VocalsFileName), ChartPitchCheck.SampleRate, ct),
                        ChartPitchCheck.SampleRate);
                    if (sungPitch.Count >= MinPitchReadings)
                    {
                        var check = ChartPitchCheck.Check(placed, sungPitch);
                        _logger.LogInformation("Ingest: {Source} pitch check: {Share:P0} of the singing on its notes; best {Best:P0} with {Gap:+0;-0} ms, {Transpose:+0;-0} semitones",
                            community.SourceId, check.Share, check.BestShare, check.GapCorrectionMs, check.Transpose);
                        if (check.Fixable)
                        {
                            placed = ChartPitchCheck.Transpose(ChartSync.Shift(placed, check.GapCorrectionMs), check.Transpose);
                            correction = (check.GapCorrectionMs != 0 ? $" moved {check.GapCorrectionMs:+0;-0} ms" : "")
                                + (check.Transpose != 0 ? $" transposed {check.Transpose:+0;-0} semitones" : "");
                        }
                        else if (!check.Fits) placed = null;
                    }
                }
                if (placed is not null)
                {
                    chart = placed;
                    chartSource = community.SourceId;
                    models = new Dictionary<string, string> { ["chart"] = community.SourceId, ["separation"] = "htdemucs_ft" };
                    notes.Add($"Community chart{(string.IsNullOrWhiteSpace(chart.Creator) ? "" : " by " + chart.Creator.Trim())} from USDB, placed on this recording ({fit.Gap.VideoGapMs:+0;-0} ms)"
                        + (correction is null ? "." : $"; its notes didn't match the singer until{correction}."));
                }
                else
                {
                    notes.Add("A community chart was found, but it doesn't fit this recording (another edit), so an AI chart was made.");
                    _logger.LogInformation("Ingest: {Source} doesn't fit {Artist} - {Title}; making an AI chart", community.SourceId, source.Artist, source.Title);
                }
            }

            if (chart is null)
            {
                // Lyrics: LRCLIB, or none (then the worker transcribes).
                progress?.Report(new IngestProgress(IngestStage.FetchingLyrics, 0));
                (string Text, LyricsKind Kind)? lyrics;
                try
                {
                    lyrics = await _lyrics.FindAsync(source.Artist, source.Title, source.Album, durationMs, ct);
                    if (lyrics is null) notes.Add("No lyrics found online; the words were transcribed.");
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // The lyrics site being down mustn't cost the song: the worker transcribes the words instead.
                    _logger.LogWarning("Ingest: lyrics lookup failed for {Artist} - {Title}: {Error}", source.Artist, source.Title, ex.Message);
                    lyrics = null;
                    notes.Add("The lyrics site couldn't be reached; the words were transcribed.");
                }

                // The chart: stems (reused when the community attempt made them), alignment and pitch from the worker.
                chartStage = IngestStage.GeneratingChart;
                progress?.Report(new IngestProgress(IngestStage.GeneratingChart, 0));
                var analysis = await _analyzer.AnalyzeAsync(
                    new ProcessTrackCommand($"ingest-{id}", audio, staging, lyrics?.Text, lyrics?.Kind ?? LyricsKind.Plain, source.Language, ReuseStems: true),
                    workerProgress, ct);
                var (bpm, gapMs, voice) = UltraStarChartBuilder.Build(analysis.TempoBpm, analysis.Lines);
                if (voice.Notes.Count == 0) throw new InvalidOperationException("No singing was found in the recording.");
                vocals = TakeInto(staging, analysis.VocalsPath, SongPackage.VocalsFileName);
                instrumental = TakeInto(staging, analysis.InstrumentalPath, SongPackage.InstrumentalFileName);
                chart = new UltraStarSong
                {
                    Title = source.Title, Artist = source.Artist, AudioFile = audioName, Bpm = bpm, GapMs = gapMs,
                    Language = LanguageName(analysis.Language), Creator = Creator, Voices = new[] { voice },
                };
                languageCode = analysis.Language;
                var syllables = analysis.Lines.SelectMany(l => l.Syllables).ToList();
                lyricScore = QualityScoring.LyricSubScore(syllables.Where(s => s.StartsWord).Select(s => s.AlignmentConfidence));
                pitchScore = QualityScoring.PitchSubScore(syllables.Where(s => s.MidiTone is not null).Select(s => s.PitchConfidence));
                hasTempo = analysis.TempoBpm > 0;
                models = analysis.Models;
            }

            // The notes against what the singer sang (from the vocals the song now has): an AI chart's notes the singer
            // sings steadily elsewhere are moved there; a community chart is only measured (people chart the intended
            // melody). The result is kept in metadata.json for song select.
            ChartCheck? noteCheck = null;
            var vocalsInPackage = Path.Combine(staging, vocals);
            if (File.Exists(vocalsInPackage))
            {
                var singer = ReferencePitch.FromVocals(await _media.DecodeMonoAsync(vocalsInPackage, ChartPitchCheck.SampleRate, ct), ChartPitchCheck.SampleRate);
                var result = ChartNoteCheck.Check(chart!, singer);
                int corrected = 0;
                if (chartSource is null)
                {
                    // An AI chart's notes from what the singer sings (the middle of each note, in the song's key):
                    // ChartBench 65.1% -> 70.2% exact pitch class against human charts.
                    var (fitted, changed) = ChartMelody.FitNotes(chart!, singer);
                    if (changed > 0)
                    {
                        corrected = changed;
                        chart = fitted;
                        result = ChartNoteCheck.Check(chart, singer);
                        notes.Add($"{corrected} notes set to where the singer sings them.");
                    }
                }
                if (result.Judged > 0)
                {
                    noteCheck = new ChartCheck(Math.Round(result.Agreement, 3), result.LinesToCheck, corrected, result.Mismatch);
                    if (result.Mismatch) notes.Add("The chart's notes don't match the singer: check it before singing.");
                    _logger.LogInformation("Ingest: notes against the singer: {Agreement:P0} agree, {Lines} of {Total} lines to check, {Corrected} corrected",
                        result.Agreement, result.LinesToCheck.Count, result.Lines, corrected);
                }
            }

            string? cover = null;
            if (source.CoverUrl is { Length: > 0 } coverUrl)
            {
                try
                {
                    // A cover on disk (re-charting a song that has one) is copied; a link is downloaded.
                    var bytes = File.Exists(coverUrl) ? await File.ReadAllBytesAsync(coverUrl, ct) : await _media.DownloadAsync(coverUrl, ct);
                    if (bytes is { Length: > 0 })
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
            var videoSource = source.VideoPath;
            if (videoDownload is not null)
            {
                if (!videoDownload.IsCompleted) progress?.Report(new IngestProgress(IngestStage.DownloadingVideo, 0));
                videoSource = await videoDownload;
                if (videoSource is null) notes.Add("No music video was found, so the cover is shown instead.");
            }
            if (videoSource is not null)
            {
                progress?.Report(new IngestProgress(IngestStage.SyncingVideo, 0));
                var videoSound = await _media.DecodeMonoAsync(videoSource, SyncSampleRate, ct);
                var master = videoSound.Length == 0 ? null : await _media.DecodeMonoAsync(audio, SyncSampleRate, ct);
                var sync = master is null ? null : VideoSync.Measure(master, videoSound, SyncSampleRate);
                if (sync is { Gap.IsValid: true })
                {
                    videoGap = sync.Gap;
                }
                else
                {
                    // Not synced (another edit, a long intro, no sound): the video is still worth showing as moving
                    // scenery. It starts with the song and the player shows it dimmed, so nobody reads its lips.
                    notes.Add(videoSound.Length == 0
                        ? "The video has no sound to sync by, so it plays dimmed in the background."
                        : "The video is a different version of the song, so it plays dimmed in the background.");
                }
                video = "video" + Path.GetExtension(videoSource).ToLowerInvariant();
                await _media.StripAudioAsync(videoSource, Path.Combine(staging, video), ct);
            }

            progress?.Report(new IngestProgress(IngestStage.Finishing, 0));
            // The chart's own notes, timing and credits; our recording, stems, cover and video. A community
            // chart keeps its #CREATOR, which is also how the library tells it from an AI chart.
            var song = chart with
            {
                Title = source.Title,
                Artist = source.Artist,
                AudioFile = audioName,
                VocalsFile = vocals,
                InstrumentalFile = instrumental,
                CoverFile = cover,
                BackgroundFile = null,
                VideoFile = video,
                VideoGapMs = videoGap.VideoGapMs,
                Year = chart.Year ?? source.Year,
                Creator = string.IsNullOrWhiteSpace(chart.Creator) ? (chartSource is null ? Creator : "USDB community") : chart.Creator,
            };
            await File.WriteAllTextAsync(Path.Combine(staging, SongPackage.UltraStarFileName), UltraStarSerializer.Write(song), new UTF8Encoding(false), ct);

            // A person's chart counts as fully confident lyrics and pitch.
            var quality = QualityScoring.Assess(new QualityMetrics(
                QualityScoring.AudioMatchScore(audioMatch),
                lyricScore,
                pitchScore,
                videoGap.Score,
                QualityScoring.MetadataScore(source.Isrc is { Length: > 0 }, hasTempo, cover is not null)));
            var trackId = source.TrackId ?? LocalTrackId(source);
            await SongPackage.WriteMetadataAsync(staging, new SongPackageMetadata
            {
                TrackId = trackId,
                Isrc = source.Isrc,
                Title = source.Title,
                Artist = source.Artist,
                Album = source.Album,
                ReleaseYear = source.Year,
                Language = languageCode ?? song.Language,
                DurationMs = durationMs,
                Timing = new TimingDescriptor(song.Bpm, song.GapMs, videoGap.VideoGapMs, videoGap.IsValid),
                Check = noteCheck,
                Quality = quality,
                Provenance = new PipelineProvenance(
                    Generator, InferenceEngine: null, models, DateTime.UtcNow, sw.ElapsedMilliseconds),
            }, ct);

            var files = new[] { audioName, SongPackage.UltraStarFileName, SongPackage.MetadataFileName, vocals, instrumental, cover, video }
                .OfType<string>().ToList();
            var folder = Publish(staging, files, outputRoot, SongPackage.FolderName(source.Artist, source.Title), trackId, id);
            _logger.LogInformation("Ingest: {Folder} ready, quality {Score} ({Tier}), video {Video}, in {Seconds:0} s",
                folder, quality.OverallScore, quality.Tier, video is not null, sw.Elapsed.TotalSeconds);
            return new IngestResult(folder, quality, video is not null && videoGap.IsValid, notes, AmbientVideo: video is not null && !videoGap.IsValid,
                ChartSource: chartSource);
        }
        finally
        {
            // A failed chart leaves the video download running: stop it before clearing its folder.
            if (videoDownload is { IsCompleted: false })
            {
                videoCts.Cancel();
                try { await videoDownload; } catch (Exception) { /* cancelled */ }
            }
            TryDelete(staging);
        }
    }

    /// <summary>A community chart is a bonus: when the lookup fails, the song gets an AI chart.</summary>
    private async Task<CommunityChart?> FindCommunityChartAsync(IngestSource source, CancellationToken ct)
    {
        try
        {
            return await _community!.FindAsync(source.Artist, source.Title, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ingest: community chart lookup failed for {Artist} - {Title}", source.Artist, source.Title);
            return null;
        }
    }

    /// <summary>A missing video never fails the song: it just gets the cover. The chart's own video is tried first.</summary>
    private async Task<string?> FindVideoAsync(IngestSource source, int durationMs, string staging, string? preferredYoutubeId, CancellationToken ct)
    {
        try
        {
            if (preferredYoutubeId is not null && await _videos!.DownloadAsync(preferredYoutubeId, staging, ct) is { } named) return named;
            return await _videos!.FindAsync(source.Artist, source.Title, durationMs, staging, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ingest: video download failed for {Artist} - {Title}", source.Artist, source.Title);
            return null;
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
            try
            {
                if (!Directory.EnumerateFileSystemEntries(incomingRoot).Any()) Directory.Delete(incomingRoot);
            }
            catch (IOException)
            {
                // Another import is gathering its files there right now.
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
