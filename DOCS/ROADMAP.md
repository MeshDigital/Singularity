# Singularity roadmap

Singularity is a karaoke game that does what UltraStar does, plus three things:

- it finds and downloads music itself, using the Soulseek stack inherited from ORBIT;
- it charts songs that don't have a chart: community charts from USDB first, then an AI worker modelled on
  UltraStarKaraokeMaker;
- it puts the show on a second screen.

It reads and writes standard UltraStar `song.txt` files (format 1.1.0), so existing collections work unchanged and
other players can open the songs it makes.

## Phases

| # | Phase | Delivers | Status |
|---|---|---|---|
| 0 | Contracts | `Singularity.Contracts`: song package metadata, the quality grade, the video-gap rule, the `song.txt` reader and writer (solo, duet, legacy), conversion from AI output to a chart, the worker protocol, with the Pydantic mirror and shared fixtures | Done |
| 1 | Inference worker | Python worker: Demucs stems, forced alignment of LRCLIB lyrics (Whisper only without lyrics), SwiftF0 pitch, tempo. C# host with cancellation and process-tree kill. LRCLIB client | Done; measured with `Tools/ChartBench` on the 529-song human-charted collection |
| 2 | Ingestion | Spotify link or list → ORBIT's Soulseek search and download → USDB community chart fitted to the recording, or AI chart → music video synced by sound → song package with quality grade, built in staging and published in one step | Done, and run live. Not done: fingerprint check (fpcalc + AcoustID) |
| 3 | Sing core | One audio clock, video drawn inside the scene, microphone capture and pitch per player, note lane, lyrics, UltraStar scoring, duets, the sing screen's look | Done |
| 4 | UltraStar parity | The checklist below | In progress |
| 5 | Second screen | A projector or TV as the stage, with song select for the room between songs | Done. The phone companion (mic over Wi-Fi, remote control) is not started; its protocol and clock sync exist |
| 6 | Editor | Correction editor (piano roll, BPM/GAP tapping, golden/freestyle marking) for songs that need checking | Not started |

## UltraStar parity checklist

Sources: UltraStar Deluxe and UltraStar Play.

**Songs and library**
- [x] Load existing UltraStar song folders: encodings, `#RELATIVE`, comma decimals, `#MP3`/`#AUDIO`, `[CO]`/`[BG]`
  images
- [x] Duets (P1/P2, legacy P3)
- [x] Song select: covers, preview from `#PREVIEWSTART` (or the medley, or a third in), search, preview video
- [x] One entry per song, its charts as versions (community and AI, solo and duet, edits)
- [ ] Sort and group by artist, title, edition, genre, language, year, folder
- [ ] Playlists (reuse ORBIT playlists)
- [ ] Jukebox mode (play with lyrics, no singing)

**Singing**
- [ ] 1–6 players: 2 work (two microphones, or a two-mic adapter split left and right)
- [x] Difficulty: Easy, Medium, Hard, chosen on the Sing page and in Settings
- [x] Note types: normal, golden (double), freestyle (unscored), rap (voice only)
- [x] Scoring out of 10,000 (notes, golden notes, line bonus) with ratings per line
- [x] Octave-independent pitch matching
- [x] `#START`/`#END`, `#VIDEOGAP`, cover or background when there is no video
- [x] Original vocals off, guide or full (separated stems), for the whole collection in the background
- [x] Pause and restart
- [ ] Medley mode (the medley is detected; the mode isn't built)
- [ ] Skip intro

**Party and results**
- [x] Results per singer: score, notes, golden notes, line bonus, rating
- [ ] Highscores per song and difficulty, statistics
- [ ] Party mode: teams, rounds, duel, blind (hidden notes), until-5000
- [ ] Webcam background

**Setup**
- [x] Microphone setup with per-input channel selection, a live pitch view and a latency (click) test
- [x] Projector or TV as the stage
- [ ] Gamepad and remote navigation (the keyboard works)
- [ ] Themes and translations

## Next

1. Highscores, then a party queue that guests fill from the projector's song select.
2. Better AI charts, measured with ChartBench (recall about 70% at 100 ms, pitch within a semitone about 85%).
3. A simple correction editor: shift the gap, nudge lines, fix a pitch.
4. The phone companion.

## Licensing notes

- The UltraStar `song.txt` format is open; reading and writing it is fine. UltraStar Deluxe's code is GPL, so
  Singularity implements its behaviour itself and copies no code.
- UltraStarKaraokeMaker and UltraStar-CLI are MIT: ideas and code can be reused with attribution. Singularity logs in
  to USDB with the user's own account and never creates accounts.
- UltrastarCreatorTool has no stated licence: ideas only.
