using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Singularity.Data;
using Singularity.Services;
using Xunit;

namespace Singularity.Tests.Services;

/// <summary>
/// Runs SchemaMigratorService's raw-SQL schema patches against the test run's throwaway database
/// (see TestDatabaseSetup). The patch step swallows its own exceptions and only logs them, so a
/// broken patch would otherwise surface only as a degraded database at app startup.
/// </summary>
public class SchemaPatchTests
{
    [Fact]
    public async Task SchemaPatches_ApplyCleanly_AndDoNotRecreateRemovedTables()
    {
        var logger = new CapturingLogger();
        var migrator = new SchemaMigratorService(logger);

        using var context = new AppDbContext();
        var connection = context.Database.GetDbConnection();
        var apply = typeof(SchemaMigratorService).GetMethod("ApplySchemaPatchesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)apply.Invoke(migrator, new object[] { context, connection })!;

        Assert.Empty(logger.Errors);

        foreach (var table in new[] { "Tracks", "PlaylistTracks", "Projects", "LibraryEntries", "audio_analysis", "audio_features" })
            Assert.True(TableExists(connection, table), $"expected table {table}");

        foreach (var table in new[] { "StemPreferences", "analysis_runs", "TrackPhrases", "GenreCueTemplates", "SetLists", "SetTracks", "RekordboxExportCueSync", "PlaylistTrackTransitions" })
            Assert.False(TableExists(connection, table), $"removed table {table} was recreated");
    }

    private static bool TableExists(System.Data.Common.DbConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private sealed class CapturingLogger : ILogger<SchemaMigratorService>
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception) + (exception is null ? "" : $" — {exception.Message}"));
        }
    }
}
