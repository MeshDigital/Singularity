using Singularity.Contracts.UltraStar;
using Xunit;

namespace Singularity.Tests.Contracts;

public class UltraStarSerializerTests
{
    [Fact]
    public void LegacyFile_ReadsLeniently()
    {
        var song = UltraStarSerializer.Read(ContractFixtures.Read("song.legacy.txt"));

        Assert.Equal("Example Song", song.Title);          // BOM stripped
        Assert.Equal("Example.mp3", song.AudioFile);       // #MP3 fallback
        Assert.Equal(300.5, song.Bpm);                     // comma decimal
        Assert.Equal(-340, song.VideoGapMs);               // seconds → ms
        Assert.Equal(2500, song.StartMs);
        Assert.Equal(180000, song.EndMs);
        Assert.Equal(45250, song.PreviewStartMs);
        Assert.Equal(400, song.MedleyStartBeat);
        Assert.Equal(800, song.MedleyEndBeat);
        Assert.Equal(new[] { "AUTHOR", "ENCODING" }, song.ExtraHeaders.Select(h => h.Key));
        Assert.False(song.IsDuet);

        var notes = song.Voices.Single().Notes;
        Assert.Equal(9, notes.Count);                      // nothing after E
        Assert.Equal(new UltraStarNote(NoteType.Regular, 0, 4, 65, "Hel"), notes[0]);
        Assert.Equal(new UltraStarNote(NoteType.Golden, 10, 6, 69, " world"), notes[2]); // word-start space kept
        Assert.Equal(UltraStarNote.LineBreak(18), notes[3]);
        Assert.Equal(NoteType.Freestyle, notes[4].Type);
        Assert.Equal(NoteType.Rap, notes[5].Type);
        Assert.Equal(new UltraStarNote(NoteType.RapGolden, 30, 3, 58, " gold~"), notes[6]);
        Assert.Equal(UltraStarNote.LineBreak(36), notes[7]); // absolute mode ignores the second number
        Assert.Equal(46, notes[8].MidiTone);               // negative pitch
    }

    [Fact]
    public void RelativeFile_IsConvertedToAbsoluteBeats()
    {
        var song = UltraStarSerializer.Read(ContractFixtures.Read("song.relative.txt"));
        var beats = song.Voices.Single().Notes.Select(n => (n.Type, n.StartBeat)).ToArray();

        Assert.Equal(new[]
        {
            (NoteType.Regular, 0), (NoteType.Regular, 4), (NoteType.LineBreak, 8),
            (NoteType.Regular, 10), (NoteType.Regular, 13), (NoteType.LineBreak, 16),
            (NoteType.Regular, 17),
        }, beats);
        Assert.DoesNotContain("#RELATIVE", UltraStarSerializer.Write(song));
    }

    [Fact]
    public void Duet_ReadsBothVoices()
    {
        var song = UltraStarSerializer.Read(ContractFixtures.Read("song.duet.txt"));

        Assert.True(song.IsDuet);
        Assert.Equal("Alice", song.Voices[0].SingerName);
        Assert.Equal("Bob", song.Voices[1].SingerName);
        Assert.Equal(4, song.Voices[0].Notes.Count);
        Assert.Equal(new[] { 10, 14 }, song.Voices[1].Notes.Select(n => n.StartBeat));
    }

    [Fact]
    public void Duet_RoundTripsExactly()
    {
        var text = ContractFixtures.Read("song.duet.txt").Replace("\r\n", "\n");
        Assert.Equal(text, UltraStarSerializer.Write(UltraStarSerializer.Read(text)));
    }

    [Fact]
    public void LegacyP3_SharedLines_GoToBothSingers_InTimeOrder()
    {
        var song = UltraStarSerializer.Read(string.Join("\n",
            "#TITLE:t", "#ARTIST:a", "#MP3:a.mp3", "#BPM:300",
            "P1", ": 0 4 0 One", "- 6", ": 30 4 0 Later",
            "P2", ": 10 4 0 Two", "- 16",
            "P3", ": 20 4 0  Together", "- 26",
            "E"));

        Assert.True(song.IsDuet);
        Assert.Equal(new[] { 0, 6, 20, 26, 30 }, song.Voices[0].Notes.Select(n => n.StartBeat));
        Assert.Equal(new[] { 10, 16, 20 }, song.Voices[1].Notes.Select(n => n.StartBeat)); // trailing break dropped
        Assert.Equal(" Together", song.Voices[1].Notes[2].Syllable);

        var reread = UltraStarSerializer.Read(UltraStarSerializer.Write(song)); // written as plain P1/P2
        Assert.Equal(song.Voices[0].Notes, reread.Voices[0].Notes);
        Assert.Equal(song.Voices[1].Notes, reread.Voices[1].Notes);
    }

    [Fact]
    public void LegacyDuetSingerHeaders_AreRead()
    {
        var text = ContractFixtures.Read("song.duet.txt")
            .Replace("#P1:Alice", "#DUETSINGERP1:Alice").Replace("#P2:Bob", "#DUETSINGERP2:Bob");
        var song = UltraStarSerializer.Read(text);
        Assert.Equal("Alice", song.Voices[0].SingerName);
        Assert.Contains("#P2:Bob\n", UltraStarSerializer.Write(song));
    }

    [Fact]
    public void Write_IsCanonical_AndReadsBackEqual()
    {
        var legacy = UltraStarSerializer.Read(ContractFixtures.Read("song.legacy.txt"));
        var written = UltraStarSerializer.Write(legacy);

        Assert.StartsWith("#VERSION:1.1.0\n#TITLE:Example Song\n", written);
        Assert.Contains("#AUDIO:Example.mp3\n#MP3:Example.mp3\n", written);
        Assert.Contains("#VIDEOGAP:-0.34\n", written);
        Assert.Contains("#BPM:300.5\n", written);
        Assert.Contains("#START:2.5\n", written);
        Assert.Contains("#PREVIEWSTART:45.25\n", written);
        Assert.Contains("* 10 6 9  world\n", written);
        Assert.Contains("- 36\n", written);
        Assert.EndsWith("E\n", written);
        Assert.DoesNotContain("\r", written);

        var again = UltraStarSerializer.Read(written);
        Assert.Equal(legacy.Voices.Single().Notes, again.Voices.Single().Notes);
        Assert.Equal(written, UltraStarSerializer.Write(again));
    }

    [Fact]
    public void ReadFile_DetectsWindows1252()
    {
        var song = UltraStarSerializer.ReadFile(ContractFixtures.PathOf("song.cp1252.txt"));

        Assert.Equal("Café Song", song.Title);
        Assert.Equal("Beyoncé", song.Artist);
        Assert.Equal(new[] { "Don’t", " pay €5" }, song.Voices.Single().Notes.Select(n => n.Syllable)); // 0x92 / 0x80: not Latin-1
    }

    [Fact]
    public void Decode_PrefersBomThenDeclaredThenStrictUtf8()
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes("#TITLE:Café\n");
        Assert.Equal("#TITLE:Café\n", UltraStarSerializer.Decode(utf8));
        Assert.Equal("#TITLE:Café\n", UltraStarSerializer.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray()));
        // Declared UTF-8 with an invalid byte: decoded as UTF-8 (replacement char), not re-guessed as 1252.
        var declared = System.Text.Encoding.ASCII.GetBytes("#ENCODING:UTF8\n#TITLE:x").Append((byte)0xE9).ToArray();
        Assert.EndsWith("x�", UltraStarSerializer.Decode(declared));
    }

    [Theory]
    [InlineData("E E")]
    [InlineData("END")]
    public void AnyLineStartingWithE_EndsTheSong(string end)
    {
        var song = UltraStarSerializer.Read($"#TITLE:t\n#ARTIST:a\n#MP3:a.mp3\n#BPM:300\n: 0 1 0 x\n{end}\n: 5 1 0 ignored\n");
        Assert.Single(song.Voices.Single().Notes);
    }

    [Fact]
    public void BeatTiming()
    {
        var song = new UltraStarSong { Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 300, GapMs = 1000 };
        Assert.Equal(50, song.MillisecondsPerBeat);
        Assert.Equal(1500, song.BeatToMs(10));
        Assert.Equal(10, song.MsToBeat(1500));
    }

    [Theory]
    [InlineData("#ARTIST:a\n#MP3:a.mp3\n#BPM:300\n: 0 1 0 x\nE\n", "TITLE")]
    [InlineData("#TITLE:t\n#ARTIST:a\n#BPM:300\nE\n", "MP3")]
    [InlineData("#TITLE:t\n#ARTIST:a\n#MP3:a.mp3\n#BPM:fast\nE\n", "BPM")]
    public void MissingOrBadHeader_Throws(string text, string header)
    {
        var ex = Assert.Throws<FormatException>(() => UltraStarSerializer.Read(text));
        Assert.Contains("#" + header, ex.Message);
    }

    [Theory]
    [InlineData("X 0 1 0 x")]
    [InlineData(": 0 x 0 x")]
    [InlineData("-")]
    public void BadNoteLine_ThrowsWithLineNumber(string line)
    {
        var ex = Assert.Throws<FormatException>(() =>
            UltraStarSerializer.Read($"#TITLE:t\n#ARTIST:a\n#MP3:a.mp3\n#BPM:300\n{line}\nE\n"));
        Assert.Contains("Line 5", ex.Message);
    }

    [Fact]
    public void Write_RejectsLineBreaksInText()
    {
        var song = new UltraStarSong
        {
            Title = "t", Artist = "a", AudioFile = "a.mp3", Bpm = 300,
            Voices = new[] { new UltraStarVoice(new[] { new UltraStarNote(NoteType.Regular, 0, 1, 60, "a\nb") }) },
        };
        Assert.Throws<ArgumentException>(() => UltraStarSerializer.Write(song));
        Assert.Throws<ArgumentException>(() => UltraStarSerializer.Write(song with { Title = "x\ny" }));
    }
}
