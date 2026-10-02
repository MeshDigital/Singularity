// ChartBench: runs songs from an UltraStar collection through LRCLIB and the inference worker, and
// scores each generated chart against the song's own human-made song.txt.
//
//   dotnet run --project Tools/ChartBench -c Release -- <songs dir> <output dir> [--sample N] [--limit N] [--filter text]
//
// --sample N spreads N picks evenly over the (alphabetical) collection instead of taking the first ones.
//
// The collection is only read. Stems are cached per song in the output dir, so a re-run after a
// pipeline change only redoes the cheap stages. Writes results.csv and prints per-song lines and medians.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Singularity.Contracts.Inference;
using Singularity.Contracts.UltraStar;
using Singularity.Services.Inference;
using Singularity.Services.Lyrics;
using Singularity.Tools.ChartBench;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ChartBench <songs dir> <output dir> [--limit N] [--filter text]");
    return 2;
}
var songsDir = args[0];
var outDir = Path.GetFullPath(args[1]);
int limit = int.MaxValue;
int? sample = null;
string? filter = null;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--limit") limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--filter") filter = args[++i];
    else if (args[i] == "--sample") sample = int.Parse(args[++i], CultureInfo.InvariantCulture);
}
if (Path.GetFullPath(songsDir).TrimEnd('\\', '/').Equals(outDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
    || outDir.StartsWith(Path.GetFullPath(songsDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("output dir must be outside the songs dir (the collection is read-only)");
    return 2;
}
Directory.CreateDirectory(outDir);

var options = InferenceWorkerOptions.Discover() ?? throw new InvalidOperationException(
    $"inference worker not found; set {InferenceWorkerOptions.PythonEnvironmentVariable}");
await using var host = new InferenceWorkerHost(options with { ReadyTimeout = TimeSpan.FromMinutes(5) }, NullLogger<InferenceWorkerHost>.Instance);
var ready = await host.StartAsync();
Console.WriteLine($"worker on {ready.Device}; models {string.Join(", ", ready.Models.Values)}");

var lrclib = new LrclibClient(new HttpClient(), NullLogger<LrclibClient>.Instance);
var rows = new List<(string Song, string Lyrics, string? Offset, ChartComparison? Result, string? Error, double Seconds)>();

var folders = Directory.GetDirectories(songsDir).Order(StringComparer.OrdinalIgnoreCase)
    .Where(d => filter is null || Path.GetFileName(d).Contains(filter, StringComparison.OrdinalIgnoreCase))
    .ToList();
// One song is benchmarked per group. With --sample the groups are N even slices of the collection,
// so a song without LRCLIB lyrics (or a duet) passes its turn to the next song in the same slice.
var groups = sample is { } n && n < folders.Count
    ? Enumerable.Range(0, n).Select(i => folders.Skip(i * folders.Count / n).Take((i + 1) * folders.Count / n - i * folders.Count / n).ToList()).ToList()
    : folders.Select(f => new List<string> { f }).ToList();

foreach (var group in groups)
foreach (var folder in group)
{
    if (rows.Count >= limit) break;
    int rowsBefore = rows.Count;
    var name = Path.GetFileName(folder);
    var txt = Directory.GetFiles(folder, "*.txt").FirstOrDefault();
    if (txt is null) continue;

    UltraStarSong reference;
    try { reference = UltraStarSerializer.ReadFile(txt); }
    catch (FormatException) { continue; }
    if (reference.IsDuet) continue; // single-voice pipeline
    var audio = Path.Combine(folder, reference.AudioFile);
    if (!File.Exists(audio)) continue;

    var sw = Stopwatch.StartNew();
    try
    {
        int durationMs = ProbeDurationMs(audio);
        var lyrics = await lrclib.FindAsync(reference.Artist, reference.Title, null, durationMs);
        var forWorker = lyrics?.ForWorker();
        if (forWorker is null)
        {
            Console.WriteLine($"skip  {name}: no LRCLIB lyrics");
            continue;
        }
        var lyricsLabel = forWorker.Value.Kind == LyricsKind.Synced ? "synced" : lyrics!.DurationMatches ? "plain" : "plain (other edit)";

        var songOut = Path.Combine(outDir, name);
        Directory.CreateDirectory(songOut);
        string? offset = null;
        var progress = new SyncProgress(e =>
        {
            if (e is LogEvent { Message: var m } && m.StartsWith("LRC timing", StringComparison.Ordinal)) offset = m;
        });
        var result = await host.ProcessTrackAsync(new ProcessTrackCommand(
            "bench", audio, songOut, forWorker.Value.Text, forWorker.Value.Kind, LanguageCode(reference.Language), ReuseStems: true), progress);

        var (bpm, gap, voice) = UltraStarChartBuilder.Build(result.TempoBpm, result.Lines);
        var generated = new UltraStarSong { Title = reference.Title, Artist = reference.Artist, AudioFile = audio, Bpm = bpm, GapMs = gap, Voices = new[] { voice } };
        File.WriteAllText(Path.Combine(songOut, "song.generated.txt"), UltraStarSerializer.Write(generated));

        var c = ChartComparison.Compare(reference, generated);
        rows.Add((name, lyricsLabel, offset, c, null, sw.Elapsed.TotalSeconds));
        var key = c.Transposition == 0 ? "" : $" (ref transposed {c.Transposition:+0;-0}: {c.PitchClassTransposed:P0})";
        Console.WriteLine($"{c.Recall100,4:P0} recall {c.Precision100,4:P0} prec {c.PitchClass,4:P0} pitch {c.Coverage,4:P0} cover  {sw.Elapsed.TotalSeconds,5:0}s  {name}  [{lyricsLabel}]{key}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException and not InferenceWorkerException)
    {
        // One bad song (odd LRCLIB record, unreadable audio, failed task) mustn't end the run.
        rows.Add((name, "-", null, null, ex.Message, sw.Elapsed.TotalSeconds));
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
    if (rows.Count > rowsBefore) break; // this group has its song
}

var ok = rows.Where(r => r.Result is not null).Select(r => r.Result!).ToList();
if (ok.Count > 0)
{
    double Median(Func<ChartComparison, double> f)
    {
        var v = ok.Select(f).Where(x => !double.IsNaN(x)).Order().ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
    Console.WriteLine();
    Console.WriteLine($"{ok.Count} songs (median): recall@100ms {Median(c => c.Recall100):P0}, recall@50ms {Median(c => c.Recall50):P0}, " +
                      $"precision@100ms {Median(c => c.Precision100):P0}, coverage {Median(c => c.Coverage):P0}, " +
                      $"pitch class {Median(c => c.PitchClass):P0} (key-corrected {Median(c => c.PitchClassTransposed):P0}; {ok.Count(c => c.Transposition != 0)} references in another key), " +
                      $"within a semitone {Median(c => c.PitchWithinSemitone):P0}, " +
                      $"onset error {Median(c => c.MedianOnsetErrorMs):+0;-0} ms; failed {rows.Count - ok.Count}");
}

var csv = new StringBuilder("song,lyrics,lrc_offset,ref_notes,gen_notes,recall50,recall100,precision100,onset_err_ms,coverage,pitch_class,pitch_semitone,transposition,pitch_class_transposed,seconds,error\n");
foreach (var (song, kind, offset, c, error, seconds) in rows)
{
    string F(double x) => double.IsNaN(x) ? "" : x.ToString("0.###", CultureInfo.InvariantCulture);
    csv.Append(Quote(song)).Append(',').Append(kind).Append(',').Append(Quote(offset ?? "")).Append(',');
    csv.Append(c is null ? ",,,,,,,,,,," : $"{c.ReferenceNotes},{c.GeneratedNotes},{F(c.Recall50)},{F(c.Recall100)},{F(c.Precision100)},{F(c.MedianOnsetErrorMs)},{F(c.Coverage)},{F(c.PitchClass)},{F(c.PitchWithinSemitone)},{c.Transposition},{F(c.PitchClassTransposed)},");
    csv.Append(F(seconds)).Append(',').Append(Quote(error ?? "")).Append('\n');
}
File.WriteAllText(Path.Combine(outDir, "results.csv"), csv.ToString());
Console.WriteLine($"results: {Path.Combine(outDir, "results.csv")}");
return 0;

static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

static int ProbeDurationMs(string audio)
{
    var psi = new ProcessStartInfo("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{audio}\"")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi)!;
    var text = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    return (int)Math.Round(double.Parse(text, CultureInfo.InvariantCulture) * 1000);
}

static string? LanguageCode(string? language) => language?.Trim().ToLowerInvariant() switch
{
    null or "" => null,
    "english" => "en",
    "german" or "deutsch" => "de",
    "dutch" or "nederlands" => "nl",
    "french" or "français" or "francais" => "fr",
    "spanish" or "español" or "espanol" => "es",
    "italian" or "italiano" => "it",
    "portuguese" => "pt",
    "swedish" => "sv",
    var other when other.Length == 2 => other,
    _ => null,
};

sealed class SyncProgress(Action<WorkerEvent> on) : IProgress<WorkerEvent>
{
    public void Report(WorkerEvent value) => on(value);
}
