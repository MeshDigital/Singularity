using System.Text.Json;
using Singularity.Contracts.Quality;
using Singularity.Contracts.Song;
using Xunit;

namespace Singularity.Tests.Contracts;

public class SongPackageTests
{
    [Fact]
    public void Fixture_Deserializes()
    {
        var m = SongPackage.Deserialize(ContractFixtures.Read("metadata.example.json"));

        Assert.Equal("spotify:track:4u7EnebtmKWzUH433cf5Qv", m.TrackId);
        Assert.Equal("b1a9c0e9-d987-4042-ae91-78d6a3267d69", m.Fingerprint!.MusicBrainzRecordingId);
        Assert.Equal(-340, m.Timing.VideoGapMs);
        Assert.Equal(QualityTier.APlus, m.Quality.Tier);
        Assert.Equal("htdemucs_ft", m.Provenance.Models["separation"]);
        Assert.Equal(DateTimeKind.Utc, m.Provenance.ProcessedAtUtc.Kind);
    }

    [Fact]
    public void Fixture_QualityScoreMatchesRubric()
    {
        // Keeps the example honest: its stored score is what QualityScoring computes from its metrics.
        var m = SongPackage.Deserialize(ContractFixtures.Read("metadata.example.json"));
        var recomputed = QualityScoring.Assess(m.Quality.Metrics);
        Assert.Equal(recomputed.OverallScore, m.Quality.OverallScore);
        Assert.Equal(recomputed.Tier, m.Quality.Tier);
    }

    [Fact]
    public void RoundTrip_IsLossless()
    {
        var original = SongPackage.Deserialize(ContractFixtures.Read("metadata.example.json"));
        var again = SongPackage.Deserialize(SongPackage.Serialize(original));

        Assert.Equal(SongPackage.Serialize(original), SongPackage.Serialize(again));
        Assert.Contains("\"tier\": \"a_plus\"", SongPackage.Serialize(original));
    }

    [Fact]
    public void NewerSchemaVersion_IsRejected()
    {
        var json = ContractFixtures.Read("metadata.example.json").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        Assert.Throws<NotSupportedException>(() => SongPackage.Deserialize(json));
    }

    [Fact]
    public void MissingRequiredField_Throws()
    {
        var json = ContractFixtures.Read("metadata.example.json").Replace("\"trackId\"", "\"trackIdX\"");
        Assert.Throws<JsonException>(() => SongPackage.Deserialize(json));
    }

    [Fact]
    public async Task WriteThenRead_ViaFolder()
    {
        var dir = Directory.CreateTempSubdirectory("singularity-pkg-");
        try
        {
            var m = SongPackage.Deserialize(ContractFixtures.Read("metadata.example.json"));
            await SongPackage.WriteMetadataAsync(dir.FullName, m);

            Assert.False(File.Exists(Path.Combine(dir.FullName, SongPackage.MetadataFileName + ".tmp")));
            var read = await SongPackage.ReadMetadataAsync(dir.FullName);
            Assert.Equal(SongPackage.Serialize(m), SongPackage.Serialize(read));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("AC/DC", "Back In Black", "AC_DC - Back In Black")]
    [InlineData("Queen", "What?", "Queen - What_")]
    [InlineData("  Artist ", "Title...", "Artist - Title")]
    public void FolderName_IsSafe(string artist, string title, string expected) =>
        Assert.Equal(expected, SongPackage.FolderName(artist, title));

    [Fact]
    public void AudioFileName_KeepsExtension()
    {
        Assert.Equal("audio.flac", SongPackage.AudioFileName(".flac"));
        Assert.Equal("audio.mp3", SongPackage.AudioFileName("mp3"));
    }
}
