using Singularity.Contracts.UltraStar;

namespace Singularity.Karaoke.Display;

/// <summary>A stretch of song time, in ms.</summary>
public readonly record struct TimeSpanMs(double FromMs, double ToMs)
{
    public bool Contains(double ms) => ms >= FromMs && ms < ToMs;
}

/// <summary>
/// Where the singing is, for the progress bar and the lane: per voice, the stretches with notes to sing (short
/// pauses inside a phrase merged away), and the stretches where the original singer is heard but the chart has no
/// notes (backing vocals, ad-libs, harmonies the chart leaves out), which aren't scored.
/// </summary>
public sealed class SongMap
{
    /// <summary>Pauses shorter than this inside a voice's singing are part of the same stretch.</summary>
    public const double MergeMs = 1500;

    public SongMap(IReadOnlyList<IReadOnlyList<TimeSpanMs>> voices, double endMs)
    {
        Voices = voices;
        EndMs = endMs;
    }

    /// <summary>Per voice (P1, P2), the stretches with notes.</summary>
    public IReadOnlyList<IReadOnlyList<TimeSpanMs>> Voices { get; }

    /// <summary>Where the progress bar ends: the song's #END or its length.</summary>
    public double EndMs { get; }

    /// <summary>Vocals the chart doesn't score; empty until <see cref="FindUnscoredVocals"/> has run (it's set later, from another thread).</summary>
    public IReadOnlyList<TimeSpanMs> Unscored { get; set; } = Array.Empty<TimeSpanMs>();

    public static SongMap For(UltraStarSong song, double endMs) =>
        new(song.Voices.Select(v => (IReadOnlyList<TimeSpanMs>)NoteSpans(song, v, MergeMs)).ToArray(), endMs);

    /// <summary>When the voice has notes sounding (freestyle included), with gaps under <paramref name="mergeMs"/> closed.</summary>
    public static List<TimeSpanMs> NoteSpans(UltraStarSong song, UltraStarVoice voice, double mergeMs)
    {
        var spans = new List<TimeSpanMs>();
        foreach (var n in voice.Notes.Where(n => n.Type != NoteType.LineBreak && n.DurationBeats > 0).OrderBy(n => n.StartBeat))
        {
            double from = song.BeatToMs(n.StartBeat), to = song.BeatToMs(n.StartBeat + n.DurationBeats);
            if (spans.Count > 0 && from - spans[^1].ToMs < mergeMs) spans[^1] = spans[^1] with { ToMs = Math.Max(spans[^1].ToMs, to) };
            else spans.Add(new TimeSpanMs(from, to));
        }
        return spans;
    }

    /// <summary>Analysis frame for the vocal loudness.</summary>
    public const int FrameMs = 50;

    /// <summary>A frame counts as singing within this many dB of the vocals' loud end (their 95th percentile).</summary>
    public const double ActiveBelowLoudDb = 20;

    /// <summary>Unscored singing must last this long to be shown (shorter is breath, bleed or a stray syllable).</summary>
    public const double MinUnscoredMs = 1200;

    /// <summary>Around every charted note this much is left alone: singers start early and hold on.</summary>
    public const double NotePaddingMs = 350;

    /// <summary>
    /// Finds where the separated original vocals are clearly singing while no voice of the chart has a note: those
    /// vocals are heard (with vocals on guide or full) but never scored.
    /// </summary>
    public static List<TimeSpanMs> FindUnscoredVocals(UltraStarSong song, ReadOnlySpan<float> vocals, int sampleRate)
    {
        int frame = sampleRate * FrameMs / 1000;
        int frames = vocals.Length / frame;
        if (frames == 0) return new List<TimeSpanMs>();
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double energy = 0;
            var s = vocals.Slice(f * frame, frame);
            for (int i = 0; i < s.Length; i++) energy += s[i] * (double)s[i];
            db[f] = 10 * Math.Log10(energy / frame + 1e-12);
        }
        var sorted = db.OrderBy(x => x).ToArray();
        double loud = sorted[(int)(sorted.Length * 0.95)];
        double threshold = Math.Max(loud - ActiveBelowLoudDb, -50);

        var charted = song.Voices.SelectMany(v => NoteSpans(song, v, 0))
            .Select(s => new TimeSpanMs(s.FromMs - NotePaddingMs, s.ToMs + NotePaddingMs)).OrderBy(s => s.FromMs).ToArray();
        var found = new List<TimeSpanMs>();
        int c = 0;
        double? runStart = null, lastActive = null;
        for (int f = 0; f < frames; f++)
        {
            double ms = f * (double)FrameMs;
            while (c < charted.Length && charted[c].ToMs <= ms) c++;
            bool inNote = c < charted.Length && charted[c].Contains(ms);
            bool active = db[f] >= threshold && !inNote;
            if (active)
            {
                runStart ??= ms;
                lastActive = ms + FrameMs;
            }
            else if (runStart is { } start && (inNote || ms - lastActive!.Value > 300)) // a short dip doesn't end the stretch
            {
                Close(start, lastActive!.Value);
                runStart = null;
            }
        }
        if (runStart is { } open) Close(open, lastActive!.Value);
        return found;

        void Close(double from, double to)
        {
            if (to - from >= MinUnscoredMs) found.Add(new TimeSpanMs(from, to));
        }
    }

    /// <summary>The stretch of <paramref name="spans"/> at <paramref name="ms"/>, if any.</summary>
    public static TimeSpanMs? At(IReadOnlyList<TimeSpanMs> spans, double ms)
    {
        foreach (var s in spans)
        {
            if (s.FromMs > ms) break;
            if (s.Contains(ms)) return s;
        }
        return null;
    }
}
