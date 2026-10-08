# Singularity user guide

This guide covers setting Singularity up, filling it with songs, and running a karaoke night. It's written for the
person at the laptop; the people singing only need a microphone.

## 1. First start

Start Singularity. The menu on the left has three groups:

- **Karaoke**: Sing (song select), Microphone, Add songs
- **Acquire**: Search (find a single file on Soulseek by hand) and Downloads
- **System**: Dashboard, Library, Users and Settings, mostly inherited from ORBIT

Open **Settings** and work through the cards from the top.

### Accounts

| Account | What it's for | Without it |
|---|---|---|
| **Soulseek** | Downloading songs | You can only use songs you already have, or audio files on your PC |
| **Spotify** | Adding songs from Spotify links | Paste songs as `Artist - Title` instead |
| **USDB** ([usdb.animux.de](https://usdb.animux.de)) | Charts made by people, used before the AI makes one | Every new song gets an AI chart |

Enter the Soulseek username and press **Connect**; it asks for the password the first time. Tick *Connect when
Singularity starts* so downloads resume by themselves. The USDB password is stored encrypted for your Windows
account and is never shown again; to change it, type the new one and press **Save**.

### Folders

- **Your karaoke songs**: folders with UltraStar songs (a folder per song, each with a `.txt` file). Singularity
  only reads them. `D:\KARAOKE\songs` is used by itself when it exists.
- **New songs are made in**: where songs Singularity makes are kept. They're ordinary UltraStar folders.
- **Downloads go to**: where Soulseek downloads land before they become karaoke songs.

### Microphones

Open **Microphone** (or Settings → *Microphones and latency…*):

1. Pick player 1's microphone. With a two-mic karaoke adapter (one stereo device), pick it for both players and set
   player 1 to **Left** and player 2 to **Right**. Two separate microphones each use **Mix**.
2. Sing or hum: the level bar should pass the gate line clearly, and a steady note should draw a flat line, not jump
   between octaves.
3. Run the **click test** with speakers (not headphones) to measure the microphone's delay, so scoring is fair.

If the click test hears nothing, Windows' *Audio enhancements* (echo cancellation) may be on for the microphone; turn
them off in the Windows sound settings.

## 2. Adding songs

Open **Add songs** and paste one of:

- a Spotify **song** link: becomes a single song in your library;
- a Spotify **album** or **playlist** link: every song in it;
- songs as text, one per line: `Queen - Bohemian Rhapsody`.

A label under the box says what it recognised ("Spotify playlist", "12 songs"). Press **Add** or Enter
(Shift+Enter starts a new line).

Each song then goes through these steps, which you can follow on **Add songs** and **Downloads**:

| Step | What happens | Takes |
|---|---|---|
| Searching Soulseek | ORBIT's search finds the best file, lossless where possible | seconds |
| Downloading audio | The file downloads | seconds to minutes |
| Looking for a community chart | USDB is checked for a chart with 3 stars or more | a few seconds |
| Generating AI chart | Vocals are separated; the community chart is fitted to your recording, or the AI makes one | 30–45 s on the GPU |
| Downloading video | The music video is found on YouTube (alongside the previous step) | 10–20 s |
| Ready | The song appears on the Sing page with a **NEW** badge | |

Songs are made one at a time, and the AI pauses while someone is singing so the game keeps the GPU.

**Other ways in:**

- **Every download becomes a karaoke song** (on by default): songs downloaded from Search or elsewhere are made into
  karaoke songs too.
- **Make karaoke songs from my downloads**: turns everything already downloaded into karaoke songs, for example a
  playlist imported before.
- **Audio file…**: makes a song from a file on your PC.

Songs your collection already has are skipped, so nothing is charted twice.

### Quality grades

Every song Singularity makes gets a grade, shown on its card:

| Grade | Means |
|---|---|
| **A+** | A community chart, or an excellent AI chart, with a synced music video |
| **A** | Fully singable |
| **B** | Singable; some lines may be a little off |
| **needs checking** | The AI wasn't sure (an unusual language, hardly any lyrics found, a very busy mix) |

The grade comes from how sure the AI was of the words and the melody, whether the download matches the recording's
length, the video sync and the song's details. It was tuned on 50 human-charted songs.

## 3. Singing

Open **Sing**. Search by artist or title; the highlighted song previews (with its video when it has one). A song with
several versions (community and AI chart, solo and duet) shows **◀ 1 of 2 ▶** on its card: Left and Right switch
versions. Press **Sing** (or Enter, or double-click).

**Finding songs.** Next to the search box: show only *New* songs, songs *With video*, *Duets*, *Community charts*,
*AI charts* or songs *Not sung yet*; pick a language; sort by artist, title, recently added, year or best score.
A card shows the song's best score once it's been sung.

**Another chart for a song.** Right-click a song's card: **Make an AI chart** makes one (for a song from your own
folders it becomes an extra version), and on an AI-charted song **Look for a community chart** tries USDB again.
Follow it on *Add songs*.

**Jukebox.** **Play** on a card, or **Jukebox** at the top, plays songs with their lyrics and video and the
original vocals, no singing or scores: music between singers. When a song ends a random one from the list follows;
**N** skips to the next.

At the top of the page:

- **Difficulty**: how far off still earns points: Easy up to 1¾ semitones, Medium up to 1, Hard up to ⅔. Within 20
  cents of the note is always full marks, and the closer you sing, the more you earn. Octaves never matter, and the
  first moment of a note (a scoop onto it) isn't held against you. For songs with separated vocals you are also scored
  against what the original singer actually sang: where a chart's note is a semitone or two off, singing it the way the
  artist did still counts. Starting notes on time matters a little too: a note first sung more than 0.12 s late
  costs part of its line's bonus (at most a quarter of it, from 0.4 s late), and the results say how much of your
  singing was on time.

**Charts that may be wrong.** Each song Singularity makes is checked against the original singer: an AI chart's notes
the singer clearly sings elsewhere are moved there, and a card says "7 lines may be off" or "Chart doesn't match the
recording" when something is wrong (Show → *Charts to check* lists them). Any song with separated vocals is checked
again when you sing it, and the Sing page says so if the chart doesn't fit. Right-click a song to make a new chart.

**Fixing a chart.** Right-click a song Singularity made and choose **Fix the chart…**. The lines are listed on the
left (*check* marks those that don't match the original singer) and the selected line is drawn with the singer as
blue dots: the notes should sit on them. **Play** plays the line. Fixes:
- **Fit to the singer** finds where (and in which key) the whole chart matches the singer best, up to 10 s either way.
- **−100 / −20 / +20 / +100 ms** move the whole chart a little.
- For the selected line: **Use the singer's pitch** (moves the notes the singer clearly sings elsewhere), **Earlier /
  Later** by a beat, **Up / Down** a semitone.
- Click a note, then **Higher / Lower** to change just that one.
Every change can be undone. **Save** writes the chart and keeps the first version next to it as `song.txt.orig`.
Songs in your own folders aren't edited.
- **Sing on**: this window, or a projector or TV (see below).
- **Text size**: the size of lyrics, notes and scores on the stage.

**While singing**

| Key | Does |
|---|---|
| Space or P | Pause and resume |
| S | Skip the intro (to three seconds before the first note) |
| V | Original vocals: off, quiet guide, full (for songs with removed vocals) |
| + / − | Bigger or smaller text |
| Esc | Back to song select |

The stage shows the notes in a lane, your voice as a dot with a trail (on the note is good), the lyrics with the sung
part coloured, and a rating after each line. The lane keeps one scale for the whole song, so a step of a semitone is
always the same height, and it glides up or down to each new line. What you sing fills the notes in your colour:
amber and a little higher when you were sharp, cyan and a little lower when flat.

**When to sing.** The bar along the bottom is a map of the song: your parts in your colour (with two singers, P1 on
the top half and P2 below), a duet partner's part in grey, and singing that isn't scored (backing vocals, ad-libs
the chart has no notes for) in faint white. The gap before the next coloured stretch is how long you can rest.
Before a line after a long wait, "in 8 s" counts down next to it, then three dots for the last moment. The lane
says when the singing you hear isn't yours: "P2 sings this part", or "Backing vocals: not scored". Rap notes are
striped and marked RAP: there only your voice counts, not the pitch; dashed notes are freestyle and not scored. Golden notes score double. After the song, the results show each
singer's score, notes, golden notes and line bonus, and the song's best scores at this difficulty: a place in the
top 10 shows as "New high score!" or "3rd best on this song". Singer names come from Settings → Singing. **R** sings
again, **Enter** returns to song select.

**Two singers.** Turn on *Two singers* on the Microphone page. Duet songs give each singer their own part; any other
song becomes a sing-off on the same notes. The lanes go top and bottom, player 1 blue and player 2 red.

**Removing vocals.** Songs Singularity makes come with their vocals already separated. For your own collection,
press **Remove vocals** on a song, or **Remove vocals for all songs** for the whole collection: about 30 seconds a
song on the GPU, in the background. It pauses while someone sings, can be stopped and resumed, and keeps the PC
awake while it runs.

## 4. The projector

Connect the projector or TV as a second display (Windows: *Extend*), then choose it under **Sing on**. From then on:

- **Between songs** the projector shows song select for the room: the highlighted song large, its neighbours either
  side, the room lit in the cover's colour, and the music video playing in place of the cover during the preview.
  Use the keyboard while the projector window has focus: **Left/Right** move between songs, **Up/Down** between a
  song's versions, **Enter** sings.
- **While singing** the stage is full screen on the projector; the laptop keeps the controls.

Both screens share the same selection, so the laptop and the room can take turns choosing.

**Watched playlists.** When you add a Spotify playlist on *Add songs*, tick **Keep checking this playlist for new
songs**: every 30 minutes (and shortly after Singularity starts) the playlist is checked again, and songs added to it
on Spotify are downloaded and made into karaoke songs by themselves. Handy for a shared "karaoke" playlist before a
party. Songs the collection already has are skipped. The playlists are listed on *Add songs* with **Check now** and
**Stop watching**.

**The party queue.** Who sings what next. Right-click a song and choose **Add to the queue** (under the name typed in
the *Up next* panel on the Sing page), or let guests add themselves from their phones. The projector shows the next
three singers; **Sing next** starts the top one, under that singer's name (results and high scores say who sang).
Each singer can have three songs waiting. The queue survives a restart.

**Phones.** Turn on Settings → Phones → *Guests pick songs and join the queue from their phone*. A QR code appears on
the projector: guests on the same Wi-Fi scan it, type their name once, search the songs and tap **Sing this**. Nothing
to install. Only phones on your network with the code's key get in, and the key changes every time Singularity starts.
The first time, Windows asks whether Singularity may use the network: allow it for private networks.

## 5. Downloads

**Downloads** lists every song on its way, with one status from *Searching Soulseek* to *Ready to sing*, and counts
of songs on their way, becoming karaoke, ready and failed. **Retry failed** searches Soulseek again for songs that
couldn't be downloaded. The **Advanced** switch shows ORBIT's full download center (peers, priorities, quality
profiles).

## 6. Troubleshooting

| Problem | What to do |
|---|---|
| "No microphone" while singing | Check the Microphone page; replug the microphone and pick it again |
| The score stays low though you sing well | Run the click test on the Microphone page; try Easy; check that the level passes the gate line |
| Songs stay at *Searching Soulseek* | Check that Soulseek is connected (Settings → Accounts) |
| A song fails at *Couldn't be downloaded* | Nobody on Soulseek shared it at that moment; **Retry failed** later |
| No AI charts, no vocal removal | Settings → System shows whether the AI worker is installed; **Set up the AI worker** there installs it (several GB) |
| No music videos | Settings → System shows whether yt-dlp is installed; *Find the music video* must be on |
| The video is dimmed | It's a different version of the song (another edit, a long intro), so it plays as a backdrop instead of in sync |
| A Spotify link isn't recognised | Connect Spotify in Settings, or paste the songs as `Artist - Title` |
| The window closed but Singularity still runs | Closing hides it to the tray (downloads carry on); use the tray icon's **Exit** to quit |

## 7. Where things are kept

| What | Where |
|---|---|
| Settings | `%APPDATA%\Singularity\config.ini` |
| Library database | `%APPDATA%\Singularity\library.db` (backed up at every start to `Backups\`) |
| Soulseek, Spotify and USDB logins | `%LOCALAPPDATA%\Singularity\` (encrypted for your Windows account) |
| Logs | `%LOCALAPPDATA%\Singularity\logs\` |
| Removed vocals for your collection | `%LOCALAPPDATA%\Singularity\stems\` |
| Songs being made | `%LOCALAPPDATA%\Singularity\staging\` (cleared when a song is done) |
| AI models | `%LOCALAPPDATA%\Singularity\models\` |
