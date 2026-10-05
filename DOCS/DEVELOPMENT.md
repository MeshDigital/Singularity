# Developing Singularity

## Build and test

```powershell
dotnet build Singularity.sln
dotnet run --project Singularity.csproj
dotnet test Tests/Singularity.Tests/Singularity.Tests.csproj
```

The tests (about 1,100) run in a few seconds. They never touch the real library database: `SINGULARITY_DB_PATH`
points each run at a temporary one. Karaoke tests use synthetic audio (generated "music", charts and vocals),
temporary folders and fakes for the AI worker, LRCLIB, ffmpeg, yt-dlp and USDB, so they need neither a GPU nor the
network.

A running Singularity locks `bin\Debug`. To build or test while it runs, use another output folder
(`dotnet test … -o <folder>`). In that case the few architecture tests that look for the source tree from the output
folder fail; they pass in a normal build.

The Python worker has its own tests:

```powershell
cd inference
.venv\Scripts\python -m pytest
```

## Development flags

| Flag | Does |
|---|---|
| `--offline` | No Soulseek connection and no startup network checks (also `SINGULARITY_OFFLINE=1`) |
| `--open-page <key>` | Opens a page at startup: `Karaoke` (Sing), `Sing`, `MicSetup`, `AddSongs`, `Projects` (Downloads), `Settings`, `Library`, `NowPlaying`, `Search`, `Home`, `Users` |
| `--sing "<song folder>"` | Starts singing that song at startup |
| `--sing-start <seconds>` | …that many seconds in |
| `--demo-singer` | The song's separated original vocals sing instead of the microphones: try the stage, scoring and results without singing (no high scores are saved) |
| `--players 2` | Adds a second singer on player 1's microphone, for this run |
| `--stage "x,y"` | Shows the stage on the display at that desktop position, for this run |
| `--stage window` | Shows the stage in an ordinary window that doesn't take the focus (preview the projector on one screen) |
| `--import "<link or list>"` | Opens Add songs and adds the link, as pasting it would |

Quote paths with spaces. In PowerShell, pass the whole argument string to `Start-Process`, or the path is split.

## Environment variables

| Variable | Does |
|---|---|
| `SINGULARITY_SONGS_DIR` | Extra song folders for this run, separated by `;` |
| `SINGULARITY_DB_PATH` | Use another library database |
| `SINGULARITY_INFERENCE_PYTHON` | The Python of the inference worker (when it isn't found next to the app) |
| `SINGULARITY_INFERENCE_DIR` | The worker's folder |
| `SINGULARITY_INFERENCE_BACKEND=fake` | The worker's deterministic stand-in for the models (tests) |
| `SINGULARITY_MODEL_DIR` | Where the worker keeps model weights |
| `SINGULARITY_WHISPER_MODEL` | Whisper model size (`medium` for about 1.5 GB) |
| `SINGULARITY_FFMPEG` | The ffmpeg the worker uses (the app passes its own) |
| `SINGULARITY_WORKER_PRIORITY=below_normal` | Run the worker below normal priority (the app sets this) |

## ChartBench

`Tools/ChartBench` measures AI charts against human ones. It runs songs from an UltraStar collection through LRCLIB
and the worker and compares each generated chart with the song's own `song.txt`:

```powershell
$env:SINGULARITY_INFERENCE_PYTHON = "<repo>\inference\.venv\Scripts\python.exe"
dotnet run --project Tools/ChartBench -c Release -- "D:\KARAOKE\songs" "<output folder>" --sample 50
```

It reports per song and as medians:

- recall and precision of note starts within 50 and 100 ms;
- coverage;
- pitch class and within-a-semitone accuracy, also after correcting for a reference in another key;
- the raw lyric and pitch confidences that the quality grade is calibrated on.

Stems are cached in the output folder, so a re-run after a pipeline change only redoes the cheap stages. The
collection is only read; the output folder must be outside it.

The sync methods were checked the same way: `VideoSync` on the collection's videos with sound against their human
`#VIDEOGAP`, and `ChartSync` on the cached vocal stems against the songs' own charts. Results are in
[ARCHITECTURE.md](ARCHITECTURE.md).

## Conventions

- Song folders the user collected are read only. Anything Singularity makes goes to its own folders, built in staging
  and moved into place in one step.
- Logins (Soulseek, Spotify, USDB) are stored with Windows data protection, never in `config.ini` or logs.
- The game core (`Singularity.Karaoke`, `Singularity.Contracts`) has no UI dependencies and is tested on its own.
- UI state that a frame needs is taken before the render pass. Changing bound properties while Avalonia renders
  throws.
- Commit per step, with a message that says what changed and why.
