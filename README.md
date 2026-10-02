# Singularity

**An all-in-one karaoke system: find it, download it, vet it, turn it into a sing-along, and put it on the big screen.**

Singularity started as a fork of [ORBIT Pure](https://github.com/MeshDigital/Orbit-pure) (`0.9.3-alpha`). It keeps ORBIT's
music-acquisition and library backend; ORBIT's DJ tooling (cue authoring, transitions, decks, Rekordbox/Serato
export, similarity and genre/mood models) has been removed.

## What's here today

- **Soulseek**: login and connection lifecycle, search with ranking and quality vetting, hedged download
  orchestration, peer reliability tracking, file sharing, chat, rooms and user browsing
- **Imports**: Spotify playlists and Liked Songs, CSV and pasted tracklists, with automatic search-and-download
- **Vetting**: fake-lossless (spectral) detection, bitrate/format checks, corruption scanning, duplicate cleanup
- **Library and playlists**: playlists and folders, smart playlists/crates, tagging, bulk rename/move, artwork and
  playlist mosaics, MusicBrainz lookups, watch folders, library health, M3U export
- **Playback**: WASAPI/ASIO output, gapless playback with optional crossfade, loudness normalisation, exact seeking
- **Karaoke building blocks** (not wired into a UI yet): Demucs stem separation (ONNX), FFmpeg decoding to PCM,
  waveform extraction, and key detection via the Essentia CLI

## What Singularity adds (planned)

- **Karaoke track builder**: vocal removal via stems, lyric fetching + word-level alignment, pitch-line extraction
- **UltraStar export** (`.txt` + audio + video/cover) and compatibility with UltraStar Deluxe / Vocaluxe / Performous
- **YouTube video** sourcing for background/music videos, synced to the track
- **Performance mode**: projector / second-screen output, singer queue, scoring

## Getting started

Requirements: Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/MeshDigital/Singularity.git
cd Singularity

dotnet build Singularity.sln
dotnet run --project Singularity.csproj
dotnet test Tests/Singularity.Tests/Singularity.Tests.csproj
```

Optional runtime tools:

- **Demucs model** (`demucs-4s.onnx`, for stem separation) is not in git. Put it in `Tools/Essentia/models/` or
  `%APPDATA%\Singularity\Models\`; `pwsh ./Tools/fetch-models.ps1` copies it from a sibling ORBIT-Pure checkout.
- **Essentia** (`essentia_streaming_extractor_music.exe`, for key detection) is not bundled; it must be on disk
  for key detection to return results.
- **FFmpeg** on `PATH`, for decoding formats Windows can't play natively and for analysis input.

## Data locations

Singularity keeps its own data and never touches an ORBIT install on the same machine:

| What | Where |
|---|---|
| Config, library DB, caches, credentials | `%APPDATA%\Singularity\` |
| Logs, playback cache | `%LOCALAPPDATA%\Singularity\` |
| Demucs model (user copy) | `%APPDATA%\Singularity\Models\` |

Set `SINGULARITY_DB_PATH` to point the library database somewhere else (the test suite does this automatically).

## Codebase notes

- ORBIT's original architecture docs, deep-dives and agent notes are kept for reference in
  [`DOCS/orbit-heritage/`](DOCS/orbit-heritage/). They describe ORBIT, including features Singularity removed.
- Databases created by earlier builds keep ORBIT's DJ tables (cue points, transitions, set lists, …); nothing
  reads them any more and new databases don't get them from the schema patches.
