# Singularity architecture

How the karaoke side of Singularity is put together. ORBIT's engine (Soulseek, imports, downloads, library, playback)
is described in [orbit-heritage/](orbit-heritage/); this document covers what Singularity adds and where it hooks in.

## Projects

```
Singularity.Contracts      shared formats, no package dependencies
  ├─ UltraStar/            song.txt reader and writer (solo, duet, legacy P3, encodings), chart builder
  ├─ Song/                 song package layout and metadata.json
  ├─ Quality/              quality grade, video-gap consensus
  ├─ Inference/            worker protocol (JSONL commands and events), mirrored in Python
  ├─ Companion/            phone companion messages (protocol only; the app isn't built yet)
  └─ Fixtures/             shared test fixtures, parsed by both the C# and Python tests

Singularity.Karaoke        the game core, no UI
  ├─ Audio/                microphone blocks and channel routing
  ├─ Calibration/          microphone latency measurement (click test)
  ├─ Companion/            clock sync for the future phone companion
  ├─ Pitch/                MPM pitch detector, pitch stream
  ├─ Scoring/              UltraStar scoring, difficulty, line ratings
  ├─ Library/              song scanner, version grouping (SongClusters), medley finder
  ├─ Display/              note lane layout, lyrics timeline
  └─ Sync/                 video sync, chart sync

Singularity (app)          Avalonia UI + ORBIT's services
  ├─ Services/Karaoke/     audio engine, microphones, stems, stage window, library, worker
  │   ├─ Ingest/           the song pipeline (queue, packager, adapters, ORBIT glue)
  │   └─ Usdb/             USDB client and community charts
  ├─ ViewModels/Karaoke/   song select, sing, add songs, downloads, settings, projector browse
  └─ Views/Avalonia/Karaoke/

inference/                 Python AI worker: Demucs, faster-whisper, MMS forced aligner, SwiftF0, librosa
Tools/ChartBench/          measures AI charts against human ones
```

## The song pipeline

A song gets from a link to the library in these steps:

1. **Request.** `KaraokeIngestService.ImportAsync` takes a Spotify link or pasted text.
   - A single song is queued as a loose library entry (`DownloadManager.QueueTracks` with no playlist), like a Search
     download.
   - Albums, playlists and lists go through ORBIT's `ImportOrchestrator.SilentImportWithResultAsync`, which reports
     the job ids and which tracks were queued or already downloaded.
2. **Download.** ORBIT searches Soulseek, ranks candidates and downloads. `KaraokeIngestService` follows
   `TrackStateChangedEvent`s for the tracks it asked for. With *every download becomes a karaoke song* on, it also
   takes any other completed download whose song the collection doesn't have yet. Pending songs are saved to
   `ingest-pending.json`, so a restart resumes them.
3. **Queue.** `IngestQueue` builds one song at a time, since the AI uses the GPU. It is held while someone sings;
   the song being built is stopped and starts again afterwards.
4. **Build.** `KaraokePackager.BuildAsync` works in a private staging folder:
   - copies the audio and checks its length against the catalogue's;
   - looks for a **community chart** (below). It separates the stems and places the chart on the vocals. If the chart
     doesn't fit, it falls back to the AI and reuses the stems;
   - otherwise fetches **lyrics** from LRCLIB (an outage counts as no lyrics) and runs the **AI worker** for stems,
     word alignment, pitch and tempo, then builds the chart (`UltraStarChartBuilder`);
   - meanwhile downloads the **music video** (the chart's own YouTube id first, else a search) and **syncs** it
     (below);
   - downloads the cover, computes the **quality grade** and writes `song.txt` and `metadata.json`.
5. **Publish.** The finished files are gathered in `{output}\.incoming\{id}` (the song scanner skips dot-folders) and
   renamed into place in one step. A package of the same track is replaced; another song of the same name is kept
   and the new one is numbered.

A song package is an ordinary UltraStar folder:

```
The Killers - Mr. Brightside/
  song.txt            #AUDIO/#MP3, #VOCALS, #INSTRUMENTAL, #VIDEO, #VIDEOGAP, #COVER, #CREATOR
  audio.flac          the downloaded master, unchanged
  vocals.wav          separated stems (Demucs htdemucs_ft)
  instrumental.wav
  video.mp4           the music video without its sound (when found)
  cover.jpg
  metadata.json       track id, ISRC, timing, quality grade, provenance
```

`#CREATOR` is "Singularity AI" for AI charts and the chart author's name for community charts; that's how the library
tells the two apart.

## Community charts (USDB)

`UsdbClient` logs in with the user's own account (stored with Windows data protection), keeps the session, spaces
requests 1.5 s apart and parses USDB's HTML. Its parsers follow UltraStar-CLI's (MIT) and were checked against real
pages. `UsdbCommunityCharts` uses a hit only when:

- its artist and title reduce to the same song as the request, by the rules that group versions;
- it has the same version note ("Alternate Version" is another arrangement);
- it has at least **3 stars**;
- it parses with at least 30 sung notes.

A USDB chart is timed against its author's audio, not our download. **`ChartSync`** places it on the download by
comparing when the chart has notes sounding with when the separated vocals are loud. It uses nine 20 s windows, the
peak-and-consensus engine described under video sync, and 60 ms agreement; three quarters of the windows must lie on
one line.

- Tested on 89 human charts against their own vocals: 63 placed within 50 ms. Most others sat 50–100 ms off,
  consistently: charters place notes on the vowel, while the vocal energy starts at the consonant.
- None of 89 was accepted against another song's vocals.

Loudness can't tell whether the notes are right, so **`ChartPitchCheck`** checks them next: the vocals go through the
microphone pitch detector, and it counts how much of the singing lands on the chart's note at that moment (within half
a semitone, any octave). Charts that fit put 48–70% there. It also searches ±10 s and all 12 transpositions; a chart
that doesn't fit but clearly does after a correction is corrected, and otherwise an AI chart is made. It caught a
placed chart that sat 3.6 s late (6% on its notes, 36% once moved).
- Live, Mr. Brightside's USDB chart landed 36 ms from where the AI, working independently from the same file, put the
  first syllable.

## AI worker

`inference/` is a Python 3.11 process that the app talks to over newline-delimited JSON on stdin and stdout
(`InferenceWorkerHost`). The protocol lives in `Singularity.Contracts/Inference` and is mirrored in Pydantic; both test
suites parse the same fixtures. Stages run one at a time and each frees its model, so 8 GB of VRAM is enough.

`KaraokeWorker` is the one worker that vocal removal and song building share:

- it runs below normal priority;
- a stopped task is killed after one second;
- the process is replaced every 25 songs, because its resident memory creeps up by about 0.6 GB over 50 songs.

## Video

- **Finding.** `YtDlpVideoFinder` takes the first of the top five YouTube results for "artist - title official music
  video" whose length fits the song, as mp4 up to 1080p.
- **Syncing.** `VideoSync` reduces both soundtracks to onset envelopes (the rise in log energy per 10 ms) and finds
  the master's 8 s windows at 10–90% of the song in the video by normalised cross-correlation within ±60 s.
  - Each window keeps its strongest peaks, and the offset most windows support wins. This stops a repeated chorus
    from fooling a window.
  - The offsets must fit one gently sloped line: video speed drift up to 100 ms per minute is allowed, and the gap is
    taken mid-song. One stray window is forgiven when four agree.
  - On 18 collection videos with sound, 10 synced; the rest had different arrangements or unrelated soundtracks.
- **Showing.** A synced video plays behind the notes at `#VIDEOGAP`. An unsynced one is kept as a backdrop at 38%
  with a vignette, and `metadata.json` marks it so. Without a video, the cover is shown blurred in a glow of its own
  colour.
- **Decoding.** `VideoFrameSource` pipes raw BGRA frames from ffmpeg into a bitmap drawn in the scene. There is no
  native video window, which would paint over the notes. `VideoSurface` uses the same source outside the stage (Now
  Playing, the preview panel, the projector carousel), and reopens the video when the audio jumps.

## Quality grade

`QualityScoring` combines:

Q = 0.25·audio + 0.25·lyrics + 0.20·pitch + 0.15·video + 0.15·metadata

and grades A+ at 0.80 or more, A at 0.68, B at 0.50, below that "needs checking".

The lyric and pitch parts are the AI's raw confidences passed through sigmoids fitted on 50 human-charted songs. Raw
alignment confidence has a median of 0.18 even for charts that match the human one about 70% of the time, so used as
they were, every AI chart would need checking. A community chart counts as fully confident. With the fit, median
accuracy against the human chart is 0.77 for A, 0.70 for B and 0.61 for "needs checking".

## Singing

- **Clock.** The audio device's played-sample position (WASAPI) is the only clock. Notes, lyrics, video and scoring
  are all derived from it each frame.
- **Microphones.** `MicrophoneCapture` opens each device once and hands out raw blocks. `MicAssignment` routes a
  device or one channel of it (two-mic adapters) to a player.
- **Pitch.** `PitchDetector` uses MPM: 2048-sample frames at 48 kHz with a 10 ms hop, octave-independent, with a
  silence gate at −55 dBFS.
- **Scoring.** `SingScorer`: 9,000 points over the scored beats (golden beats count double) plus a 1,000-point line
  bonus. A beat earns credit by how close it was sung: full within 20 cents, falling off quadratically to nothing at
  the difficulty's tolerance (Easy 1.75, Medium 1.0, Hard 0.65 semitones); gaps up to half a beat are forgiven, and
  the first 80 ms of a note forgive the attack. Each judged beat reports its offset, which the stage shows as sharp
  or flat. With a `ReferencePitch` (the original singer's pitch every 10 ms, read from the separated vocals in the
  background when a song starts, median-filtered) a sample earns the better of its credit against the note and
  against the singer, where the singer is within 2 semitones of the note. Measured on 8 songs with the artists' own
  vocals: +700 to +1,500 on AI charts; a singer 1.5 semitones off gains nothing.
- **Timing.** The first sample of a note with half credit or better is its start; `SingScorer.OnTime` gives 1 up to
  120 ms late, 0 from 400 ms. A line's bonus keeps `1 - 0.25 x (1 - timing)` of its share; note points don't change.
  On the singers' own vocals timing is 97-100%; 150 ms late costs about 20-30 points.
- **Chart note check.** `ChartNoteCheck` compares each note with the singer's median pitch over it: agreeing within
  half a semitone, steadily elsewhere (corrected in AI charts at import), lines where most notes disagree, and a
  mismatch below 40% agreement. The result is `metadata.json`'s `check`; song select shows it, and the Sing page runs
  it again from the reference curve for every song with separated vocals.
- **Note lane.** `NoteLaneLayout` keeps one pitch scale per song (its widest line plus a margin) and gives each line
  a centre the stage glides to. The singer's pitch is folded to the octave of the note being sung and steadied by
  `PitchSmoother` (3-reading median, EMA) for display only.
- **Party queue and phones.** `PartyQueue` (Singularity.Karaoke) holds who sings what next (3 per singer, kept in
  `party-queue.json`). `PhoneRemoteServer` serves the phone page and a small JSON API with EmbedIO's own listener
  (no URL reservations or admin rights): only while switched on, only to private/loopback addresses, and every API
  call needs the key from the QR code (new per start; constant-time compare; 4 KB bodies). The page puts song text in
  with `textContent` only.
- **Song map.** `SongMap` holds each voice's stretches of notes and, found in the background from the separated
  vocals (50 ms loudness, within 20 dB of their loud end, at least 1.2 s, clear of every voice's notes), the singing
  the chart doesn't score. The stage draws it as the progress bar and uses it for the lane's labels.
- **Drawing.** `SingStage` draws each frame from a `StageSnapshot`, which is taken in the animation-frame callback
  before the render pass. Bound properties must not change during rendering.
- **Stems.** `StemStore` caches stems for the user's own songs, and `StemBatchQueue` separates a whole collection in
  the background (held while singing, resumable, keeps the PC awake). `SingAudioEngine` mixes instrumental and
  vocals at the chosen level.
- **Projector.** `StageScreenService` opens a borderless full-screen window on the chosen display, identified by its
  desktop position. Between songs it shows `StageBrowseView`; while singing, the stage.

## Song library

- **Scanning.** `SongScanner` finds `.txt` files under the song folders, resolves files case-insensitively and falls
  back to UltraStar's cover naming conventions. It skips dot-folders.
- **Grouping.** `SongClusters` groups charts of the same song by a key built from the primary artist and the base
  title: case, accents, featured artists, "The" and version notes are ignored. Versions are ordered human duet (with
  two singers), human solo, then AI charts by grade.
- **Collection.** `KaraokeLibrary` holds the configured folders (`D:\KARAOKE\songs` by default when it exists) plus
  the import folder. It answers "does the collection have this song", used to skip duplicates, and "which video
  goes with this audio file", for Now Playing (only an exact file or a same-size copy, so the gap fits).
