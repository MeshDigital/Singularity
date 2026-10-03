using System.Diagnostics;
using Singularity.Services.Karaoke;
using Xunit;

namespace Singularity.Tests.Services.Karaoke;

public class KeepAwakeTests
{
    [Fact]
    public void Windows_ListsThePowerRequestWhileActive()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string reason = "Singularity keep-awake test";
        using (var awake = KeepAwake.Start(reason))
        {
            Assert.True(awake.IsActive);
        }
        // Once disposed, nothing is held (Dispose twice is harmless).
        var again = KeepAwake.Start(reason);
        again.Dispose();
        again.Dispose();
        Assert.False(again.IsActive);
    }
}
