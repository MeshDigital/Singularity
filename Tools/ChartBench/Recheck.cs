using System.Diagnostics;
using System.Globalization;
using Singularity.Contracts.UltraStar;
using Singularity.Karaoke.Scoring;
using Singularity.Karaoke.Sync;

namespace Singularity.Tools.ChartBench;

/// <summary>
/// Measures the import's note correction without the worker: each benchmarked song's generated chart against the human
/// chart, before and after <see cref="ChartNoteCheck"/> moves the notes the singer (from the cached vocals) sings
/// steadily elsewhere. Prints per song and medians.
/// </summary>
public static class Recheck
{
    public static void Run(string songsDir, string outDir)
    {
        var rows = new List<(string Song, ChartComparison Before, ChartComparison After, int Moved)>();
        var variants = new List<(ChartComparison Middle, ChartComparison Keyed)>();
        var snaps = Thresholds.ToDictionary(t => t, _ => new List<ChartComparison>());
        foreach (var songOut in Directory.GetDirectories(outDir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(songOut);
            var generatedFile = Path.Combine(songOut, "song.generated.txt");
            var vocals = Path.Combine(songOut, "vocals.wav");
            var referenceFile = Directory.Exists(Path.Combine(songsDir, name)) ? Directory.GetFiles(Path.Combine(songsDir, name), "*.txt").FirstOrDefault() : null;
            if (!File.Exists(generatedFile) || !File.Exists(vocals) || referenceFile is null) continue;

            var reference = UltraStarSerializer.ReadFile(referenceFile);
            var generated = UltraStarSerializer.ReadFile(generatedFile);
            var singer = ReferencePitch.FromVocals(Decode(vocals, 16_000), 16_000);
            var check = ChartNoteCheck.Check(generated, singer);
            var corrected = ChartNoteCheck.Apply(generated, check.Corrections);

            var before = ChartComparison.Compare(reference, generated);
            var after = ChartComparison.Compare(reference, corrected);
            rows.Add((name, before, after, check.Corrections.Count));
            variants.Add((ChartComparison.Compare(reference, MiddlePitch(generated, singer, null)),
                ChartComparison.Compare(reference, MiddlePitch(generated, singer, 0.25))));
            foreach (var t in Thresholds)
                snaps[t].Add(ChartComparison.Compare(reference, MiddlePitch(generated, singer, t)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(name.Length > 44 ? name[..44] : name),-44} moved {check.Corrections.Count,3}  semitone {before.PitchWithinSemitone:0.000} -> {after.PitchWithinSemitone:0.000}  class {before.PitchClass:0.000} -> {after.PitchClass:0.000}"));
        }
        if (rows.Count == 0)
        {
            Console.WriteLine("Nothing to recheck: run the bench first.");
            return;
        }
        static double Median(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a[a.Length / 2]; }
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{rows.Count} songs, median notes moved {Median(rows.Select(r => (double)r.Moved)):0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"pitch within a semitone: {Median(rows.Select(r => r.Before.PitchWithinSemitone)):0.000} -> {Median(rows.Select(r => r.After.PitchWithinSemitone)):0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"pitch class:             {Median(rows.Select(r => r.Before.PitchClass)):0.000} -> {Median(rows.Select(r => r.After.PitchClass)):0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"B middle of each note:   semitone {Median(variants.Select(v => v.Middle.PitchWithinSemitone)):0.000}, class {Median(variants.Select(v => v.Middle.PitchClass)):0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"C B + snapped to key:    semitone {Median(variants.Select(v => v.Keyed.PitchWithinSemitone)):0.000}, class {Median(variants.Select(v => v.Keyed.PitchClass)):0.000}"));
        foreach (var t in Thresholds)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  snap beyond {t:0.00}:      semitone {Median(snaps[t].Select(c => c.PitchWithinSemitone)):0.000}, class {Median(snaps[t].Select(c => c.PitchClass)):0.000}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"better / worse / same (semitone): {rows.Count(r => r.After.PitchWithinSemitone > r.Before.PitchWithinSemitone + 0.005)} / {rows.Count(r => r.After.PitchWithinSemitone < r.Before.PitchWithinSemitone - 0.005)} / {rows.Count(r => Math.Abs(r.After.PitchWithinSemitone - r.Before.PitchWithinSemitone) <= 0.005)}"));
    }

    private static readonly double[] Thresholds = { 0.30, 0.35, 0.40, 0.45 };

    private static UltraStarSong MiddlePitch(UltraStarSong chart, ReferencePitch singer, double? snapBeyond) =>
        ChartMelody.FitNotes(chart, singer, snapBeyond).Song;

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
