using System.Numerics;

namespace Singularity.Karaoke.Pitch;

/// <summary>A detected pitch, or silence (<see cref="Hz"/> = 0).</summary>
/// <param name="Clarity">Peak height of the normalised square difference function, 0..1: how periodic the frame is.</param>
/// <param name="LevelDb">Frame RMS in dBFS.</param>
public readonly record struct PitchEstimate(double Hz, double Clarity, double LevelDb)
{
    public bool IsVoiced => Hz > 0;

    /// <summary>Fractional MIDI note number (69 = A4 = 440 Hz); NaN when unvoiced.</summary>
    public double Midi => IsVoiced ? 69 + 12 * Math.Log2(Hz / 440.0) : double.NaN;
}

/// <summary>
/// Monophonic pitch detection for singing, using the McLeod Pitch Method (McLeod and Wyvill, "A smarter
/// way to find pitch", 2005). The normalised square difference function (NSDF) is built from an
/// FFT autocorrelation, so a frame costs O(N log N). The pitch is the first "key maximum" within
/// <see cref="CutoffK"/> of the highest one. Preferring the earliest strong peak, not the highest,
/// is what keeps MPM from the octave-low errors plain autocorrelation makes.
/// One instance per microphone: it reuses its buffers and is not thread-safe.
/// </summary>
public sealed class PitchDetector
{
    /// <summary>A key maximum counts when it reaches this share of the highest one.</summary>
    public const double CutoffK = 0.93;

    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly Complex[] _spectrum;
    private readonly Fft _fft;
    private readonly double[] _nsdf;

    /// <param name="frameSize">Samples per analysis frame, a power of two; 2048 at 48 kHz (43 ms) reaches down to ~50 Hz.</param>
    public PitchDetector(int sampleRate, int frameSize = 2048, double minHz = 65, double maxHz = 1400,
        double silenceDb = -45, double minClarity = 0.6)
    {
        if ((frameSize & (frameSize - 1)) != 0) throw new ArgumentException("Frame size must be a power of two.", nameof(frameSize));
        _sampleRate = sampleRate;
        _frameSize = frameSize;
        _minLag = Math.Max(2, (int)Math.Floor(sampleRate / maxHz));
        _maxLag = Math.Min(frameSize / 2, (int)Math.Ceiling(sampleRate / minHz));
        _spectrum = new Complex[frameSize * 2];
        _fft = new Fft(frameSize * 2);
        _nsdf = new double[_maxLag + 2];
        SilenceDb = silenceDb;
        MinClarity = minClarity;
    }

    public int FrameSize => _frameSize;

    /// <summary>Frames quieter than this are silence, whatever their periodicity.</summary>
    public double SilenceDb { get; set; }

    /// <summary>Frames less periodic than this (breath, consonants, noise) are unvoiced.</summary>
    public double MinClarity { get; set; }

    /// <summary>Analyses one frame of <see cref="FrameSize"/> mono samples in -1..1.</summary>
    public PitchEstimate Detect(ReadOnlySpan<float> frame)
    {
        if (frame.Length != _frameSize) throw new ArgumentException($"Expected {_frameSize} samples.", nameof(frame));

        double energy = 0;
        for (int i = 0; i < frame.Length; i++) energy += frame[i] * (double)frame[i];
        double levelDb = 10 * Math.Log10(energy / frame.Length + 1e-12);
        if (levelDb < SilenceDb) return new PitchEstimate(0, 0, levelDb);

        // Autocorrelation r(t) via FFT, zero-padded to 2N so it is linear, not circular.
        for (int i = 0; i < _spectrum.Length; i++) _spectrum[i] = i < frame.Length ? new Complex(frame[i], 0) : Complex.Zero;
        _fft.Transform(_spectrum, inverse: false);
        for (int i = 0; i < _spectrum.Length; i++)
        {
            var bin = _spectrum[i];
            _spectrum[i] = new Complex(bin.Real * bin.Real + bin.Imaginary * bin.Imaginary, 0); // power spectrum, no sqrt
        }
        _fft.Transform(_spectrum, inverse: true);

        // m(t) = sum over the overlap of x[j]^2 + x[j+t]^2, updated incrementally; nsdf(t) = 2 r(t) / m(t).
        double m = 2 * energy;
        for (int t = 0; t <= _maxLag + 1 && t < _frameSize; t++)
        {
            if (t > 0) m -= frame[t - 1] * (double)frame[t - 1] + frame[_frameSize - t] * (double)frame[_frameSize - t];
            _nsdf[t] = m > 1e-12 ? 2 * _spectrum[t].Real / m : 0;
        }

        // Key maxima: the highest point between each upward and the next downward zero crossing.
        double highest = 0;
        Span<int> peaks = stackalloc int[64];
        int peakCount = 0;
        int t0 = 1;
        while (t0 < _maxLag && _nsdf[t0] > 0) t0++; // skip the lobe around lag 0
        for (int t = t0; t < _maxLag && peakCount < peaks.Length; t++)
        {
            if (_nsdf[t] <= 0 || _nsdf[t - 1] > 0) continue; // find an upward zero crossing
            int best = t;
            for (; t < _maxLag && _nsdf[t] > 0; t++)
                if (_nsdf[t] > _nsdf[best]) best = t;
            if (best >= _minLag)
            {
                peaks[peakCount++] = best;
                highest = Math.Max(highest, _nsdf[best]);
            }
        }
        if (peakCount == 0) return new PitchEstimate(0, 0, levelDb);

        int chosen = -1;
        for (int i = 0; i < peakCount; i++)
        {
            if (_nsdf[peaks[i]] >= CutoffK * highest) { chosen = peaks[i]; break; }
        }
        double clarity = _nsdf[chosen];
        if (clarity < MinClarity) return new PitchEstimate(0, clarity, levelDb);

        // Parabolic interpolation around the peak for sub-sample lag precision.
        double a = _nsdf[chosen - 1], b = _nsdf[chosen], c = _nsdf[chosen + 1];
        double denom = a - 2 * b + c;
        double shift = Math.Abs(denom) > 1e-12 ? 0.5 * (a - c) / denom : 0;
        double lag = chosen + Math.Clamp(shift, -0.5, 0.5);
        return new PitchEstimate(_sampleRate / lag, clarity, levelDb);
    }
}
