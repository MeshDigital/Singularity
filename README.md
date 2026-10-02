# Singularity

**An all-in-one karaoke system: find it, download it, vet it, turn it into a sing-along, and put it on the big screen.**

Singularity is built on top of [ORBIT Pure](https://github.com/MeshDigital/Orbit-pure) (forked at `0.9.3-alpha`) and keeps its
whole music-acquisition backend, then adds AI layers that turn any song into a playable karaoke track.

## What it inherits from ORBIT

- **Soulseek**: authentication, ad-hoc search, hedged search + download orchestration, peer/quality vetting
- **Spotify playlist import** and tracklist-paste import, with automatic search-and-download of every track
- **Auto-vetting**: fake-lossless (FFT) detection, bitrate/format checks, corruption scanning, duplicate cleanup
- **Library manager**: tagging, bulk rename/move, watch folders, health dashboard
- **Audio engine**: WASAPI/ASIO output, loudness normalisation, exact seeking
- **AI/DSP**: Demucs stem separation (ONNX), CREPE pitch model, BPM/key/structure analysis, genre/mood classifiers

## What Singularity adds (planned)

- **Karaoke track builder**: vocal removal via stems, lyric fetching + word-level alignment, pitch-line extraction
- **UltraStar export** (`.txt` + audio + video/cover) and compatibility with UltraStar Deluxe / Vocaluxe / Performous
- **YouTube video** sourcing for background/music videos, synced to the track
- **Performance mode**: projector / second-screen output, singer queue, scoring

The full roadmap lives in [`DOCS/`](DOCS/).

## Getting started

Requirements: Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/MeshDigital/Singularity.git
cd Singularity

# AI models (~770 MB) are not in git. This copies them from a sibling ORBIT-Pure checkout:
pwsh ./Tools/fetch-models.ps1

dotnet build Singularity.sln
dotnet run --project Singularity.csproj
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj
```

## Data locations

Singularity keeps its own data and never touches an ORBIT install on the same machine:

| What | Where |
|---|---|
| Config, library DB, caches, credentials | `%APPDATA%\Singularity\` |
| Logs, playback cache | `%LOCALAPPDATA%\Singularity\` |
| Demucs model downloads (shared with ORBIT on purpose) | `%APPDATA%\Antigravity\Models\` |

Set `SINGULARITY_DB_PATH` to point the library database somewhere else (the test suite does this automatically).

## Codebase notes

- The C# root namespace is still `SLSKDONET` and the test project is still `SLSKDONET.Tests`. Renaming them is mechanical and can happen whenever it's convenient.
- ORBIT's original architecture docs, deep-dives and agent notes are kept for reference in [`DOCS/orbit-heritage/`](DOCS/orbit-heritage/).
