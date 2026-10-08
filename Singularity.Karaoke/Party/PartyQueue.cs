using System.Text.Json;

namespace Singularity.Karaoke.Party;

/// <summary>A song someone wants to sing.</summary>
/// <param name="Folder">The song's folder: what identifies it in the library.</param>
/// <param name="From">"phone" or "laptop".</param>
public sealed record QueuedSong(Guid Id, string Folder, string Title, string Artist, string Singer, DateTime AddedUtc, string From = "laptop");

/// <summary>
/// Who sings what next at a party: filled from phones and the laptop, shown on the projector and in song select,
/// taken from the top when the next song starts. A singer may have <see cref="MaxPerSinger"/> songs waiting, and can't
/// queue the same song twice, so one enthusiastic phone can't fill the evening. Kept in a file, so a restart doesn't
/// lose the night. Thread-safe: phones add from the web server's threads.
/// </summary>
public sealed class PartyQueue
{
    public const int MaxPerSinger = 3;
    public const int MaxNameLength = 24;

    private readonly object _lock = new();
    private readonly List<QueuedSong> _items = new();
    private readonly string? _path;

    /// <param name="path">Where the queue is kept; null keeps it in memory only.</param>
    public PartyQueue(string? path = null)
    {
        _path = path;
        Load();
    }

    /// <summary>Raised after every change, on whatever thread made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<QueuedSong> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public QueuedSong? Next
    {
        get { lock (_lock) return _items.FirstOrDefault(); }
    }

    /// <summary>Adds a song for a singer; returns why not when it can't be added.</summary>
    public (QueuedSong? Added, string? Refused) Add(string folder, string title, string artist, string singer, string from = "laptop")
    {
        singer = CleanName(singer);
        if (singer.Length == 0) return (null, "Type your name first.");
        QueuedSong added;
        lock (_lock)
        {
            var mine = _items.Where(i => string.Equals(i.Singer, singer, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Any(i => string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                return (null, $"{title} is already in the queue for {singer}.");
            if (mine.Count >= MaxPerSinger)
                return (null, $"{singer} already has {MaxPerSinger} songs waiting. Sing one first!");
            added = new QueuedSong(Guid.NewGuid(), folder, title, artist, singer, DateTime.UtcNow, from);
            _items.Add(added);
            Save();
        }
        Changed?.Invoke();
        return (added, null);
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _items.RemoveAll(i => i.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Moves an entry one place up (towards singing next).</summary>
    public bool MoveUp(Guid id)
    {
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Id == id);
            if (i <= 0) return false;
            (_items[i - 1], _items[i]) = (_items[i], _items[i - 1]);
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Takes the entry off the queue as its song starts.</summary>
    public QueuedSong? Take(Guid id)
    {
        QueuedSong? taken;
        lock (_lock)
        {
            taken = _items.FirstOrDefault(i => i.Id == id);
            if (taken is null) return null;
            _items.Remove(taken);
            Save();
        }
        Changed?.Invoke();
        return taken;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>A name as typed on a phone: trimmed, control characters out, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var items = JsonSerializer.Deserialize<List<QueuedSong>>(File.ReadAllText(_path));
            if (items is not null) _items.AddRange(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged queue file starts an empty queue.
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The queue still works in memory.
        }
    }
}
