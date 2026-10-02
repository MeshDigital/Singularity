using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services;

public class ChatImageOfferTests
{
    [Theory]
    [InlineData("SINGULARITY_IMG_OFFER|2048|@@share/pics/a.png|a.png")]
    [InlineData("ORBIT_IMG_OFFER|2048|@@share/pics/a.png|a.png")] // offers from ORBIT clients
    public void TryParseImageOffer_AcceptsCurrentAndLegacyPrefix(string message)
    {
        Assert.True(ChatAttachmentService.TryParseImageOffer(message, out var size, out var path, out var name));
        Assert.Equal(2048, size);
        Assert.Equal("@@share/pics/a.png", path);
        Assert.Equal("a.png", name);
    }

    [Fact]
    public void TryParseImageOffer_RejectsPlainText()
    {
        Assert.False(ChatAttachmentService.TryParseImageOffer("hello there", out _, out _, out _));
    }
}
