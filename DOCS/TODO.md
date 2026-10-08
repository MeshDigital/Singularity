# To do

The working list, in the order it will be done. Ticked items stay for a release or two, then move to the
[roadmap](ROADMAP.md)'s history. Each item says what "done" means.

## Now

- [x] **Release 0.2.2-alpha.** The download queue no longer stalls after a stalled download; "Separating the vocals" and
  "Placing the community chart" instead of "Generating AI chart"; Downloads filters (the counts are filters, a search,
  songs still moving first). Done: installed, smoke-tested, a screenshot of the Downloads page, released on GitHub.

## Singing feel

- [x] **Score against the original singer.** At import (and in the background for older songs) save the singer's pitch
  curve from the separated vocals next to the chart. While singing, a beat also counts when it matches what the singer
  actually sang there, so a wrong note in an AI chart no longer costs points. Done: the artist's own vocals score
  clearly higher on AI charts than today (measured with the score bench on the songs with vocals), human charts don't
  get easier for a singer who is off, tests.
- [x] **Check AI charts against the singer.** The pitch check community charts get, per line, for AI charts: lines where
  the chart and the singer disagree are flagged ("needs checking" with the line numbers) and, where the singer's pitch is
  clear, the note is corrected. Done: measured on the songs with vocals, flagged lines visible in song select.
- [x] **Credit for timing.** A small bonus for starting a note on time, within the line bonus. Done: a late singer loses a
  little, an on-time one nothing, tests.
- [x] **Best score on the projector's version badge.**

## Party night

- [x] **Phones as remotes, in the browser.** A small local web server and a QR code on the projector; guests browse the
  collection and queue songs from their phone, nothing to install. Done: works on an Android and an iPhone on the same
  Wi-Fi, only on the local network, nothing exposed to the internet. (Tested here end to end on 127.0.0.1 and in a
  390 px browser frame; a real phone over Wi-Fi is still to be tried.)
- [x] **Party queue.** Singers' names and who's next, shown on the projector between songs; filled from phones, the
  laptop or the projector's song select.
- [x] **Watched Spotify playlist.** A "karaoke" playlist re-synced now and then, so songs added on Spotify appear by
  themselves (ORBIT's playlist sync does the work). (Built and unit-tested; not yet run against Spotify, which needs
  the owner's accounts: try "Keep checking this playlist" on Add songs.)

## Larger work

- [ ] **A simple chart editor.** Shift the gap, nudge a line, fix a pitch; opened from songs that need checking.
- [ ] **Better AI charts,** measured with ChartBench (today: about 70% of notes within 100 ms, 85% of pitches within a
  semitone).
- [ ] **Phones as microphones** (the companion app): pitch on the phone, clock sync, per-phone latency calibration.
- [ ] **Party modes:** teams, rounds, duel, blind (hidden notes), until-5000.
- [ ] **Fingerprint check** (Chromaprint and AcoustID): confirm a download is the requested recording.
- [ ] **Now singing** to Discord or as an OBS overlay.
- [ ] UltraStar parity leftovers: group by edition, genre and folder; playlists; medley mode; statistics; webcam
  background; gamepad navigation; themes and translations; more than two singers.

## Small fixes

- [x] *Remove* on the song folder Singularity found by itself doesn't stick.
- [ ] A slow download's backup transfer from a second peer can write to the same `.part` file for a moment.
- [x] README screenshots with one singer (the current ones show two: the screenshot machine has two set up).
- [x] An installed copy started from inside the source folder logs to the source folder's `logs` (it takes it for a
  development run).

## Decisions for the owner

These wait for an answer; nothing is done about them until then.

- **Licence.** None yet, which means "all rights reserved". Open source (MIT, or GPL so changes stay open), or private?
- **Public or private repository.** Private now, so images in release notes only show to signed-in members. Goes with
  the licence.
- **Code signing.** Without a certificate Windows SmartScreen warns on every install (about €100-400 a year). Worth it
  once other people install the app.
- **Two versions of a song.** Re-importing or re-charting a song replaces its chart. Keep a community and an AI chart
  side by side instead, as songs from your own folders do?
- **The Hot N Cold misplacement.** Why that community chart was placed 3.6 s late is unknown (the pitch check now
  corrects it). Finding out needs the original from USDB, downloaded with the owner's account.
