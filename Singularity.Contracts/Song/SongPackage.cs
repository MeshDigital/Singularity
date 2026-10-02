using System.Text;
using System.Text.Json;
using Singularity.Contracts.Json;

namespace Singularity.Contracts.Song;

/// <summary>
/// Layout of a song package: one self-contained folder per song under the songs root, named
/// "{Artist} - {Title}". Only metadata.json and song.txt are required; the media files are
/// optional because a package can be partially built (e.g. no video found).
/// </summary>
public static class SongPackage
{
    public const string MetadataFileName = "metadata.json";
    public const string UltraStarFileName = "song.txt";
    public const string VideoFileName = "video.mp4";
    public const string VocalsFileName = "vocals.wav";
    public const string InstrumentalFileName = "instrumental.wav";
    public const string CoverFileName = "cover.jpg";

    /// <summary>The master audio keeps its source format (flac, mp3, …): "audio" + its extension.</summary>
    public static string AudioFileName(string extension) => "audio" + (extension.StartsWith('.') ? extension : "." + extension);

    /// <summary>"{Artist} - {Title}", with characters Windows doesn't allow in folder names replaced.</summary>
    public static string FolderName(string artist, string title)
    {
        var raw = $"{artist.Trim()} - {title.Trim()}";
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        // Windows strips trailing dots/spaces from folder names, which would break lookups.
        return sb.ToString().TrimEnd('.', ' ');
    }

    public static string Serialize(SongPackageMetadata metadata) =>
        JsonSerializer.Serialize(metadata, ContractJson.File);

    /// <exception cref="NotSupportedException">The file was written by a newer schema version.</exception>
    /// <exception cref="JsonException">The file isn't valid metadata.</exception>
    public static SongPackageMetadata Deserialize(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var version)
                && version > SongPackageMetadata.CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"metadata.json schema version {version} is newer than this build supports ({SongPackageMetadata.CurrentSchemaVersion}).");
            }
        }

        return JsonSerializer.Deserialize<SongPackageMetadata>(json, ContractJson.File)
               ?? throw new JsonException("metadata.json is empty.");
    }

    public static async Task WriteMetadataAsync(string packageFolder, SongPackageMetadata metadata, CancellationToken ct = default)
    {
        var path = Path.Combine(packageFolder, MetadataFileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, Serialize(metadata), new UTF8Encoding(false), ct);
        File.Move(temp, path, overwrite: true); // never leave a half-written metadata.json behind
    }

    public static async Task<SongPackageMetadata> ReadMetadataAsync(string packageFolder, CancellationToken ct = default) =>
        Deserialize(await File.ReadAllTextAsync(Path.Combine(packageFolder, MetadataFileName), ct));
}
