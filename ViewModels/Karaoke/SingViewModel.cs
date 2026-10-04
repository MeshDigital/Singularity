using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Singularity.Configuration;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke;
using Singularity.Karaoke.Audio;
using Singularity.Karaoke.Display;
using Singularity.Karaoke.Library;
using Singularity.Karaoke.Scoring;
using Singularity.Services;
using Singularity.Services.Karaoke;
using Singularity.Views;

namespace Singularity.ViewModels.Karaoke;

/// <summary>One singer's part of a frame.</summary>
public sealed record PlayerSnapshot(
    int Player,
    LyricsFrame Lyrics,
    NoteLaneLayout? Lane,
    IReadOnlyList<(UltraStarNote Note, int Beat)> HitBeats,
    PitchReading? Pitch,
    int Score,
    LineResult? LastLine,
    double LastLineAgeBeats);

/// <summary>Everything the stage draws for one frame, taken under the lock in <see cref="SingViewModel.Tick"/>.</summary>
/// <param name="VideoAmbient">The video isn't synced to the song: draw it dimmed, as scenery.</param>
/// <param name="SongProgress">How far through the song, 0..1.</param>
public sealed record StageSnapshot(double Beat, IReadOnlyList<PlayerSnapshot> Players, bool Finished, WriteableBitmap? Video, double TextScale,
    bool VideoAmbient = false, double SongProgress = 0);

/// <summary>A singer's line on the results screen.</summary>
public sealed record PlayerResult(string Name, int Score, string Title, string Notes, string Golden, string LineBonus, string Lines);

/// <summary>
/// The sing screen for one or two singers: plays the song, scores each microphone, and gives the
/// stage a snapshot per frame. In a duet, player 1 sings voice P1 and player 2 voice P2. With a solo
/// song, both sing the same part. Each capture device is opened once, so two players on one two-mic
/// adapter share a capture and are split by channel. Microphone audio arrives on capture threads;
/// all scoring state is guarded by one lock and the UI only reads snapshots. Positions come from
/// <see cref="SingAudioEngine.PositionMs"/>, the device's played-sample clock, never from frame timing.
/// </summary>
public sealed class SingViewModel : ReactiveObject, IDisposable
{
    /// <summary>How long after the last note the results show, in beats.</summary>
    private const int OutroBeats = 16;

    private readonly AppConfig _config;
    private readonly SingAudioEngine _audio;
    private readonly StageScreenService _stage;
    private readonly StemStore _stems;
    private readonly StemBatchQueue _batch;
    private readonly Services.Karaoke.Ingest.KaraokeIngestService _ingest;
    private readonly ConfigManager _configManager;
    private readonly ILoggerFactory _loggers;
    private readonly INavigationService _navigation;
    private readonly ILogger<SingViewModel> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, MicrophoneCapture> _captures = new();
    private readonly List<Player> _players = new();

    private UltraStarSong? _song;
    private SongEntry? _entry;
    private int _lastNoteEndBeat;
    private bool _finished;
    private bool _resultsShown;
    private VideoFrameSource? _video;
    private bool _videoAmbient;
    private Avalonia.Media.Color _glow = Avalonia.Media.Color.FromRgb(0x1A, 0x14, 0x30);
    private int _glowLoad;
    private WriteableBitmap? _videoBitmap;
    private bool _hasVideoFrame;
    private long _lastDiagnosticTicks;

    private string _title = "";
    private string _artist = "";
    private string _status = "";
    private Bitmap? _background;
    private bool _isPaused;
    private bool _showResults;

    public SingViewModel(SingAudioEngine audio, StageScreenService stage, StemStore stems, StemBatchQueue batch, Services.Karaoke.Ingest.KaraokeIngestService ingest, ConfigManager configManager,
        ILoggerFactory loggers, INavigationService navigation, AppConfig config, ILogger<SingViewModel> logger)
    {
        _batch = batch;
        _ingest = ingest;
        _stems = stems;
        _stage = stage;
        _configManager = configManager;
        _config = config;
        _audio = audio;
        _loggers = loggers;
        _navigation = navigation;
        _logger = logger;
        BackCommand = new RelayCommand(Back);
        PauseCommand = new RelayCommand(TogglePause);
        RestartCommand = new RelayCommand(() => { if (_entry is { } e) Start(e); });
        CycleVocalsCommand = new RelayCommand(CycleVocals);
        BiggerTextCommand = new RelayCommand(() => TextScale += 0.1);
        SmallerTextCommand = new RelayCommand(() => TextScale -= 0.1);
    }

    // ── Projector ──────────────────────────────────────────────────────────
    private bool _isOnProjector;
    private string _projectorText = "";
    private string _liveScores = "";
    private long _lastLiveScoreTicks;

    /// <summary>The stage is full screen on another display; this window shows the operator view.</summary>
    public bool IsOnProjector { get => _isOnProjector; private set => this.RaiseAndSetIfChanged(ref _isOnProjector, value); }
    public string ProjectorText { get => _projectorText; private set => this.RaiseAndSetIfChanged(ref _projectorText, value); }
    public string LiveScores { get => _liveScores; private set => this.RaiseAndSetIfChanged(ref _liveScores, value); }

    // ── Display size ───────────────────────────────────────────────────────
    public const double MinTextScale = 0.75, MaxTextScale = 2.0;

    /// <summary>Size of all stage text and note bars, 0.75-2.0 (1 = default); saved to config.</summary>
    public double TextScale
    {
        get => _config.KaraokeTextScale is > 0 ? _config.KaraokeTextScale : 1.0;
        set
        {
            value = Math.Round(Math.Clamp(value, MinTextScale, MaxTextScale), 2);
            if (Math.Abs(value - TextScale) < 0.001) return;
            _config.KaraokeTextScale = value;
            _ = _configManager.SaveAsync(_config);
            this.RaisePropertyChanged();
        }
    }

    public ICommand BiggerTextCommand { get; }
    public ICommand SmallerTextCommand { get; }

    // ── Original vocals ────────────────────────────────────────────────────
    public ICommand CycleVocalsCommand { get; }

    /// <summary>Button text: the current original-vocals setting.</summary>
    public string VocalsText => !_audio.HasStems ? "Vocals: original mix" : $"Vocals: {VocalsMode.ToLowerInvariant()}";

    private string VocalsMode => _config.KaraokeVocals is "Guide" or "Full" ? _config.KaraokeVocals : "Off";

    private void CycleVocals()
    {
        if (!_audio.HasStems)
        {
            Status = "This song has no separated vocals yet, so the original mix plays. Use \"Separate vocals\" in song select.";
            return;
        }
        _config.KaraokeVocals = VocalsMode switch { "Off" => "Guide", "Guide" => "Full", _ => "Off" };
        _ = _configManager.SaveAsync(_config);
        ApplyVocals();
    }

    private void ApplyVocals()
    {
        _audio.VocalsVolume = VocalsMode switch { "Full" => 1f, "Guide" => 0.3f, _ => 0f };
        this.RaisePropertyChanged(nameof(VocalsText));
    }

    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Artist { get => _artist; private set => this.RaiseAndSetIfChanged(ref _artist, value); }

    /// <summary>Microphone / playback notices shown in the corner.</summary>
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public Bitmap? Background { get => _background; private set => this.RaiseAndSetIfChanged(ref _background, value); }

    /// <summary>The cover's colour, darkened, glowing behind a song without a synced video.</summary>
    public Avalonia.Media.Color Glow { get => _glow; private set => this.RaiseAndSetIfChanged(ref _glow, value); }
    public bool IsPaused { get => _isPaused; private set => this.RaiseAndSetIfChanged(ref _isPaused, value); }
    public bool IsActive => _song is not null;

    public ICommand BackCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RestartCommand { get; }

    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }
    public ObservableCollection<PlayerResult> Results { get; } = new();

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
        LoadGlow(entry.CoverPath ?? entry.BackgroundPath);

        // Background vocal removal and song import give the GPU to the singers until the song ends.
        _batch.Hold();
        _ingest.Queue.Hold();

        var mics = MicAssignment.FromConfig(_config);
        lock (_sync)
        {
            _song = song;
            _finished = false;
            _players.Clear();
            foreach (var mic in mics)
            {
                // Duet: each singer their own part. Solo song: everyone sings the one part.
                int voice = song.IsDuet ? Math.Min(mic.Player - 1, song.Voices.Count - 1) : 0;
                _players.Add(new Player(mic, voice, song));
            }
            _lastNoteEndBeat = _players.Select(p => p.Voice).Distinct()
                .SelectMany(v => song.Voices[v].Notes).Where(n => n.Type != NoteType.LineBreak)
                .Select(n => n.StartBeat + n.DurationBeats).DefaultIfEmpty(0).Max();
        }

        try
        {
            // With separated stems the original vocals can be turned down (V); otherwise the original mix plays.
            _audio.Load(entry.AudioPath!, startMs ?? song.StartMs ?? 0, _stems.Find(entry));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Audio}", entry.AudioPath);
            Status = "Can't play this song's audio: " + ex.Message;
            return;
        }

        StartMicrophones(song);

        // UltraStar's #VIDEOGAP: video position = audio position + gap.
        if (entry.VideoPath is { } videoPath)
        {
            _video = VideoFrameSource.Open(videoPath, (startMs ?? song.StartMs ?? 0) + song.VideoGapMs, _logger);
            _hasVideoFrame = false;
            _videoAmbient = IsUnsyncedVideo(entry.Folder);
        }

        ApplyVocals();
        IsPaused = false;
        _audio.Play();
        this.RaisePropertyChanged(nameof(IsActive));

        var screen = _stage.Open(this);
        IsOnProjector = screen is not null;
        ProjectorText = screen is null ? "" : $"Singing on {screen}. Esc or Back ends the song.";
    }

    /// <summary>Opens each distinct capture device once and gives every player a scoring session on it.</summary>
    private void StartMicrophones(UltraStarSong song)
    {
        var notes = new List<string>();
        foreach (var group in _players.GroupBy(p => p.Mic.DeviceKey))
        {
            var players = group.ToList();
            string who = "P" + string.Join("+P", players.Select(p => p.Mic.Player));
            var capture = new MicrophoneCapture(_loggers.CreateLogger<MicrophoneCapture>());
            if (!capture.Start(() => _audio.PositionMs, group.Key))
            {
                capture.Dispose();
                notes.Add($"{who}: no microphone");
                continue;
            }
            _captures[group.Key] = capture;
            lock (_sync)
            {
                foreach (var p in players)
                {
                    var player = p;
                    p.Session = new SingerSession(song, p.Voice, capture.SampleRate, Difficulty.Medium, _config.KaraokeMicLatencyMs);
                    p.Session.Scorer.BeatJudged += (note, beat, hit) => { if (hit) player.Hits.Add((note, beat)); };
                    p.Session.Scorer.LineCompleted += line => { player.LastLine = line; player.LastLineBeat = song.MsToBeat(_audio.PositionMs); };
                }
            }
            capture.SamplesCaptured += block => OnMicBlock(players, block);
            notes.Add($"{who}: {capture.DeviceName}");
        }
        Status = string.Join(" · ", notes);
    }

    private void OnMicBlock(List<Player> players, MicBlock block)
    {
        lock (_sync)
        {
            if (IsPaused) return;
            foreach (var p in players)
            {
                if (p.Session is null) continue;
                if (p.Mono.Length < block.Frames) p.Mono = new float[block.Frames];
                int count = block.Extract(p.Mic.Channel, p.Mono);
                p.Session.Push(p.Mono.AsSpan(0, count), block.TimeMs);
            }
        }
    }

    /// <summary>Called by the stage once per display frame.</summary>
    public StageSnapshot? Tick()
    {
        lock (_sync)
        {
            if (_song is not { } song) return null;
            double beat = song.MsToBeat(_audio.PositionMs);

            var players = new List<PlayerSnapshot>(_players.Count);
            foreach (var p in _players)
            {
                var lyrics = p.Timeline.At(beat);
                if (lyrics.Current is { } line && line.Index != p.LaneLine)
                {
                    p.LaneLine = line.Index;
                    p.Lane = new NoteLaneLayout(line, song.Voices[p.Voice].Notes);
                    p.Hits.RemoveAll(h => h.Note.StartBeat < line.StartBeat);
                }
                players.Add(new PlayerSnapshot(p.Mic.Player, lyrics, p.Lane, p.Hits.ToArray(), p.Session?.LastReading,
                    p.Session?.Scorer.Score.Total ?? 0, p.LastLine, beat - p.LastLineBeat));
            }

            LogDiagnostics(beat);

            bool audioEnded = _audio.IsLoaded && !_audio.IsPlaying && !IsPaused && beat > 0;
            if (!_finished && (beat > _lastNoteEndBeat + OutroBeats || audioEnded))
            {
                _finished = true;
                foreach (var p in _players) p.Session?.Finish();
            }
            if (_finished && !_resultsShown)
            {
                _resultsShown = true;
                var results = _players.Select(Result).ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PublishResults(results));
            }

            UpdateVideo();
            if (Environment.TickCount64 - _lastLiveScoreTicks > 250)
            {
                _lastLiveScoreTicks = Environment.TickCount64;
                LiveScores = string.Join("     ", players.Select(p => (_players.Count > 1 ? $"P{p.Player} " : "Score ") + p.Score.ToString("N0")));
            }
            double end = _song?.EndMs is { } e && e > 0 ? e : _audio.DurationMs;
            double progress = end > 0 ? Math.Clamp(_audio.PositionMs / end, 0, 1) : 0;
            return new StageSnapshot(beat, players, _finished, _hasVideoFrame ? _videoBitmap : null, TextScale, _videoAmbient, progress);
        }
    }

    private PlayerResult Result(Player p)
    {
        var score = p.Session?.Scorer.Score ?? new ScoreBreakdown(0, 0, 0);
        var shown = DisplayedScore.From(score);
        var lines = p.Session?.Scorer.CompletedLines ?? Array.Empty<LineResult>();
        int perfect = lines.Count(l => l.Rating == LineRating.Perfect);
        int great = lines.Count(l => l.Rating is LineRating.Awesome or LineRating.Great);
        _logger.LogInformation("Sing results P{Player}: {Notes} notes + {Golden} golden + {Bonus} line bonus = {Total}",
            p.Mic.Player, score.Notes, score.Golden, score.LineBonus, score.Total);
        return new PlayerResult(
            _players.Count > 1 ? $"Player {p.Mic.Player}" : "",
            shown.Total, ScoreTitles.For(shown.Total),
            $"{shown.Notes:N0}", $"{shown.Golden:N0}", $"{shown.LineBonus:N0}",
            p.Session is null ? "No microphone" : lines.Count == 0 ? "" : $"{perfect} perfect, {great} great of {lines.Count} lines");
    }

    private void PublishResults(IReadOnlyList<PlayerResult> results)
    {
        _audio.Stop();
        StopMicrophones();
        _batch.Release(); // nobody is singing on the results screen
        _ingest.Queue.Release();
        Results.Clear();
        foreach (var r in results) Results.Add(r);
        ShowResults = true;
    }

    // Every ~2 s: what each microphone delivers, so "the score stays 0" can be told apart from
    // "the mic hears nothing" and "readings land on the wrong beat".
    private void LogDiagnostics(double beat)
    {
        if (Environment.TickCount64 - _lastDiagnosticTicks <= 2000) return;
        _lastDiagnosticTicks = Environment.TickCount64;
        foreach (var p in _players)
        {
            if (p.Session?.LastReading is { } r)
                _logger.LogDebug("Sing P{Player}: beat {Beat:0.0}, reading at beat {ReadingBeat:0.0}: {Level:0.0} dBFS, clarity {Clarity:0.00}, {Pitch}",
                    p.Mic.Player, beat, r.Beat, r.Pitch.LevelDb, r.Pitch.Clarity, r.Pitch.IsVoiced ? $"MIDI {r.Pitch.Midi:0.0}" : "unvoiced");
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
            _videoBitmap ??= new WriteableBitmap(
                new Avalonia.PixelSize(video.Width, video.Height), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using var buffer = _videoBitmap.Lock();
            if (buffer.Size.Width == video.Width && buffer.Size.Height == video.Height)
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

    private void TogglePause()
    {
        if (_song is null || ShowResults) return;
        if (IsPaused) _audio.Play();
        else _audio.Pause();
        IsPaused = !IsPaused;
    }

    private void Back()
    {
        StopPlayback();
        _navigation.NavigateTo("Karaoke");
    }

    private void StopMicrophones()
    {
        foreach (var capture in _captures.Values) capture.Dispose();
        _captures.Clear();
    }

    private void StopPlayback()
    {
        _batch.Release();
        _ingest.Queue.Release();
        _stage.Close();
        IsOnProjector = false;
        StopMicrophones();
        _audio.Stop();
        _video?.Dispose();
        _video = null;
        _hasVideoFrame = false;
        _videoBitmap = null;
        lock (_sync)
        {
            _players.Clear();
            _song = null;
        }
        this.RaisePropertyChanged(nameof(IsActive));
    }

    /// <summary>
    /// A song Singularity made whose video didn't sync (its metadata.json says so): the video plays from
    /// the start, dimmed. Community songs have no metadata.json and their #VIDEOGAP is trusted.
    /// </summary>
    private static bool IsUnsyncedVideo(string folder)
    {
        var path = Path.Combine(folder, Singularity.Contracts.Song.SongPackage.MetadataFileName);
        if (!File.Exists(path)) return false;
        try
        {
            return !Singularity.Contracts.Song.SongPackage.Deserialize(File.ReadAllText(path)).Timing.VideoStructureValid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private void LoadGlow(string? coverPath)
    {
        int load = ++_glowLoad;
        if (coverPath is null) return;
        _ = Task.Run(() =>
        {
            var c = StageBrowseViewModel.DominantColor(coverPath);
            // Darkened: it sits behind the notes and lyrics, which must stay readable.
            var dim = Avalonia.Media.Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (load == _glowLoad) Glow = dim;
            });
        });
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

    public void Dispose() => StopPlayback();

    private sealed class Player(MicAssignment mic, int voice, UltraStarSong song)
    {
        public MicAssignment Mic { get; } = mic;
        public int Voice { get; } = voice;
        public LyricsTimeline Timeline { get; } = new(song.Voices[voice]);
        public SingerSession? Session { get; set; }
        public NoteLaneLayout? Lane { get; set; }
        public int LaneLine { get; set; } = -1;
        public List<(UltraStarNote Note, int Beat)> Hits { get; } = new();
        public LineResult? LastLine { get; set; }
        public double LastLineBeat { get; set; }
        public float[] Mono { get; set; } = Array.Empty<float>();
    }
}
