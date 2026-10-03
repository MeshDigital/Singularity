using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Configuration;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke;
using Singularity.Karaoke.Display;
using Singularity.Karaoke.Library;
using Singularity.Karaoke.Scoring;
using Singularity.Services;
using Singularity.Services.Karaoke;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

/// <summary>Everything the stage draws for one frame, taken under the lock in <see cref="SingViewModel.Tick"/>.</summary>
public sealed record StageSnapshot(
    double Beat,
    LyricsFrame Lyrics,
    NoteLaneLayout? Lane,
    IReadOnlyList<(UltraStarNote Note, int Beat)> HitBeats,
    PitchReading? Pitch,
    int Score,
    LineResult? LastLine,
    double LastLineAgeBeats,
    bool Finished,
    Avalonia.Media.Imaging.WriteableBitmap? Video);

/// <summary>
/// The sing screen: plays the song, scores the microphone, and gives the stage a snapshot per frame.
/// Microphone audio arrives on the capture thread; all scoring state is guarded by one lock and the
/// UI only ever reads a snapshot. Positions come from <see cref="SingAudioEngine.PositionMs"/>, the
/// device's played-sample clock, never from frame timing.
/// </summary>
public sealed class SingViewModel : ReactiveObject, IDisposable
{
    /// <summary>How long after the last note the results show, in beats.</summary>
    private const int OutroBeats = 16;

    private readonly AppConfig _config;
    private readonly SingAudioEngine _audio;
    private readonly MicrophoneCapture _mic;
    private readonly INavigationService _navigation;
    private readonly ILogger<SingViewModel> _logger;
    private readonly object _sync = new();

    private UltraStarSong? _song;
    private SingerSession? _session;
    private LyricsTimeline? _timeline;
    private NoteLaneLayout? _lane;
    private int _laneLine = -1;
    private int _lastNoteEndBeat;
    private readonly List<(UltraStarNote Note, int Beat)> _hits = new();
    private LineResult? _lastLine;
    private double _lastLineBeat;
    private bool _finished;
    private long _lastDiagnosticTicks;

    private SongEntry? _entry;
    private VideoFrameSource? _video;
    private Avalonia.Media.Imaging.WriteableBitmap? _videoBitmap;
    private bool _hasVideoFrame;
    private bool _resultsShown;
    private bool _showResults;
    private ScoreBreakdown? _final;
    private string _title = "";
    private string _artist = "";
    private string _status = "";
    private Bitmap? _background;
    private bool _isPaused;

    public SingViewModel(SingAudioEngine audio, MicrophoneCapture mic, INavigationService navigation, AppConfig config, ILogger<SingViewModel> logger)
    {
        _config = config;
        _audio = audio;
        _mic = mic;
        _navigation = navigation;
        _logger = logger;
        BackCommand = new RelayCommand(Back);
        PauseCommand = new RelayCommand(TogglePause);
        RestartCommand = new RelayCommand(() => { if (_entry is { } e) Start(e); });
        _mic.SamplesCaptured += OnMicSamples;
    }

    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Artist { get => _artist; private set => this.RaiseAndSetIfChanged(ref _artist, value); }

    /// <summary>Microphone / playback notices shown in the corner.</summary>
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public Bitmap? Background { get => _background; private set => this.RaiseAndSetIfChanged(ref _background, value); }
    public bool IsPaused { get => _isPaused; private set => this.RaiseAndSetIfChanged(ref _isPaused, value); }
    public bool IsActive => _song is not null;

    public ICommand BackCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RestartCommand { get; }

    // ── Results ────────────────────────────────────────────────────────────
    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }
    private DisplayedScore Shown => DisplayedScore.From(_final ?? new ScoreBreakdown(0, 0, 0));
    public int FinalScore => Shown.Total;
    public string FinalTitle => ScoreTitles.For(FinalScore);
    public string NotesPoints => $"{Shown.Notes:N0}";
    public string GoldenPoints => $"{Shown.Golden:N0}";
    public string LineBonusPoints => $"{Shown.LineBonus:N0}";
    public string LineSummary { get; private set; } = "";

    /// <param name="startMs">Where to start playing; null = the song's #START (or the beginning).</param>
    public void Start(SongEntry entry, double? startMs = null)
    {
        StopPlayback();
        _entry = entry;
        _resultsShown = false;
        ShowResults = false;
        var song = entry.Song;
        Title = song.Title;
        Artist = song.Artist;
        Background = LoadBackground(entry.BackgroundPath ?? entry.CoverPath);

        lock (_sync)
        {
            _song = song;
            _timeline = new LyricsTimeline(song.Voices[0]);
            _lane = null;
            _laneLine = -1;
            _hits.Clear();
            _lastLine = null;
            _finished = false;
            _lastNoteEndBeat = song.Voices[0].Notes.Where(n => n.Type != NoteType.LineBreak)
                .Select(n => n.StartBeat + n.DurationBeats).DefaultIfEmpty(0).Max();
        }

        try
        {
            _audio.Load(entry.AudioPath!, startMs ?? song.StartMs ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Audio}", entry.AudioPath);
            Status = "Can't play this song's audio: " + ex.Message;
            return;
        }

        if (_mic.Start(() => _audio.PositionMs, _config.KaraokeMicDeviceId))
        {
            lock (_sync)
            {
                _session = new SingerSession(song, 0, _mic.SampleRate, Difficulty.Medium, _config.KaraokeMicLatencyMs);
                _session.Scorer.BeatJudged += (note, beat, hit) => { if (hit) _hits.Add((note, beat)); };
                _session.Scorer.LineCompleted += line => { _lastLine = line; _lastLineBeat = song.MsToBeat(_audio.PositionMs); };
            }
            Status = _mic.DeviceName ?? "";
        }
        else
        {
            Status = "No microphone: singing isn't scored";
        }

        // UltraStar's #VIDEOGAP: video position = audio position + gap.
        if (entry.VideoPath is { } videoPath)
        {
            double audioStart = startMs ?? song.StartMs ?? 0;
            _video = VideoFrameSource.Open(videoPath, audioStart + song.VideoGapMs, _logger);
            _hasVideoFrame = false;
        }

        IsPaused = false;
        _audio.Play();
        this.RaisePropertyChanged(nameof(IsActive));
    }

    private void OnMicSamples(float[] samples, int count, double songTimeMs)
    {
        lock (_sync)
        {
            if (IsPaused) return;
            _session?.Push(samples.AsSpan(0, count), songTimeMs);
        }
    }

    /// <summary>Called by the stage once per display frame.</summary>
    public StageSnapshot? Tick()
    {
        lock (_sync)
        {
            if (_song is null || _timeline is null) return null;
            double beat = _song.MsToBeat(_audio.PositionMs);
            var lyrics = _timeline.At(beat);

            if (lyrics.Current is { } line && line.Index != _laneLine)
            {
                _laneLine = line.Index;
                _lane = new NoteLaneLayout(line, _song.Voices[0].Notes);
                _hits.RemoveAll(h => h.Note.StartBeat < line.StartBeat);
            }

            // Every ~2 s: what the microphone delivers, so "the score stays 0" can be told apart from
            // "the mic hears nothing" and "readings land on the wrong beat".
            if (Environment.TickCount64 - _lastDiagnosticTicks > 2000)
            {
                _lastDiagnosticTicks = Environment.TickCount64;
                if (_session?.LastReading is { } r)
                    _logger.LogDebug("Sing: beat {Beat:0.0}, mic reading at beat {ReadingBeat:0.0}: {Level:0.0} dBFS, clarity {Clarity:0.00}, {Pitch}",
                        beat, r.Beat, r.Pitch.LevelDb, r.Pitch.Clarity, r.Pitch.IsVoiced ? $"MIDI {r.Pitch.Midi:0.0}" : "unvoiced");
                else
                    _logger.LogDebug("Sing: beat {Beat:0.0}, no microphone reading yet", beat);
            }

            if (!_finished && (beat > _lastNoteEndBeat + OutroBeats || (_audio.IsLoaded && !_audio.IsPlaying && !IsPaused && beat > 0)))
            {
                _finished = true;
                _session?.Finish();
            }
            if (_finished && !_resultsShown)
            {
                _resultsShown = true;
                var final = _session?.Scorer.Score ?? new ScoreBreakdown(0, 0, 0);
                var lines = _session?.Scorer.CompletedLines ?? Array.Empty<LineResult>();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PublishResults(final, lines));
            }

            UpdateVideo();
            return new StageSnapshot(
                beat, lyrics, _lane, _hits.ToArray(), _session?.LastReading,
                _session?.Scorer.Score.Total ?? 0, _lastLine, beat - _lastLineBeat, _finished,
                _hasVideoFrame ? _videoBitmap : null);
        }
    }

    /// <summary>Copies the due video frame into the bitmap the stage draws (UI thread, from Tick).</summary>
    private void UpdateVideo()
    {
        if (_video is not { } video || _song is not { } song) return;
        var frame = video.TakeFrame(_audio.PositionMs + song.VideoGapMs);
        if (frame is null) return;
        try
        {
            _videoBitmap ??= new Avalonia.Media.Imaging.WriteableBitmap(
                new Avalonia.PixelSize(VideoFrameSource.Width, video.Height), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using var buffer = _videoBitmap.Lock();
            if (buffer.Size.Height == video.Height)
            {
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, buffer.Address, frame.Pixels.Length);
                _hasVideoFrame = true;
            }
        }
        finally
        {
            video.Release(frame);
        }
    }

    private void PublishResults(ScoreBreakdown final, IReadOnlyList<LineResult> lines)
    {
        _audio.Stop();
        _mic.Stop();
        _final = final;
        _logger.LogInformation("Sing results: {Notes} notes + {Golden} golden + {Bonus} line bonus = {Total}", final.Notes, final.Golden, final.LineBonus, final.Total);
        int perfect = lines.Count(l => l.Rating == LineRating.Perfect);
        int great = lines.Count(l => l.Rating is LineRating.Awesome or LineRating.Great);
        LineSummary = lines.Count == 0 ? "" : $"{perfect} perfect and {great} great lines out of {lines.Count}";
        foreach (var name in new[] { nameof(FinalScore), nameof(FinalTitle), nameof(NotesPoints), nameof(GoldenPoints), nameof(LineBonusPoints), nameof(LineSummary) })
            this.RaisePropertyChanged(name);
        ShowResults = true;
    }

    private void TogglePause()
    {
        if (_song is null) return;
        if (IsPaused) _audio.Play();
        else _audio.Pause();
        IsPaused = !IsPaused;
    }

    private void Back()
    {
        StopPlayback();
        _navigation.NavigateTo("Karaoke");
    }

    private void StopPlayback()
    {
        _mic.Stop();
        _audio.Stop();
        _video?.Dispose();
        _video = null;
        _hasVideoFrame = false;
        _videoBitmap = null;
        lock (_sync)
        {
            _session = null;
            _song = null;
            _timeline = null;
        }
        this.RaisePropertyChanged(nameof(IsActive));
    }

    private Bitmap? LoadBackground(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 1280);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Background {Path} could not be decoded", path);
            return null;
        }
    }

    public void Dispose()
    {
        _mic.SamplesCaptured -= OnMicSamples;
        StopPlayback();
    }
}
