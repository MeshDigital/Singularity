using System;
using System.IO;
using System.Reflection;
using Moq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Singularity.Configuration;
using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services;

/// <summary>
/// Crossfade end-of-track behaviour: when the outgoing track runs out mid-crossfade the player
/// must still complete the hand-over instead of stalling with the incoming track at a partial level.
/// </summary>
public class AudioPlayerCrossfadeTests : IDisposable
{
    private readonly string _wav = Path.Combine(Path.GetTempPath(), $"xfade-{Guid.NewGuid():N}.wav");

    public AudioPlayerCrossfadeTests()
    {
        using var writer = new WaveFileWriter(_wav, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
        for (int i = 0; i < 8000; i++) writer.WriteSample(0f);
    }

    public void Dispose()
    {
        try { File.Delete(_wav); } catch { /* reader may still be closing on the disposal task */ }
    }

    [Fact]
    public void OutgoingTrackEndingMidCrossfade_CompletesTheHandOver()
    {
        var sut = new AudioPlayerService(new AppConfig());
        Get<System.Timers.Timer>(sut, "_timer")!.Stop(); // drive the tick by hand

        var outgoing = NewDeck(PlaybackState.Stopped, gain: 0.3f);   // ran out mid-fade
        var incoming = NewDeck(PlaybackState.Playing, gain: 0.4f);   // audible but not at full level
        Set(sut, "_current", outgoing.Deck);
        Set(sut, "_next", incoming.Deck);
        Set(sut, "_isCrossfading", true);
        bool ended = false;
        sut.CrossfadeEnded += (_, _) => ended = true;

        typeof(AudioPlayerService).GetMethod("OnTimerElapsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, new object?[] { null, null });

        Assert.True(ended);
        Assert.False(Get<bool>(sut, "_isCrossfading"));
        Assert.Same(incoming.Deck, Get<object>(sut, "_current"));
        Assert.Null(Get<object>(sut, "_next"));
        Assert.Equal(1f, incoming.Gain.Volume);
    }

    private (object Deck, VolumeSampleProvider Gain) NewDeck(PlaybackState state, float gain)
    {
        var deckType = typeof(AudioPlayerService).GetNestedType("Deck", BindingFlags.NonPublic)!;
        var deck = Activator.CreateInstance(deckType, nonPublic: true)!;
        var reader = new AudioFileReader(_wav);
        var output = new Mock<IWavePlayer>();
        output.SetupGet(o => o.PlaybackState).Returns(state);
        var volume = new VolumeSampleProvider(reader) { Volume = gain };
        deckType.GetField("AudioFile")!.SetValue(deck, reader);
        deckType.GetField("Output")!.SetValue(deck, output.Object);
        deckType.GetField("Gain")!.SetValue(deck, volume);
        deckType.GetField("FilePath")!.SetValue(deck, _wav);
        return (deck, volume);
    }

    private static void Set(object o, string field, object? value) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static T? Get<T>(object o, string field) =>
        (T?)o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
}
