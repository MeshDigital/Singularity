using System.Numerics;

namespace Singularity.Karaoke.Pitch;

/// <summary>
/// In-place iterative radix-2 FFT for one fixed size. The bit-reversal permutation and twiddle
/// factors are computed once, so a transform allocates nothing. One instance per detector.
/// </summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int[] _reversed;
    private readonly Complex[] _twiddles; // e^(-2πik/n) for k < n/2

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.", nameof(n));
        _n = n;
        _reversed = new int[n];
        int bits = System.Numerics.BitOperations.Log2((uint)n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _reversed[i] = r;
        }
        _twiddles = new Complex[n / 2];
        for (int k = 0; k < n / 2; k++) _twiddles[k] = Complex.FromPolarCoordinates(1, -2 * Math.PI * k / n);
    }

    public void Transform(Complex[] data, bool inverse)
    {
        if (data.Length != _n) throw new ArgumentException($"Expected {_n} values.", nameof(data));

        for (int i = 0; i < _n; i++)
        {
            int j = _reversed[i];
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (int len = 2; len <= _n; len <<= 1)
        {
            int half = len / 2, stride = _n / len;
            for (int start = 0; start < _n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    var w = _twiddles[k * stride];
                    if (inverse) w = Complex.Conjugate(w);
                    var even = data[start + k];
                    var odd = data[start + k + half] * w;
                    data[start + k] = even + odd;
                    data[start + k + half] = even - odd;
                }
            }
        }

        if (inverse)
            for (int i = 0; i < _n; i++) data[i] /= _n;
    }
}
