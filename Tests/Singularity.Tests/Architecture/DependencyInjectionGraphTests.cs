using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Singularity.Tests.Architecture;

/// <summary>
/// Builds the app's real service container with validation on, so a service whose constructor
/// needs something that isn't registered fails here instead of at app startup.
/// </summary>
public class DependencyInjectionGraphTests
{
    [Fact]
    public void ServiceGraph_ResolvesEveryRegisteredService()
    {
        var services = new ServiceCollection();
        App.ConfigureSharedServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider);
    }
}
