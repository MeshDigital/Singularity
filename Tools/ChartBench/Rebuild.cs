using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Singularity.Contracts.Inference;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;

namespace Singularity.Tools.ChartBench;

/// <summary>The worker's raw output for a song, saved by the bench so charts can be rebuilt without it.</summary>
public sealed record SavedAnalysis(double TempoBpm, IReadOnlyList<LyricLine> Lines);

/// <summary>
/// Rebuilds each benchmarked song's chart from its saved analysis with other chart-builder settings (the minimum grid
/// BPM), applies the import's pitch correction from the singer (<see cref="ChartMelody"/>), and compares with the
/// human chart: timing (note starts within 50 and 100 ms) and pitch, as medians per setting.
/// </summary>
public static class Rebuild
{
    public static void Run(string songsDir, string outDir, IReadOnlyList<double> grids)
    {
        var results = grids.ToDictionary(g => g, _ => new List<ChartComparison>());
        int songs = 0;
        foreach (var songOut in Directory.GetDirectories(outDir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(songOut);
            var analysisFile = Path.Combine(songOut, "analysis.json");
            var vocals = Path.Combine(songOut, "vocals.wav");
            var referenceFile = Directory.Exists(Path.Combine(songsDir, name)) ? Directory.GetFiles(Path.Combine(songsDir, name), "*.txt").FirstOrDefault() : null;
            if (!File.Exists(analysisFile) || referenceFile is null) continue;

            var analysis = JsonSerializer.Deserialize<SavedAnalysis>(File.ReadAllText(analysisFile))!;
            var reference = UltraStarSerializer.ReadFile(referenceFile);
            var singer = File.Exists(vocals) ? ReferencePitch.FromVocals(Decode(vocals, 16_000), 16_000) : null;
            songs++;
            foreach (var grid in grids)
            {
                var (bpm, gap, voice) = UltraStarChartBuilder.Build(analysis.TempoBpm, analysis.Lines, grid);
                var generated = new UltraStarSong { Title = reference.Title, Artist = reference.Artist, AudioFile = "audio", Bpm = bpm, GapMs = gap, Voices = new[] { voice } };
                if (singer is not null) generated = ChartMelody.FitNotes(generated, singer).Song;
                results[grid].Add(ChartComparison.Compare(reference, generated));
            }
        }
        if (songs == 0)
        {
            Console.WriteLine("Nothing to rebuild: run the bench first (it saves analysis.json).");
            return;
        }
        static double Median(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a[a.Length / 2]; }
        Console.WriteLine($"{songs} songs");
        Console.WriteLine("min grid BPM | beat ms | recall50 recall100 precision100 | pitch class within semitone");
        foreach (var grid in grids)
        {
            var r = results[grid];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{grid,12:0} | {60000 / (grid * 4),7:0} | {Median(r.Select(c => c.Recall50)),8:0.000} {Median(r.Select(c => c.Recall100)),9:0.000} {Median(r.Select(c => c.Precision100)),11:0.000} | {Median(r.Select(c => c.PitchClass)),11:0.000} {Median(r.Select(c => c.PitchWithinSemitone)),15:0.000}"));
        }
    }

    private static float[] Decode(string file, int rate)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-i", file, "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        var bytes = ms.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }
}
