using System;
using System.IO;

namespace Singularity.Data;

/// <summary>
/// The one place the library database path is decided. <c>SINGULARITY_DB_PATH</c> overrides it — the
/// test project sets that to a throwaway database, because tests that opened
/// <c>new AppDbContext()</c> used to read and write the user's real library (2026-09-29: two
/// test temp folders were left behind as enabled Library Sources and shared on Soulseek).
/// </summary>
public static class OrbitPaths
{
    public const string DbPathEnvironmentVariable = "SINGULARITY_DB_PATH";

    public static string LibraryDbPath =>
        Environment.GetEnvironmentVariable(DbPathEnvironmentVariable) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Singularity", "library.db");
}
