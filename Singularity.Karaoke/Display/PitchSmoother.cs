namespace Singularity.Karaoke.Display;

/// <summary>
/// Steadies the singer's pitch for display: a 3-reading median drops single-frame spikes (a consonant read
/// as an octave jump), then an exponential moving average takes out the flutter. A new note (a jump of more
/// than <see cref="JumpSemitones"/>) or a break in the voice starts afresh, so the dot never slides between
/// notes. Display only: scoring judges the readings themselves.
/// </summary>
public sealed class PitchSmoother
{
    public const double Alpha = 0.35;
    public const double JumpSemitones = 2.5;

    /// <summary>Within this many semitones of the target, the dot is drawn halfway towards it.</summary>
    public const double SnapSemitones = 0.35;

    private readonly double[] _window = new double[3];
    private int _count;
    private double? _smoothed;

    /// <summary>Adds a reading (already folded to the target's octave); null is silence. Returns the smoothed pitch.</summary>
    public double? Add(double? midi)
    {
        if (midi is not { } m || double.IsNaN(m))
        {
            Reset();
            return null;
        }
        if (_smoothed is { } s && Math.Abs(m - s) > JumpSemitones && _count > 0 && Math.Abs(m - _window[(_count - 1) % 3]) <= JumpSemitones)
            Reset(keepLast: true); // two readings agree on a new note: start there instead of gliding

        _window[_count % 3] = m;
        _count++;
        double median = _count < 3 ? m : Median(_window[0], _window[1], _window[2]);
        _smoothed = _smoothed is { } prev ? Alpha * median + (1 - Alpha) * prev : median;
        return _smoothed;
    }

    public void Reset() => Reset(keepLast: false);

    private void Reset(bool keepLast)
    {
        double last = _count > 0 ? _window[(_count - 1) % 3] : 0;
        _count = 0;
        _smoothed = null;
        if (keepLast)
        {
            _window[0] = last;
            _count = 1;
            _smoothed = last;
        }
    }

    /// <summary>Pulls a pitch that is nearly on the note halfway onto it, so a held note stays calm.</summary>
    public static double Snap(double midi, double target) =>
        Math.Abs(midi - target) <= SnapSemitones ? target + (midi - target) * 0.5 : midi;

    private static double Median(double a, double b, double c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
