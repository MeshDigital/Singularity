using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Singularity.Karaoke.Scoring;

namespace Singularity.Services.Karaoke;

/// <summary>The high score table, kept in %APPDATA%\Singularity\highscores.json.</summary>
public sealed class HighScoreStore
{
    private readonly string _path;
    private readonly ILogger<HighScoreStore> _logger;
    private readonly object _lock = new();
    private HighScoreTable? _table;

    public HighScoreStore(ILogger<HighScoreStore> logger, string? path = null)
    {
        _logger = logger;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Singularity", "highscores.json");
    }

    /// <summary>Raised (on any thread) after a score was kept.</summary>
    public event Action? Changed;

    public int Add(HighScore score)
    {
        int place;
        lock (_lock)
        {
            place = Table.Add(score);
            if (place > 0) Save();
        }
        if (place > 0) Changed?.Invoke();
        return place;
    }

    public IReadOnlyList<HighScore> Top(string song, string difficulty, int count = 5)
    {
        lock (_lock) return Table.Top(song, difficulty, count);
    }

    public HighScore? Best(string song)
    {
        lock (_lock) return Table.Best(song);
    }

    private HighScoreTable Table => _table ??= Load();

    private HighScoreTable Load()
    {
        try
        {
            if (File.Exists(_path))
                return new HighScoreTable(JsonSerializer.Deserialize<List<HighScore>>(File.ReadAllText(_path)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "High scores couldn't be read; starting a new table");
        }
        return new HighScoreTable();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(Table.All, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "High scores couldn't be saved");
        }
    }
}
