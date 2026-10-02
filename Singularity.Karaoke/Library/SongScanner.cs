using System.Collections.Concurrent;
using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Library;

/// <summary>A playable (or nearly playable) song found on disk.</summary>
/// <param name="TxtPath">The song.txt it was read from.</param>
/// <param name="AudioPath">Null when the audio file is missing; such a song can't be sung.</param>
/// <param name="Problems">Human-readable notes for the library health view (missing files, …).</param>
public sealed record SongEntry(
    string TxtPath,
    UltraStarSong Song,
    string? AudioPath,
    string? VideoPath,
    string? CoverPath,
    string? BackgroundPath,
    IReadOnlyList<string> Problems)
{
    public string Folder => Path.GetDirectoryName(TxtPath)!;
    public bool IsPlayable => AudioPath is not null && Song.Voices.Any(v => v.Notes.Count > 0);
}

/// <summary>A song.txt that couldn't be read at all.</summary>
public sealed record SongScanFailure(string TxtPath, string Error);

public sealed record SongScanResult(IReadOnlyList<SongEntry> Songs, IReadOnlyList<SongScanFailure> Failures);

/// <summary>
/// Finds UltraStar songs under one or more folders, as song select needs them. Files referenced by
/// the headers are resolved case-insensitively. A cover or background that the headers leave out
/// (or point at a missing file) falls back to UltraStar's naming conventions: "... [CO].jpg",
/// "... [BG].jpg", or "cover.jpg" / "background.jpg". Only reads; never changes a song folder.
/// </summary>
public static class SongScanner
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

    public static SongScanResult Scan(IEnumerable<string> roots, CancellationToken ct = default)
    {
        var txts = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.txt", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var songs = new ConcurrentBag<SongEntry>();
        var failures = new ConcurrentBag<SongScanFailure>();
        Parallel.ForEach(txts, new ParallelOptions { CancellationToken = ct }, txt =>
        {
            try
            {
                songs.Add(Resolve(txt, UltraStarSerializer.ReadFile(txt)));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new SongScanFailure(txt, ex.Message));
            }
        });

        return new SongScanResult(
            songs.OrderBy(s => s.Song.Artist, StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(s => s.Song.Title, StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(s => s.TxtPath, StringComparer.OrdinalIgnoreCase).ToList(),
            failures.OrderBy(f => f.TxtPath, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static SongEntry Resolve(string txt, UltraStarSong song)
    {
        var folder = Path.GetDirectoryName(txt)!;
        var files = Directory.GetFiles(folder);
        var problems = new List<string>();

        string? Find(string? name, string what)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null) problems.Add($"{what} file not found: {name}");
            return match;
        }

        string? Convention(string tag, string plain) => files.FirstOrDefault(f =>
            ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
            && (Path.GetFileNameWithoutExtension(f).EndsWith($"[{tag}]", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(f).Equals(plain, StringComparison.OrdinalIgnoreCase)));

        var audio = Find(song.AudioFile, "audio");
        var video = Find(song.VideoFile, "video");
        var cover = Find(song.CoverFile, "cover") ?? Convention("CO", "cover");
        var background = Find(song.BackgroundFile, "background") ?? Convention("BG", "background");
        if (!song.Voices.Any(v => v.Notes.Count > 0)) problems.Add("no notes");

        return new SongEntry(txt, song, audio, video, cover, background, problems);
    }
}
