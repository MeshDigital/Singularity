using Singularity.Karaoke.Audio;
using Xunit;

namespace Singularity.Tests.Karaoke;

public class MicBlockTests
{
    // Two frames of a stereo adapter: P1 on the left (0.1, 0.3), P2 on the right (0.5, 0.7).
    private static readonly MicBlock Stereo = new(new[] { 0.1f, 0.5f, 0.3f, 0.7f }, 2, 2, 1000);

    [Theory]
    [InlineData(MicChannel.Left, 0.1f, 0.3f)]
    [InlineData(MicChannel.Right, 0.5f, 0.7f)]
    [InlineData(MicChannel.Mix, 0.3f, 0.5f)]
    public void StereoAdapter_SplitsPlayers(MicChannel channel, float first, float second)
    {
        var mono = new float[2];
        Assert.Equal(2, Stereo.Extract(channel, mono));
        Assert.Equal(first, mono[0], 5);
        Assert.Equal(second, mono[1], 5);
    }

    [Theory]
    [InlineData(MicChannel.Mix)]
    [InlineData(MicChannel.Left)]
    [InlineData(MicChannel.Right)]
    public void MonoDevice_AlwaysReadsItsOnlyChannel(MicChannel channel)
    {
        var mono = new float[3];
        new MicBlock(new[] { 0.2f, 0.4f, 0.6f }, 3, 1, 0).Extract(channel, mono);
        Assert.Equal(new[] { 0.2f, 0.4f, 0.6f }, mono);
    }
}
