using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Timers;
using Singularity.Configuration;
using Singularity.Services.Audio;

namespace Singularity.Services
{
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
        private readonly AppConfig _config;

        /// <summary>
        /// One decoded/opened audio stream + its output device. Two decks let the engine have
        /// the next track already open and ready (preloaded) while the current one is still
        /// playing, which is what makes gapless transitions and crossfading possible.
        /// </summary>
        private class Deck : IDisposable
        {
            public AudioFileReader? AudioFile;
            public IWavePlayer? Output;
            public MeteringSampleProvider? Metering;
            public VariSpeedSampleProvider? VariSpeed;
            /// <summary>In-process gain stage for master volume/crossfade/loudness automation.
            /// Deliberately NOT <see cref="IWavePlayer.Volume"/> — on WASAPI that setter writes
            /// through to the OS-level per-app session volume (the same control behind the
            /// Windows Volume Mixer slider for this process), not an internal signal multiply.
            /// Driving that ~20x/sec from the crossfade timer was a real bug: it round-trips
            /// through the Windows Audio Session API on every tick, and any dropped/out-of-order
            /// update (process suspend, COM call failure, a deck disposed mid-write) leaves the
            /// OS-visible session sitting at whatever the last write was — reported as "audio
            /// gets muted in Windows". <see cref="Output"/>'s own Volume is pinned to 1.0 once at
            /// creation and never touched again; all real gain changes go through this instead.</summary>
            public NAudio.Wave.SampleProviders.VolumeSampleProvider? Gain;
            /// <summary>Linear gain applied on top of the master volume for loudness-normalized playback (see <see cref="AppConfig.LoudnessNormalizationEnabled"/>). 1.0 = no adjustment.</summary>
            public float LoudnessGain = 1f;

            /// <summary>File this deck plays (for <see cref="Singularity.Services.Audio.ExactSeek"/>).</summary>
            public string FilePath = string.Empty;

            /// <summary>Background exact seek in flight (Media Foundation formats decode forward to
            /// the target, ~70–300 ms). The deck must not start playing until it finishes.</summary>
            public System.Threading.Tasks.Task? SeekTask;

            /// <summary>Live-seek hand-off (see the Position setter): latest target not yet applied,
            /// and whether a worker is draining it. Guarded by <see cref="SeekLock"/>.</summary>
            public readonly object SeekLock = new();
            public double? RequestedSeekSeconds;
            public bool SeekWorkerRunning;

            /// <summary>Blocks until a pending background seek is done (normally long finished —
            /// preload happens minutes before the crossfade). Bounded so playback can never hang.</summary>
            public void WaitForSeek()
            {
                try { SeekTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* seek failure: play from wherever it is */ }
            }

            /// <summary>Seeks this (not yet playing) deck exactly to <paramref name="seconds"/> in the background.</summary>
            public void SeekInBackground(double seconds)
            {
                var file = AudioFile;
                if (file == null) return;
                if (!Singularity.Services.Audio.ExactSeek.NeedsDecodeForward(FilePath))
                {
                    WaitForSeek();
                    file.CurrentTime = TimeSpan.FromSeconds(seconds);
                    VariSpeed?.Reset();
                    return;
                }
                var previous = SeekTask;
                SeekTask = System.Threading.Tasks.Task.Run(async () =>
                {
                    if (previous != null) { try { await previous; } catch { } }
                    Singularity.Services.Audio.ExactSeek.Seek(file, FilePath, seconds);
                    VariSpeed?.Reset();
                });
            }

            public void Dispose()
            {
                try { Output?.Stop(); } catch { /* already stopped/disposed */ }
                AudioFile?.Dispose();
                Output?.Dispose();
            }
        }


        private Deck? _current;
        private Deck? _next;
        private string? _nextFilePath;
        private bool _isCrossfading;
        private double _crossfadeElapsedSeconds;
        private double _crossfadeProgress;
        private float _masterVolumeFraction = 1f;

        private bool _isInitialized;
        private System.Timers.Timer? _timer;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler<AudioLevelsEventArgs>? AudioLevelsChanged;
        public event EventHandler<float[]>? SpectrumChanged;
        /// <summary>Mono time-domain block (2048 samples) of what is playing, while a visualizer is attached.</summary>
        public event EventHandler<float[]>? WaveformChanged;
        public event EventHandler? EndReached;
        public event EventHandler? PausableChanged;

        /// <summary>True while an active crossfade is in progress. Previously computed
        /// entirely internally (the <c>_isCrossfading</c> field) with no way for any ViewModel
        /// to know a mix was even happening, let alone how far through it was or which preset.</summary>
        public bool IsCrossfading => _isCrossfading;

        /// <summary>0.0-1.0 progress through the active crossfade; 0 when none is active.</summary>
        public double CrossfadeProgress => _crossfadeProgress;

        public event EventHandler<CrossfadeStartedEventArgs>? CrossfadeStarted;
        public event EventHandler<double>? CrossfadeProgressChanged;
        public event EventHandler? CrossfadeEnded;

        /// <summary>Fired when the engine autonomously advances to a preloaded track (gapless
        /// swap or crossfade completion), so listeners can sync "now playing" state without
        /// re-triggering Play() themselves.</summary>
        public event EventHandler? TrackAdvanced;

        private int _vuMeterSkipCounter = 0;
        private const int VU_METER_SKIP_FRAMES = 5; // Only update VU meter every 5th buffer
        private const double TimerIntervalSeconds = 0.05; // 50ms tick, matches _timer below

        private double _pitch = 1.0;
        /// <summary>Turntable-style pitch: 1.0 = normal, &gt;1.0 = faster/higher, &lt;1.0 =
        /// slower/lower. Speed and pitch move together, matching a real turntable/CDJ pitch
        /// fader rather than tempo-only time-stretching.</summary>
        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                if (_current?.VariSpeed != null) _current.VariSpeed.Speed = value;
                if (_next?.VariSpeed != null) _next.VariSpeed.Speed = value;
            }
        }

        /// <summary>When enabled, the preloaded next track fades in while the current track
        /// fades out over <see cref="CrossfadeSeconds"/>, instead of a hard gapless cut.</summary>
        public bool CrossfadeEnabled { get; set; } = false;

        /// <summary>Length of the crossfade overlap, in seconds. Only used when
        /// <see cref="CrossfadeEnabled"/> is true.</summary>
        public double CrossfadeSeconds { get; set; } = 3.0;

        public AudioPlayerService(AppConfig config)
        {
            _config = config;
            _isInitialized = true;
            _timer = new System.Timers.Timer(TimerIntervalSeconds * 1000);
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var current = _current;
            if (current?.AudioFile == null) return;

            // The outgoing track ran out mid-crossfade. This used to stall for good: this timer
            // bailed out as soon as the outgoing deck wasn't Playing, and the deck's own
            // PlaybackStopped handler deliberately defers to the crossfade — so nothing ever
            // finished it. The incoming track kept playing at a partial fade level while the UI
            // sat on the old track past the end of its waveform, and the hand-over came late or
            // never. Auto-generated Outro cues sit only seconds before the end, so this hit
            // almost every transition. Finish the hand-over now instead.
            if (_isCrossfading && current.Output?.PlaybackState == PlaybackState.Stopped)
            {
                CompleteCrossfade();
                return;
            }

            if (current.Output?.PlaybackState != PlaybackState.Playing)
                return;

            TimeChanged?.Invoke(this, (long)current.AudioFile.CurrentTime.TotalMilliseconds);
            PositionChanged?.Invoke(this, Position);

            if (_isCrossfading)
            {
                AdvanceCrossfade(current);
                return;
            }

            if (CrossfadeEnabled && _next?.Output != null)
            {
                var remaining = current.AudioFile.TotalTime - current.AudioFile.CurrentTime;
                if (remaining.TotalSeconds <= CrossfadeSeconds)
                {
                    _isCrossfading = true;
                    _crossfadeElapsedSeconds = 0;
                    _crossfadeProgress = 0;
                    if (_next.Gain != null) _next.Gain.Volume = 0f;
                    _next.WaitForSeek();
                    if (_next.VariSpeed != null) _next.VariSpeed.Speed = current.VariSpeed?.Speed ?? 1.0;
                    _next.Output.Play();

                    CrossfadeStarted?.Invoke(this, new CrossfadeStartedEventArgs
                    {
                        DurationSeconds = CrossfadeSeconds,
                    });
                }
            }
        }

        private void AdvanceCrossfade(Deck current)
        {
            if (_next?.Output == null)
            {
                var wasCrossfading = _isCrossfading;
                _isCrossfading = false;
                _crossfadeProgress = 0;
                if (wasCrossfading) CrossfadeEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            _crossfadeElapsedSeconds += TimerIntervalSeconds;
            var t = CrossfadeSeconds > 0 ? Math.Clamp(_crossfadeElapsedSeconds / CrossfadeSeconds, 0.0, 1.0) : 1.0;

            // Equal-power curve: constant perceived loudness through the overlap, unlike a
            // linear fade which dips in the middle.
            if (current.Gain != null) current.Gain.Volume = (float)(_masterVolumeFraction * current.LoudnessGain * Math.Cos(t * Math.PI / 2));
            if (_next.Gain != null) _next.Gain.Volume = (float)(_masterVolumeFraction * _next.LoudnessGain * Math.Sin(t * Math.PI / 2));

            _crossfadeProgress = t;
            CrossfadeProgressChanged?.Invoke(this, t);

            if (t >= 1.0)
                CompleteCrossfade();
        }

        /// <summary>Ends the crossfade: incoming deck at full level, then promoted.</summary>
        private void CompleteCrossfade()
        {
            _isCrossfading = false;
            _crossfadeElapsedSeconds = 0;
            _crossfadeProgress = 0;
            if (_next?.Gain != null) _next.Gain.Volume = _masterVolumeFraction * _next.LoudnessGain;
            if (_current != null) PromoteNextDeck(_current);
            CrossfadeEnded?.Invoke(this, EventArgs.Empty);
        }

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying => _current?.Output?.PlaybackState == PlaybackState.Playing;
        public long Length => (long)(_current?.AudioFile?.TotalTime.TotalMilliseconds ?? 0);
        public double Duration => _current?.AudioFile?.TotalTime.TotalSeconds ?? 0;
        public long Time => (long)(_current?.AudioFile?.CurrentTime.TotalMilliseconds ?? 0);

        public float Position
        {
            // Clamped: Media Foundation rounds FLAC lengths down to the second, so the raw ratio
            // can run a little past 1 at the very end of a track.
            get => (float)(_current?.AudioFile is { Length: > 0 } file ? Math.Clamp(file.Position / (double)file.Length, 0, 1) : 0);
            set
            {
                var deck = _current;
                if (deck?.AudioFile == null) return;

                if (!Singularity.Services.Audio.ExactSeek.NeedsDecodeForward(deck.FilePath))
                {
                    deck.AudioFile.Position = (long)(value * deck.AudioFile.Length);
                    deck.VariSpeed?.Reset(); // discard stale buffered samples from before the seek
                    return;
                }

                // FLAC/M4A: Media Foundation lands up to ~0.9 s off target, so seek exactly by
                // decoding forward — off the UI thread, with the output paused meanwhile. Rapid
                // seeks (dragging a seek bar) collapse into the latest target.
                lock (deck.SeekLock)
                {
                    deck.RequestedSeekSeconds = Math.Clamp(value, 0f, 1f) * deck.AudioFile.TotalTime.TotalSeconds;
                    if (deck.SeekWorkerRunning) return; // the running worker picks up the new target
                    deck.SeekWorkerRunning = true;
                }
                bool wasPlaying = deck.Output?.PlaybackState == PlaybackState.Playing;
                deck.SeekTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (wasPlaying) deck.Output?.Pause();
                        while (true)
                        {
                            double seconds;
                            lock (deck.SeekLock)
                            {
                                if (deck.RequestedSeekSeconds is not double next || deck.AudioFile == null)
                                {
                                    deck.SeekWorkerRunning = false;
                                    break;
                                }
                                seconds = next;
                                deck.RequestedSeekSeconds = null;
                            }
                            try
                            {
                                Singularity.Services.Audio.ExactSeek.Seek(deck.AudioFile, deck.FilePath, seconds);
                                deck.VariSpeed?.Reset();
                            }
                            catch (Exception ex)
                            {
                                Serilog.Log.Warning(ex, "[AudioPlayerService] Exact seek failed for {File}", deck.FilePath);
                            }
                        }
                    }
                    finally
                    {
                        lock (deck.SeekLock) deck.SeekWorkerRunning = false;
                        if (wasPlaying && ReferenceEquals(_current, deck)) deck.Output?.Play();
                    }
                });
            }
        }

        public int Volume
        {
            get => (int)(_masterVolumeFraction * 100);
            set
            {
                _masterVolumeFraction = Math.Clamp(value / 100f, 0f, 1f);
                // While crossfading, the fade envelope owns each deck's volume; the new
                // master level takes effect once the crossfade finishes (see AdvanceCrossfade).
                if (_current?.Gain != null && !_isCrossfading)
                {
                    _current.Gain.Volume = _masterVolumeFraction * _current.LoudnessGain;
                }
            }
        }

        public bool IsVisualizerActive { get; private set; }

        private int _visualizerAttachCount;

        /// <summary>
        /// Was never called from anywhere, which is why IsVisualizerActive stayed permanently
        /// false — the FFT/spectrum computation it gates ran unconditionally for every playing
        /// track regardless of whether a visualizer control was ever going to render it. Wired
        /// from VibeVisualizer/OrbitVisualizerCanvas's OnAttachedToVisualTree. Reference-counted
        /// so two simultaneously-visible visualizer instances don't have one's detach turn the
        /// flag off while the other is still showing.
        /// </summary>
        public void NotifyVisualizerAttached()
        {
            IsVisualizerActive = System.Threading.Interlocked.Increment(ref _visualizerAttachCount) > 0;
        }

        public void NotifyVisualizerDetached()
        {
            var count = System.Threading.Interlocked.Decrement(ref _visualizerAttachCount);
            if (count < 0)
            {
                // Defensive: an unmatched Detached call (shouldn't happen if every control pairs
                // Attached/Detached correctly) — clamp so a stray extra call can't push future
                // legitimate attach/detach pairs permanently out of sync.
                System.Threading.Interlocked.Exchange(ref _visualizerAttachCount, 0);
                count = 0;
            }
            IsVisualizerActive = count > 0;
        }

        public void Play(string filePath, double? trackLoudnessLufs = null) => OpenDevice(filePath, autoPlay: true, trackLoudnessLufs);

        /// <summary>
        /// Opens the file and initializes the output device without starting playback.
        /// Callers that just need a track "hot and ready" (e.g. Cue Forge loading a track
        /// before the user presses play) should use this instead of Play() — a
        /// Play()-then-immediately-Pause() sequence still lets a moment of real audio through
        /// the WASAPI buffer before the pause takes effect, which is audible as a brief blip.
        /// </summary>
        public void LoadWithoutPlaying(string filePath, double? trackLoudnessLufs = null) => OpenDevice(filePath, autoPlay: false, trackLoudnessLufs);

        /// <summary>
        /// Opens and initializes the output device for the track that will play next, without
        /// making any sound, so the transition when the current track ends can be a near-instant
        /// swap (gapless) or a timed overlap (crossfade) instead of a cold file-open that causes
        /// an audible gap. Call this as soon as the next track is known (e.g. right after the
        /// current one starts), well before playback is expected to reach it.
        /// </summary>
        public void PreloadNext(string filePath, double? trackLoudnessLufs = null)
        {
            if (_current == null) return;
            if (_nextFilePath == filePath && _next != null) return; // already preloaded

            CancelPreload();

            try
            {
                var deck = CreateDeck(filePath, trackLoudnessLufs);
                deck.Gain!.Volume = 0f;
                _next = deck;
                _nextFilePath = filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Preload failed for {filePath}: {ex.Message}");
                _next = null;
                _nextFilePath = null;
            }
        }


        /// <summary>Discards any preloaded next track (e.g. the queue changed before it was needed).</summary>
        public void CancelPreload()
        {
            _next?.Dispose();
            _next = null;
            _nextFilePath = null;
            _isCrossfading = false;
            _crossfadeElapsedSeconds = 0;
        }

        private void OpenDevice(string filePath, bool autoPlay, double? trackLoudnessLufs = null)
        {
            Stop();

            try
            {
                _current = CreateDeck(filePath, trackLoudnessLufs);
                _current.Gain!.Volume = _masterVolumeFraction * _current.LoudnessGain;
                if (autoPlay) _current.Output.Play();
                LengthChanged?.Invoke(this, (long)_current.AudioFile!.TotalTime.TotalMilliseconds);
                PausableChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Playback error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Builds an output device honoring the user's configured audio-output mode/device
        /// (Settings → Advanced → Audio Output — WaveOut/WASAPI-Shared/WASAPI-Exclusive/ASIO),
        /// falling back to the previous hardcoded WASAPI-Shared behavior if the configured
        /// device/driver fails to open (e.g. an ASIO driver that's since been uninstalled).
        /// </summary>
        private IWavePlayer CreateConfiguredOutputDevice(ISampleProvider source)
        {
            var mode = Enum.TryParse<AudioOutputMode>(_config.AudioOutputMode, out var parsed)
                ? parsed
                : AudioOutputMode.WasapiShared;
            var deviceName = _config.AudioOutputDeviceName;

            // Construction rarely fails — Init is where a busy/unsupported device throws, so each
            // attempt includes Init. Fallbacks: the chosen device in shared mode, then the Windows
            // default. (The old fallback went straight to the default device and only wrote to the
            // console, so a failing selection looked like "the setting does nothing".)
            var attempts = new List<(string Label, Func<IWavePlayer> Create)>
            {
                ($"{mode}/{deviceName ?? "default"}", () => AudioOutputProvider.CreateDevice(mode, deviceName)),
            };
            if (mode != AudioOutputMode.WasapiShared && deviceName != null)
                attempts.Add(($"WasapiShared/{deviceName}", () => AudioOutputProvider.CreateSharedDevice(deviceName, 50)));
            attempts.Add(("WasapiShared/Windows default", () => new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100)));

            Exception? last = null;
            foreach (var (label, create) in attempts)
            {
                IWavePlayer? output = null;
                try
                {
                    output = create();
                    output.Init(source);
                    if (last != null)
                        Serilog.Log.Warning(last, "[AudioPlayerService] Configured output failed; playing on {Fallback}", label);
                    return output;
                }
                catch (Exception ex)
                {
                    output?.Dispose();
                    last = ex;
                }
            }
            throw new InvalidOperationException("No audio output device could be opened.", last);
        }

        /// <summary>Opens <paramref name="deck"/>'s output on the configured device and wires its end-of-track handling.</summary>
        private IWavePlayer OpenDeckOutput(Deck deck)
        {
            var output = CreateConfiguredOutputDevice(deck.Metering!);
            // Never touch output.Volume. On NAudio's WasapiOut that property is the DEVICE's Windows
            // master volume (AudioEndpointVolume), not a per-stream level: "pinning" it to 1.0 here
            // set the user's system volume to 100% on every track change (reported 2026-09-29).
            // All real gain — master volume, crossfades, loudness — goes through deck.Gain.
            output.PlaybackStopped += (s, e) =>
            {
                if (!ReferenceEquals(s, deck.Output)) return; // an output replaced by a device switch
                if (!ReferenceEquals(_current, deck)) return; // stale event from a deck we've already advanced past
                if (_isCrossfading) return; // the crossfade timer owns this transition

                if (_next != null)
                {
                    PromoteNextDeck(deck);
                }
                else
                {
                    EndReached?.Invoke(this, EventArgs.Empty);
                }
            };
            return output;
        }

        /// <summary>
        /// Moves the playing (and preloaded) decks onto the output device now selected in Settings,
        /// keeping position and play/pause state. Before, a new device only applied to the next
        /// track that was opened — so changing it mid-song appeared to do nothing.
        /// </summary>
        public void ApplyOutputSettings()
        {
            foreach (var deck in new[] { _current, _next })
            {
                if (deck?.Output == null || deck.Metering == null) continue;
                try
                {
                    var old = deck.Output;
                    bool wasPlaying = old.PlaybackState == PlaybackState.Playing;
                    var fresh = OpenDeckOutput(deck);
                    deck.Output = fresh;           // old output's PlaybackStopped is now ignored
                    try { old.Stop(); } catch { /* already stopped */ }
                    old.Dispose();
                    if (wasPlaying) fresh.Play();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[AudioPlayerService] Switching output device failed for {File}", deck.FilePath);
                }
            }
        }

        /// <summary>
        /// Loudness normalization à la ReplayGain, using each track's already-analyzed integrated
        /// loudness instead of a separate scan pass. Deliberately attenuation-only (never boosts
        /// a quiet track above unity gain) — boosting risks clipping since there's no limiter in
        /// this signal chain, and every other player with this feature defaults the same way.
        /// </summary>
        private float ComputeLoudnessGain(double? trackLoudnessLufs)
        {
            if (!_config.LoudnessNormalizationEnabled || trackLoudnessLufs is not double lufs)
                return 1f;

            var gainDb = Math.Min(0.0, _config.LoudnessNormalizationTargetLufs - lufs);
            return (float)Math.Pow(10.0, gainDb / 20.0);
        }

        private Deck CreateDeck(string filePath, double? trackLoudnessLufs = null)
        {
            // PlayableAudio: files Windows can't decode (some FLAC/Opus/Ogg) play from an
            // ffmpeg-decoded WAV instead of failing to load. FilePath is what is actually read,
            // so ExactSeek knows whether a decode-forward seek is needed.
            var reader = Singularity.Services.Audio.PlayableAudio.Open(filePath, out var playablePath);
            var deck = new Deck
            {
                FilePath = playablePath,
                AudioFile = reader,
                LoudnessGain = ComputeLoudnessGain(trackLoudnessLufs)
            };

            // Set up channel and resampler for pitch (turntable style)
            var sampleChannel = new SampleChannel(deck.AudioFile, true);

            // 0. Vari-speed for pitch control: reads the channel at a variable rate via linear
            // interpolation, so speeding up/slowing down raises/lowers pitch just like a real
            // turntable/CDJ pitch fader (as opposed to tempo-only time-stretching).
            deck.VariSpeed = new VariSpeedSampleProvider(sampleChannel) { Speed = _pitch };

            // 0.75. In-process gain stage — see the Deck.Gain field doc for why this, and not
            // Output.Volume, is what master volume/crossfade/loudness-normalization drive.
            deck.Gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(deck.VariSpeed) { Volume = 1f };

            // 1. Intercept for FFT (Spectrum). Only forwarded upstream while this deck is the
            // active one, so a preloaded/promoted deck seamlessly takes over the visualizer.
            var fftProvider = new FftSampleProvider(deck.Gain, 2048, magnitudes =>
            {
                if (ReferenceEquals(_current, deck)) SpectrumChanged?.Invoke(this, magnitudes);
            }, isActive: () => IsVisualizerActive,
            onWaveform: samples =>
            {
                if (ReferenceEquals(_current, deck)) WaveformChanged?.Invoke(this, samples);
            });

            // 2. Wrap in Metering for VU
            deck.Metering = new MeteringSampleProvider(fftProvider);
            deck.Metering.StreamVolume += (s, e) =>
            {
                if (!ReferenceEquals(_current, deck)) return;

                // Throttle VU meter updates to reduce event marshalling overhead
                _vuMeterSkipCounter++;
                if (_vuMeterSkipCounter >= VU_METER_SKIP_FRAMES)
                {
                    _vuMeterSkipCounter = 0;
                    AudioLevelsChanged?.Invoke(this, new AudioLevelsEventArgs
                    {
                        Left = e.MaxSampleValues[0],
                        Right = e.MaxSampleValues.Length > 1 ? e.MaxSampleValues[1] : e.MaxSampleValues[0]
                    });
                }
            };

            deck.Output = OpenDeckOutput(deck);
            return deck;
        }

        /// <summary>Makes the preloaded deck the active one and disposes the deck that just finished.</summary>
        private void PromoteNextDeck(Deck finishedDeck)
        {
            var promoted = _next;
            _next = null;
            _nextFilePath = null;
            if (promoted?.Output == null)
            {
                return;
            }

            _current = promoted;
            if (promoted.Gain != null) promoted.Gain.Volume = _masterVolumeFraction * promoted.LoudnessGain;
            if (promoted.Output.PlaybackState != PlaybackState.Playing)
            {
                promoted.WaitForSeek();
                promoted.Output.Play();
            }

            LengthChanged?.Invoke(this, (long)(promoted.AudioFile?.TotalTime.TotalMilliseconds ?? 0));

            // Disposing the finished deck's WasapiOut synchronously, here, on the timer thread —
            // in the same call stack that just started/confirmed the newly-promoted deck's own
            // WasapiOut is playing — has been observed to silently kill the PROMOTED deck's
            // playback moments later: its PlaybackState keeps reporting Playing and its volume
            // stays at 1, but AudioFile.CurrentTime simply stops advancing forever (confirmed via
            // live logging — position frozen at the exact promotion timestamp, five-plus minutes
            // later, with no further PlaybackStopped/EndReached ever firing). Deck.Dispose() calls
            // Output.Stop() first, which blocks waiting for that WasapiOut's dedicated event-sync
            // thread to exit — doing that immediately adjacent to another WasapiOut instance
            // starting up on the timer thread is exactly the kind of ordering NAudio/WASAPI is
            // fragile about. Deferring disposal to a background thread, decoupled from this call
            // stack, avoids whatever race that ordering was hitting.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    finishedDeck.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioPlayerService] Deferred disposal of finished deck failed: {ex.Message}");
                }
            });

            TrackAdvanced?.Invoke(this, EventArgs.Empty);
        }

        // Custom FFT Provider (Inline for simplicity or could be moved)
        /// <summary>
        /// Taps the playing signal for the visualizers: a mono downmix, windowed and FFT'd off the
        /// audio thread. The downmix matters — this used to FFT the raw interleaved stereo stream
        /// (L,R,L,R…), which folds the two channels into one sequence and scrambles the frequency
        /// axis every visualizer reads. Also hands out the time-domain block (oscilloscope preset).
        /// </summary>
        private class FftSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _fftSize;
            private readonly int _channels;
            private readonly Action<float[]> _onFftCalculated;
            private readonly Action<float[]>? _onWaveform;
            private readonly float[] _buffer;
            private readonly float[] _processingBuffer;
            private readonly System.Numerics.Complex[] _complexBuffer;
            private readonly float[] _window;
            private int _pos;
            private int _fftBusy;
            private float _frameSum;
            private int _frameChannel;
            private readonly Func<bool> _isActive;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public FftSampleProvider(ISampleProvider source, int fftSize, Action<float[]> onFftCalculated, Func<bool>? isActive = null, Action<float[]>? onWaveform = null)
            {
                _source = source;
                _fftSize = fftSize;
                _channels = Math.Max(1, source.WaveFormat.Channels);
                _onFftCalculated = onFftCalculated;
                _onWaveform = onWaveform;
                _buffer = new float[fftSize];
                _processingBuffer = new float[fftSize];
                _complexBuffer = new System.Numerics.Complex[fftSize];
                _window = new float[fftSize];
                for (int i = 0; i < fftSize; i++)
                    _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)))); // Hann
                _isActive = isActive ?? (() => true);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);

                // No visualizer attached — skip the work, and restart from a clean window later.
                if (!_isActive())
                {
                    _pos = 0;
                    _frameSum = 0;
                    _frameChannel = 0;
                    return read;
                }

                for (int i = 0; i < read; i++)
                {
                    // Frames can straddle Read calls, so the channel position carries over.
                    _frameSum += buffer[offset + i];
                    if (++_frameChannel < _channels) continue;

                    _buffer[_pos++] = _frameSum / _channels;
                    _frameSum = 0;
                    _frameChannel = 0;

                    if (_pos >= _fftSize)
                    {
                        // Skip this block if the previous FFT is still running.
                        if (System.Threading.Interlocked.CompareExchange(ref _fftBusy, 1, 0) == 0)
                        {
                            Array.Copy(_buffer, _processingBuffer, _fftSize);
                            _ = System.Threading.Tasks.Task.Run(PerformFft);
                        }
                        // 50% overlap: keep the second half as the start of the next window, so a
                        // new spectrum arrives every ~23 ms instead of every ~46 ms.
                        int half = _fftSize / 2;
                        Array.Copy(_buffer, half, _buffer, 0, half);
                        _pos = half;
                    }
                }

                return read;
            }

            private void PerformFft()
            {
                try
                {
                    _onWaveform?.Invoke((float[])_processingBuffer.Clone());

                    for (int i = 0; i < _fftSize; i++)
                        _complexBuffer[i] = new System.Numerics.Complex(_processingBuffer[i] * _window[i], 0);

                    MathNet.Numerics.IntegralTransforms.Fourier.Forward(_complexBuffer, MathNet.Numerics.IntegralTransforms.FourierOptions.NoScaling);

                    var magnitude = new float[_fftSize / 2];
                    for (int i = 0; i < magnitude.Length; i++)
                        magnitude[i] = (float)_complexBuffer[i].Magnitude;

                    _onFftCalculated(magnitude);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "Visualizer FFT block failed");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _fftBusy, 0);
                }
            }
        }

        /// <summary>
        /// Reads its source at a variable rate using linear interpolation between samples,
        /// producing a turntable/CDJ-style varispeed effect: <see cref="Speed"/> above 1.0 plays
        /// faster and higher-pitched, below 1.0 slower and lower-pitched — speed and pitch always
        /// move together, exactly like physically speeding up or slowing down a record. Speed can
        /// be changed at any time (e.g. from a live slider) with no audible discontinuity.
        /// </summary>
        internal class VariSpeedSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _sourceBuffer = Array.Empty<float>();
            private int _sourceFrameCount; // valid frames currently held in _sourceBuffer
            private double _readPosition;  // fractional frame index into _sourceBuffer
            private bool _sourceExhausted;

            /// <summary>1.0 = normal speed/pitch. Typical DJ pitch-fader range is ~0.92–1.08.</summary>
            public double Speed { get; set; } = 1.0;

            public WaveFormat WaveFormat => _source.WaveFormat;

            public VariSpeedSampleProvider(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
            }

            /// <summary>Discards buffered samples so the next Read() starts fresh from wherever
            /// the underlying source now is. Must be called after seeking the source directly
            /// (e.g. AudioFileReader.Position), otherwise stale buffered samples from the old
            /// position would play briefly before catching up.</summary>
            public void Reset()
            {
                _sourceFrameCount = 0;
                _readPosition = 0;
                _sourceExhausted = false;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                var speed = Speed <= 0 ? 1.0 : Speed;
                int framesRequested = count / _channels;
                int framesWritten = 0;

                while (framesWritten < framesRequested)
                {
                    int baseFrame = (int)_readPosition;
                    if (baseFrame + 1 >= _sourceFrameCount && !RefillBuffer(speed, framesRequested - framesWritten))
                    {
                        break; // source exhausted — return what we've produced so far
                    }

                    baseFrame = (int)_readPosition;
                    double frac = _readPosition - baseFrame;

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        float s0 = _sourceBuffer[baseFrame * _channels + ch];
                        float s1 = _sourceBuffer[(baseFrame + 1) * _channels + ch];
                        buffer[offset + framesWritten * _channels + ch] = (float)(s0 + (s1 - s0) * frac);
                    }

                    framesWritten++;
                    _readPosition += speed;
                }

                return framesWritten * _channels;
            }

            /// <summary>Drops already-consumed frames, then tops up the buffer from the source.
            /// Returns false once the source has no more data and the buffer can't satisfy
            /// another interpolated frame.</summary>
            private bool RefillBuffer(double speed, int framesStillNeeded)
            {
                int consumedWholeFrames = Math.Min((int)_readPosition, _sourceFrameCount);
                if (consumedWholeFrames > 0)
                {
                    int keepFrames = _sourceFrameCount - consumedWholeFrames;
                    if (keepFrames > 0)
                    {
                        Array.Copy(_sourceBuffer, consumedWholeFrames * _channels, _sourceBuffer, 0, keepFrames * _channels);
                    }
                    _sourceFrameCount = keepFrames;
                    _readPosition -= consumedWholeFrames;
                }

                if (_sourceExhausted)
                {
                    return (int)_readPosition + 1 < _sourceFrameCount;
                }

                int framesToRead = Math.Max(256, (int)Math.Ceiling(framesStillNeeded * speed) + 8);
                int requiredCapacityFrames = _sourceFrameCount + framesToRead;
                if (_sourceBuffer.Length < requiredCapacityFrames * _channels)
                {
                    Array.Resize(ref _sourceBuffer, requiredCapacityFrames * _channels);
                }

                int samplesRead = _source.Read(_sourceBuffer, _sourceFrameCount * _channels, framesToRead * _channels);
                int framesRead = samplesRead / _channels;
                _sourceFrameCount += framesRead;
                if (framesRead == 0) _sourceExhausted = true;

                return (int)_readPosition + 1 < _sourceFrameCount;
            }
        }

        public void Pause()
        {
            if (_current?.Output == null) return;

            bool wasPlaying = _current.Output.PlaybackState == PlaybackState.Playing;
            if (wasPlaying) _current.Output.Pause();
            else if (_current.Output.PlaybackState == PlaybackState.Paused) _current.Output.Play();

            // Keep a preloaded/crossfading deck in lockstep so it doesn't keep playing silently
            // (or fail to resume) independently of the current one.
            if (_next?.Output != null)
            {
                if (wasPlaying && _next.Output.PlaybackState == PlaybackState.Playing) _next.Output.Pause();
                else if (!wasPlaying && _next.Output.PlaybackState == PlaybackState.Paused) _next.Output.Play();
            }

            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _current?.Dispose();
            _current = null;
            CancelPreload();
            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
