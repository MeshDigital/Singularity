using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Singularity.Models;

namespace Singularity.Services.Library;

public class PlaylistExportService
{
    private readonly ILogger<PlaylistExportService> _logger;

    public PlaylistExportService(ILogger<PlaylistExportService> logger)
    {
        _logger = logger;
    }

    public async Task ExportToM3uAsync(
        string playlistName,
        IEnumerable<PlaylistTrack> tracks,
        string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to M3U: {Path}", playlistName, targetPath);

            var trackList = tracks
                .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath) && File.Exists(t.ResolvedFilePath))
                .GroupBy(t => t.ResolvedFilePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var lines = new List<string> { "#EXTM3U", $"#PLAYLIST:{playlistName}" };

            foreach (var track in trackList)
            {
                int durationSec = Math.Max(0, track.CanonicalDuration.GetValueOrDefault() / 1000);
                string artist = track.Artist ?? "Unknown Artist";
                string title = track.Title ?? "Unknown Title";
                lines.Add($"#EXTINF:{durationSec},{artist} - {title}");
                lines.Add(track.ResolvedFilePath!);
            }

            await File.WriteAllLinesAsync(targetPath, lines, System.Text.Encoding.UTF8);
            _logger.LogInformation("M3U export completed: {Count} tracks.", trackList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to M3U");
            throw;
        }
    }

}
