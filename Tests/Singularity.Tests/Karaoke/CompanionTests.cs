using System.Text.Json;
using Singularity.Contracts.Companion;
using Singularity.Karaoke.Companion;
using Singularity.Tests.Contracts;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class CompanionProtocolTests
{
    [Fact]
    public void FixtureSession_DecodesBothDirections()
    {
        var lines = File.ReadAllLines(ContractFixtures.PathOf("companion-session.jsonl")).Where(l => l.Length > 0).ToArray();
        string[] pcTypes = { "\"welcome\"", "\"ping\"", "\"song_state\"" };
        var fromPc = lines.Where(l => pcTypes.Any(l.Contains)).Select(CompanionProtocol.DecodePc).ToArray();
        var fromPhone = lines.Where(l => !pcTypes.Any(l.Contains)).Select(CompanionProtocol.DecodePhone).ToArray();

        Assert.Equal("4821", Assert.IsType<HelloMessage>(fromPhone[0]).SessionCode);
        Assert.Equal(1000512.25, Assert.IsType<PongMessage>(fromPhone[1]).ReceivedMs);
        var pitch = Assert.IsType<PitchBatchMessage>(fromPhone[2]);
        Assert.Equal(57.12, pitch.Samples[0].Midi);
        Assert.Null(pitch.Samples[1].Midi);
        Assert.Equal(RemoteAction.Select, Assert.IsType<RemoteMessage>(fromPhone[3]).Action);

        Assert.Equal(2, Assert.IsType<WelcomeMessage>(fromPc[0]).PlayerSlot);
        Assert.Equal(7, Assert.IsType<PingMessage>(fromPc[1]).Id);
        Assert.Equal("Today is gonna be the day", Assert.IsType<SongStateMessage>(fromPc[2]).Line);
    }

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var state = new SongStateMessage(true, "t", "a", "line", null);
        Assert.Equal(state, CompanionProtocol.DecodePc(CompanionProtocol.Encode(state)));
        Assert.Equal("{\"type\":\"ping\",\"id\":3}", CompanionProtocol.Encode(new PingMessage(3)));
    }

    [Fact]
    public void UnknownType_Throws() =>
        Assert.ThrowsAny<JsonException>(() => CompanionProtocol.DecodePhone("{\"type\":\"selfdestruct\"}"));
}

public class ClockSyncTests
{
    /// <summary>A phone whose clock runs <paramref name="offset"/> ms ahead, over a link with the given one-way delays.</summary>
    private static void Exchange(ClockSync sync, double pcNow, double offset, double up, double down, double turnaround = 1)
    {
        double t1 = pcNow + up + offset;
        double t2 = t1 + turnaround;
        double t3 = pcNow + up + turnaround + down;
        sync.Add(pcNow, t1, t2, t3);
    }

    [Fact]
    public void SymmetricLink_GivesTheExactOffset()
    {
        var sync = new ClockSync();
        for (int i = 0; i < 8; i++) Exchange(sync, i * 1000, offset: 52_300, up: 4, down: 4);
        Assert.True(sync.IsSynced);
        Assert.Equal(52_300, sync.OffsetMs, 6);
        Assert.Equal(1_000 + 52_300 - 52_300, sync.ToPcTime(1_000 + 52_300), 6);
    }

    [Fact]
    public void JitteryWiFi_TrustsTheFastestExchanges()
    {
        var sync = new ClockSync();
        var rng = new Random(3);
        for (int i = 0; i < 32; i++)
        {
            // Mostly 3-6 ms each way, but a third of the exchanges stall 20-150 ms in one direction.
            double up = 3 + rng.NextDouble() * 3, down = 3 + rng.NextDouble() * 3;
            if (i % 3 == 0) up += 20 + rng.NextDouble() * 130;
            Exchange(sync, i * 500, offset: -8_000, up, down);
        }
        Assert.InRange(sync.OffsetMs, -8_002, -7_998);
        Assert.True(sync.UncertaintyMs < 7);
    }

    [Fact]
    public void ImpossibleReply_IsIgnored()
    {
        var sync = new ClockSync();
        sync.Add(1000, 5, 10, 999); // came back before it was sent
        Assert.Equal(0, sync.SampleCount);
        Assert.False(sync.IsSynced);
    }
}
