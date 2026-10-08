using System.Net;
using Singularity.Services.Karaoke.Party;
using Xunit;

namespace Singularity.Tests.Services;

public class PhoneRemoteServerTests
{
    [Theory]
    [InlineData("192.168.1.23", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.20.4.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:db8::1", false)]
    public void OnlyTheLocalNetwork_IsAnswered(string address, bool local) =>
        Assert.Equal(local, PhoneRemoteServer.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void ASongsId_IsStableAndShort()
    {
        var id = PhoneRemoteServer.IdOf(@"D:\KARAOKE\Singularity\The Killers - Mr. Brightside");
        Assert.Equal(12, id.Length);
        Assert.Equal(id, PhoneRemoteServer.IdOf(@"d:\karaoke\singularity\the killers - mr. brightside"));
        Assert.NotEqual(id, PhoneRemoteServer.IdOf(@"D:\KARAOKE\Singularity\Green Day - Holiday"));
    }
}
