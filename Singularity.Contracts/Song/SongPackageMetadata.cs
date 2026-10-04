using System.Text.Json.Serialization;
using Singularity.Contracts.Quality;

namespace Singularity.Contracts.Song;

/// <summary>
/// metadata.json — the Singularity-specific description of a song package folder, alongside the
/// UltraStar song.txt that other players read. All times are integer milliseconds (no floating
/// seconds) so values round-trip exactly; UltraStar's seconds-based #VIDEOGAP is derived on write.
/// </summary>
public sealed record SongPackageMetadata
{
    /// <summary>Schema of this file. Bumped only for breaking changes; readers reject newer versions.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Stable identity of the recording, e.g. "spotify:track:3n3Ppam7vgaVa1iaRUc9Lp".</summary>
    public required string TrackId { get; init; }

    public string? Isrc { get; init; }
    public AudioFingerprint? Fingerprint { get; init; }

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public int? ReleaseYear { get; init; }
    public string? Language { get; init; }

    /// <summary>Length of the master audio (audio.* in the package).</summary>
    public required int DurationMs { get; init; }

    public required TimingDescriptor Timing { get; init; }
    public required QualityAssessment Quality { get; init; }
    public required PipelineProvenance Provenance { get; init; }
}

/// <param name="Chromaprint">Raw fpcalc fingerprint of the master audio.</param>
/// <param name="AcoustId">AcoustID track id, when the lookup matched.</param>
/// <param name="MusicBrainzRecordingId">Recording the AcoustID match points at.</param>
public sealed record AudioFingerprint(string Chromaprint, string? AcoustId = null, string? MusicBrainzRecordingId = null);

/// <param name="Bpm">UltraStar BPM: one beat = 60000 / (Bpm * 4) ms. A timing resolution, not the song's tempo.</param>
/// <param name="GapMs">Offset of beat 0 from the start of the audio (UltraStar #GAP).</param>
/// <param name="VideoGapMs">UltraStar #VIDEOGAP in ms (seconds in song.txt): video position = audio position + gap, so positive skips the start of the video.</param>
/// <param name="VideoStructureValid">
/// The video sync agreed, so the video follows the song at <paramref name="VideoGapMs"/>. False with a
/// video in the package: it isn't synced and plays dimmed from the start, as a backdrop.
/// </param>
public sealed record TimingDescriptor(double Bpm, int GapMs, int VideoGapMs, bool VideoStructureValid);

/// <param name="OverallScore">Weighted score in [0, 1], rounded to 3 decimals. See <see cref="QualityScoring"/>.</param>
public sealed record QualityAssessment(double OverallScore, QualityTier Tier, QualityMetrics Metrics);

/// <summary>Sub-scores in [0, 1] that make up <see cref="QualityAssessment.OverallScore"/>.</summary>
public sealed record QualityMetrics(
    double AudioMatch,
    double LyricAlignment,
    double PitchConfidence,
    double VideoMatch,
    double MetadataConfidence);

/// <summary>Quality tiers, written to JSON as "a_plus", "a", "b", "review_required".</summary>
public enum QualityTier
{
    /// <summary>Q ≥ 0.90: competitive scoring and official video enabled.</summary>
    APlus,
    /// <summary>0.75 ≤ Q &lt; 0.90: fully playable.</summary>
    A,
    /// <summary>0.60 ≤ Q &lt; 0.75: playable, may fall back to cover art.</summary>
    B,
    /// <summary>Q &lt; 0.60: quarantined; only reachable from the correction editor.</summary>
    ReviewRequired,
}

/// <param name="Generator">The app build that wrote the package, e.g. "Singularity/0.1.0".</param>
/// <param name="InferenceEngine">The worker build that produced stems/alignment/pitch, e.g. "singularity-inference/0.1.0".</param>
/// <param name="Models">Model name per pipeline role, e.g. { "separation": "htdemucs_ft" }.</param>
public sealed record PipelineProvenance(
    string Generator,
    string? InferenceEngine,
    IReadOnlyDictionary<string, string> Models,
    DateTime ProcessedAtUtc,
    long ProcessingDurationMs);
