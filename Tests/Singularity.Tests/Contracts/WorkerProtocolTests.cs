using System.Text.Json;
using Singularity.Contracts.Inference;
using Singularity.Contracts.UltraStar;
using Xunit;

namespace Singularity.Tests.Contracts;

public class WorkerProtocolTests
{
    private static string[] SessionLines() =>
        File.ReadAllLines(ContractFixtures.PathOf("worker-session.jsonl")).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void FixtureSession_DecodesEveryLine()
    {
        var lines = SessionLines();
        var commands = lines.Where(l => l.StartsWith("{\"command\"")).Select(WorkerProtocol.DecodeCommand).ToArray();
        var events = lines.Where(l => !l.StartsWith("{\"command\"")).Select(WorkerProtocol.DecodeEvent).ToArray();

        var process = Assert.IsType<ProcessTrackCommand>(commands[0]);
        Assert.Equal("t-001", process.TaskId);
        Assert.Contains("\n", process.Lyrics);
        Assert.IsType<CancelCommand>(commands[1]);
        Assert.IsType<ShutdownCommand>(commands[2]);

        var ready = Assert.IsType<ReadyEvent>(events[0]);
        Assert.Equal(WorkerProtocol.Version, ready.ProtocolVersion);
        Assert.Equal(PipelineStage.Separation, Assert.IsType<StageStartedEvent>(events[1]).Stage);
        Assert.Equal(0.5, Assert.IsType<ProgressUpdateEvent>(events[2]).Progress);
        Assert.Equal(41200, Assert.IsType<StageCompletedEvent>(events[3]).DurationMs); // discriminator last
        Assert.Equal(WorkerLogLevel.Warning, Assert.IsType<LogEvent>(events[4]).Level);
        Assert.Equal("gpu_stats", Assert.IsType<UnknownWorkerEvent>(events[5]).Name);

        var done = Assert.IsType<TaskFinishedEvent>(events[6]);
        Assert.Equal(TaskOutcome.Succeeded, done.Outcome);
        Assert.Equal(2, done.Result!.Lines.Count);
        Assert.Null(done.Result.Lines[0].Syllables[2].MidiTone);
        Assert.False(done.Result.Lines[0].Syllables[5].StartsWord);

        var failed = Assert.IsType<TaskFinishedEvent>(events[7]);
        Assert.Equal(TaskOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Result);
    }

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        WorkerEvent[] events =
        {
            new ProgressUpdateEvent("t", PipelineStage.Alignment, 0.25, "ok"),
            new LogEvent(WorkerLogLevel.Error, "boom"),
            new TaskFinishedEvent("t", TaskOutcome.Cancelled),
        };
        foreach (var e in events)
        {
            var line = WorkerProtocol.Encode(e);
            Assert.DoesNotContain("\n", line);
            Assert.Equal(e, WorkerProtocol.DecodeEvent(line));
        }

        Assert.Equal("{\"command\":\"cancel\",\"taskId\":\"t\"}", WorkerProtocol.Encode(new CancelCommand("t")));
        Assert.Equal("{\"command\":\"shutdown\"}", WorkerProtocol.Encode(new ShutdownCommand()));
    }

    [Fact]
    public void BlankLine_IsNull() => Assert.Null(WorkerProtocol.DecodeEvent("   "));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"stage\":\"pitch\"}")]
    [InlineData("{\"event\":\"progress_update\",\"taskId\":\"t\",\"stage\":\"warp_drive\",\"progress\":1}")]
    public void Malformed_Throws(string line) =>
        Assert.ThrowsAny<JsonException>(() => WorkerProtocol.DecodeEvent(line));

    [Fact]
    public void UnknownCommand_Throws() =>
        Assert.Throws<JsonException>(() => WorkerProtocol.DecodeCommand("{\"command\":\"format_disk\"}"));

    [Fact]
    public void FixtureResult_BuildsAPlayableChart()
    {
        var done = SessionLines().Select(l => l.StartsWith("{\"command\"") ? null : WorkerProtocol.DecodeEvent(l))
            .OfType<TaskFinishedEvent>().First();
        var (bpm, gap, voice) = UltraStarChartBuilder.Build(done.Result!.TempoBpm, done.Result.Lines);

        Assert.Equal(284.12, bpm);
        Assert.Equal(1250, gap);
        var song = new UltraStarSong { Title = "t", Artist = "a", AudioFile = "audio.flac", Bpm = bpm, GapMs = gap, Voices = new[] { voice } };
        var reread = UltraStarSerializer.Read(UltraStarSerializer.Write(song));
        Assert.Equal(voice.Notes, reread.Voices.Single().Notes);
    }
}

public class UltraStarChartBuilderTests
{
    private static TimedSyllable S(string text, int start, int end, int? tone = 60, bool word = true, double conf = 0.9) =>
        new(text, start, end, word, tone, conf, 0.9);

    [Theory]
    [InlineData(120, 240)]
    [InlineData(71.03, 284.12)]
    [InlineData(200, 200)]
    [InlineData(55, 220)]
    public void GridBpm_DoublesUntilFineEnough(double tempo, double grid) =>
        Assert.Equal(grid, UltraStarChartBuilder.GridBpmFor(tempo));

    [Fact]
    public void Quantizes_WithGapAtFirstSyllable_AndLineBreaks()
    {
        // tempo 300 → 75 ms... grid 300 → 50 ms per beat
        var lines = new[]
        {
            new LyricLine(new[] { S("Hel", 1000, 1200, 62), S("lo", 1200, 1500, 64, word: false) }),
            new LyricLine(new[] { S("world", 2000, 2400, 65) }),
        };
        var (bpm, gap, voice) = UltraStarChartBuilder.Build(300, lines);

        Assert.Equal(300, bpm);
        Assert.Equal(1000, gap);
        Assert.Equal(new[]
        {
            new UltraStarNote(NoteType.Regular, 0, 4, 62, "Hel"),
            new UltraStarNote(NoteType.Regular, 4, 6, 64, "lo"),
            UltraStarNote.LineBreak(10),
            new UltraStarNote(NoteType.Regular, 20, 8, 65, "world"),
        }, voice.Notes);
    }

    [Fact]
    public void WordStarts_GetLeadingSpace_ExceptAtLineStart()
    {
        var lines = new[] { new LyricLine(new[] { S("Is", 0, 200), S("this", 200, 400), S("it", 400, 600, word: false) }) };
        var texts = UltraStarChartBuilder.Build(300, lines).Voice.Notes.Select(n => n.Syllable);
        Assert.Equal(new[] { "Is", " this", "it" }, texts);
    }

    [Fact]
    public void Overlaps_ArePushedLater_AndLengthIsAtLeastOne()
    {
        var lines = new[] { new LyricLine(new[] { S("a", 0, 300), S("b", 100, 110) }) };
        var notes = UltraStarChartBuilder.Build(300, lines).Voice.Notes;
        Assert.Equal(6, notes[1].StartBeat);
        Assert.Equal(1, notes[1].DurationBeats);
    }

    [Fact]
    public void UnvoicedSyllable_IsFreestyle_AtPreviousPitch()
    {
        var lines = new[] { new LyricLine(new[] { S("a", 0, 200, 67), S("h", 200, 400, null) }) };
        var notes = UltraStarChartBuilder.Build(300, lines).Voice.Notes;
        Assert.Equal(new UltraStarNote(NoteType.Freestyle, 4, 4, 67, " h"), notes[1]);
    }

    [Fact]
    public void GoldenNotes_AreTheLongestConfidentFivePercent()
    {
        var syllables = Enumerable.Range(0, 40).Select(i => S($"s{i}", i * 1000, i * 1000 + (i == 7 || i == 30 ? 900 : 200))).ToArray();
        var notes = UltraStarChartBuilder.Build(300, new[] { new LyricLine(syllables) }).Voice.Notes;

        var golden = notes.Select((n, i) => (n, i)).Where(x => x.n.Type == NoteType.Golden).Select(x => x.i).ToArray();
        Assert.Equal(new[] { 7, 30 }, golden); // floor(40 * 0.05) = 2
    }

    [Fact]
    public void Empty_IsEmptyVoice() =>
        Assert.Empty(UltraStarChartBuilder.Build(120, Array.Empty<LyricLine>()).Voice.Notes);
}
