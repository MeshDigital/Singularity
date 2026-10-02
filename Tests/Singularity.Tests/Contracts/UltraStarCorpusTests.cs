using Singularity.Contracts.UltraStar;
using Xunit;

namespace Singularity.Tests.Contracts;

/// <summary>Runs only when SINGULARITY_SONGS_DIR points at a real UltraStar song collection (read-only).</summary>
public sealed class RequiresSongCollectionFactAttribute : FactAttribute
{
    public RequiresSongCollectionFactAttribute()
    {
        if (UltraStarCorpusTests.SongsDir is null)
            Skip = "set SINGULARITY_SONGS_DIR to an UltraStar songs folder to run";
    }
}

public class UltraStarCorpusTests
{
    public static string? SongsDir =>
        Environment.GetEnvironmentVariable("SINGULARITY_SONGS_DIR") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    [RequiresSongCollectionFact]
    public void EverySongParses_AndRoundTripsThroughTheWriter()
    {
        var failures = new List<string>();
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(SongsDir!, "*.txt", SearchOption.AllDirectories))
        {
            count++;
            try
            {
                var song = UltraStarSerializer.ReadFile(path);
                var again = UltraStarSerializer.Read(UltraStarSerializer.Write(song));
                if (!again.Voices.SelectMany(v => v.Notes).SequenceEqual(song.Voices.SelectMany(v => v.Notes))
                    || again.Bpm != song.Bpm || again.GapMs != song.GapMs || again.VideoGapMs != song.VideoGapMs)
                    failures.Add($"{path}: round trip changed the song");
            }
            catch (FormatException ex)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        Assert.True(count > 0, "no .txt files found");
        Assert.True(failures.Count == 0, $"{failures.Count}/{count} failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
    }
}
