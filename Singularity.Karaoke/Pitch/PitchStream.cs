namespace Singularity.Karaoke.Pitch;

/// <summary>
/// Turns a stream of microphone samples into a pitch reading every <see cref="HopMs"/>, each dated at
/// the centre of its analysis frame. Samples arrive in whatever block size the device delivers, with
/// the time of their first sample. A gap or jump in those times (seek, pause, device restart)
/// starts a fresh buffer.
/// </summary>
public sealed class PitchStream
{
    public const double HopMs = 10;

    private readonly PitchDetector _detector;
    private readonly int _sampleRate;
    private readonly int _hop;
    private readonly float[] _buffer;
    private int _buffered;
    private double _bufferStartMs = double.NaN;

    public PitchStream(int sampleRate, double silenceDb = -55)
    {
        _sampleRate = sampleRate;
        _detector = new PitchDetector(sampleRate, frameSize: FrameSizeFor(sampleRate), silenceDb: silenceDb);
        _hop = (int)Math.Round(sampleRate * HopMs / 1000);
        _buffer = new float[_detector.FrameSize * 4];
    }

    public int SampleRate => _sampleRate;

    /// <summary>Raised for every analysed frame: the time of the frame's centre and its pitch.</summary>
    public event Action<double, PitchEstimate>? Frame;

    /// <param name="samples">Mono samples in -1..1, contiguous with the previous push.</param>
    /// <param name="timeMs">Time of <paramref name="samples"/>[0], on whatever clock the caller uses.</param>
    public void Push(ReadOnlySpan<float> samples, double timeMs)
    {
        double expected = _bufferStartMs + _buffered * 1000.0 / _sampleRate;
        if (double.IsNaN(_bufferStartMs) || Math.Abs(expected - timeMs) > 50)
        {
            _buffered = 0;
            _bufferStartMs = timeMs;
        }

        while (samples.Length > 0)
        {
            int take = Math.Min(samples.Length, _buffer.Length - _buffered);
            samples[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            samples = samples[take..];
            Analyse();
        }
    }

    private void Analyse()
    {
        int frame = _detector.FrameSize;
        int offset = 0;
        while (_buffered - offset >= frame)
        {
            var estimate = _detector.Detect(_buffer.AsSpan(offset, frame));
            Frame?.Invoke(_bufferStartMs + (offset + frame / 2.0) * 1000.0 / _sampleRate, estimate);
            offset += _hop;
        }
        if (offset == 0) return;
        Array.Copy(_buffer, offset, _buffer, 0, _buffered - offset);
        _buffered -= offset;
        _bufferStartMs += offset * 1000.0 / _sampleRate;
    }

    /// <summary>About 43 ms of audio, rounded to a power of two: low enough for bass voices, short enough to follow fast notes.</summary>
    private static int FrameSizeFor(int sampleRate) => 1 << (int)Math.Round(Math.Log2(sampleRate * 0.043));
}

/// <summary>Note names for display: MIDI 57 → "A3".</summary>
public static class NoteNames
{
    private static readonly string[] Names = { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" };

    public static string Name(double midi)
    {
        int n = (int)Math.Round(midi);
        return Names[((n % 12) + 12) % 12] + (n / 12 - 1 - (n < 0 && n % 12 != 0 ? 1 : 0));
    }

    /// <summary>How far off the nearest note, in cents (-50..+50).</summary>
    public static double Cents(double midi) => (midi - Math.Round(midi)) * 100;
}
