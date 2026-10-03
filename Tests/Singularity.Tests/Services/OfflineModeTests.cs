using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Singularity.Configuration;
using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services;

/// <summary>RuntimeOptions.Offline is process-wide, so these run alone.</summary>
[Collection(NonParallelCollection.Name)]
public class OfflineModeTests
{
    [Fact]
    public async Task Offline_NeverConnectsToSoulseek()
    {
        var soulseek = new Mock<ISoulseekAdapter>();
        var service = new ConnectionLifecycleService(NullLogger<ConnectionLifecycleService>.Instance, soulseek.Object, new EventBusService());
        try
        {
            RuntimeOptions.Offline = true;
            await service.RequestConnectAsync("stored-password");
            soulseek.Verify(s => s.ConnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            RuntimeOptions.Offline = false;
        }
    }

    [Fact]
    public void Initialize_ReadsTheFlag()
    {
        try
        {
            RuntimeOptions.Initialize(new[] { "--OFFLINE" });
            Assert.True(RuntimeOptions.Offline);
            RuntimeOptions.Initialize(Array.Empty<string>());
            Assert.Equal(Environment.GetEnvironmentVariable(RuntimeOptions.OfflineEnvironmentVariable) == "1", RuntimeOptions.Offline);
        }
        finally
        {
            RuntimeOptions.Offline = false;
        }
    }
}
