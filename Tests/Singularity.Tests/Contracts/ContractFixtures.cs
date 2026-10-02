namespace Singularity.Tests.Contracts;

/// <summary>The shared fixture files from Singularity.Contracts/Fixtures, which the Python worker's tests also parse.</summary>
internal static class ContractFixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Read(string name) => File.ReadAllText(PathOf(name));
}
