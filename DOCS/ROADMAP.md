# Singularity roadmap

Singularity is a karaoke game that does everything UltraStar does, with three additions: it finds and downloads music itself (the Soulseek stack inherited from ORBIT), it creates charts for songs that don't have one (an AI worker modelled on UltraStarKaraokeMaker), and it supports multiplayer across a second monitor and phones on the same network.

Singularity reads and writes standard UltraStar `song.txt` files (format 1.1.0), so existing song collections work unchanged, and other players can open songs that Singularity creates.

## Phases

| # | Phase | Delivers | Status |
|---|---|---|---|
| 0 | Contracts | `Singularity.Contracts`: song package metadata, the quality rubric, the 3-window video-gap rule, the `song.txt` reader/writer (solo, duet, legacy relative), conversion from AI output to a chart, and the worker JSONL protocol. Includes the Pydantic mirror in `inference/` and shared fixtures. | Done |
| 1 | Inference worker | Python worker: Demucs stems, forced alignment of LRCLIB lyrics (Whisper only when no lyrics are found), SwiftF0 pitch, tempo. A C# host that launches it, reads events and supports cancellation. | Next |
| 2 | Ingestion | Track request → existing Soulseek search/download → fpcalc + AcoustID check → lyrics → worker → chart → song package. Video fetch with 3-window correlation. Quality tier shown in the library. | |
| 3 | Sing core | Song player with a single audio clock and video rendered *inside* the Skia scene (avoids LibVLC's native-window airspace problem). Mic capture and pitch detection per player, a note lane, a lyric line, UltraStar scoring. 1–6 players, duets. | |
| 4 | UltraStar parity | Everything in the checklist below. | |
| 5 | Second screen | A second monitor as the audience/score view, plus a phone companion app (mic over Wi-Fi, remote control, QR pairing) with per-device latency calibration. | |
| 6 | Editor | Correction editor (piano roll, BPM/GAP tapping, golden/freestyle marking) for `review_required` songs and any other chart. | |

## UltraStar parity checklist

Sources for this list are UltraStar Deluxe and UltraStar Play.

**Songs and library**
- [ ] Load existing UltraStar song folders (encoding detection, `#RELATIVE`, comma decimals, `#MP3`/`#AUDIO`): parser done in phase 0
- [ ] Duets (P1/P2): parser done in phase 0
- [ ] Song select: covers, preview playback from `#PREVIEWSTART`, search
- [ ] Sort and group by artist, title, edition, genre, language, year, folder
- [ ] Playlists (reuse ORBIT playlists)
- [ ] Jukebox mode (play with lyrics, no singing)

**Singing**
- [ ] 1–6 players, each with their own mic/channel, colour and name
- [ ] Difficulty (pitch tolerance easy/medium/hard)
- [ ] Note types: normal, golden (double score), freestyle (unscored), rap (rhythm only, ignores pitch)
- [ ] Scoring out of 10,000: notes, golden notes and line bonus, plus rating text per line
- [ ] Octave-independent pitch matching (singers can sing an octave off)
- [ ] `#START`/`#END`, `#VIDEOGAP`, background image when there is no video
- [ ] Medley mode (`#MEDLEYSTARTBEAT`/`#MEDLEYENDBEAT`, automatic medley detection when they're missing)
- [ ] Pause, restart, skip intro

**Party and results**
- [ ] Party mode: teams, rounds and modes such as duel, blind (hidden notes) and until-5000
- [ ] Highscores per song and difficulty, statistics
- [ ] Webcam background (optional)

**Setup**
- [ ] Mic setup with per-input channel selection, gain and a latency test
- [ ] Keyboard, gamepad and remote navigation
- [ ] Themes and translations

## Second screen and multiplayer

1. **Second monitor.** A second window shows lyrics and notes to the audience or a second group of singers, while the main window keeps scores and controls. Both windows are driven by the same audio clock.
2. **Phone companion.** Phones join over the local network by scanning a QR code. A phone can:
   - act as a microphone, streaming audio or pitch frames to the PC;
   - act as a remote control for song select and queueing;
   - show its singer's own lyric line.
   Wi-Fi adds roughly 50–150 ms of variable delay, so each phone gets a calibration step and its timestamps are aligned to the PC's clock. Pitch detection on the phone sends far less data than streaming raw audio, so it's the default.
3. **Online play**, using ORBIT's social and rooms layer, is out of scope until 1 and 2 work.

The companion protocol will become a second set of contracts in `Singularity.Contracts`, built the same way as the worker protocol, with shared fixtures.

## Licensing notes

- The UltraStar `song.txt` format is open; reading and writing it is fine. UltraStar Deluxe's code is GPL, so we implement its behaviour ourselves and copy no code.
- UltraStarKaraokeMaker is MIT: its pipeline ideas and code can be reused with attribution.
- UltrastarCreatorTool has no stated licence: use it for ideas only.
