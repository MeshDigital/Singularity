namespace Singularity.Karaoke.Companion;

/// <summary>
/// Estimates a phone's clock offset from ping/pong exchanges, NTP style. For one exchange:
/// t0 = PC sends the ping, t1/t2 = the phone receives it and replies (its clock), and t3 = the PC
/// receives the reply. Then offset = ((t1 - t0) + (t2 - t3)) / 2 and round trip = (t3 - t0) - (t2 - t1).
/// The formula assumes both directions take equally long. On Wi-Fi they often don't, and the
/// error is up to half the round trip, so only the fastest exchanges are trusted.
/// </summary>
public sealed class ClockSync
{
    public const int Window = 32;
    public const double FastestShare = 0.25;

    private readonly Queue<(double Offset, double RoundTrip)> _samples = new();

    /// <param name="sentMs">t0, PC clock.</param>
    /// <param name="phoneReceivedMs">t1, phone clock.</param>
    /// <param name="phoneSentMs">t2, phone clock.</param>
    /// <param name="receivedMs">t3, PC clock.</param>
    public void Add(double sentMs, double phoneReceivedMs, double phoneSentMs, double receivedMs)
    {
        double roundTrip = (receivedMs - sentMs) - (phoneSentMs - phoneReceivedMs);
        if (roundTrip < 0) return; // clock went backwards or a garbled reply
        double offset = ((phoneReceivedMs - sentMs) + (phoneSentMs - receivedMs)) / 2;
        _samples.Enqueue((offset, roundTrip));
        while (_samples.Count > Window) _samples.Dequeue();
    }

    public int SampleCount => _samples.Count;

    public bool IsSynced => _samples.Count >= 4;

    /// <summary>Phone clock minus PC clock, from the median of the fastest exchanges.</summary>
    public double OffsetMs
    {
        get
        {
            if (_samples.Count == 0) return 0;
            var fastest = _samples.OrderBy(s => s.RoundTrip).Take(Math.Max(1, (int)Math.Ceiling(_samples.Count * FastestShare)))
                .Select(s => s.Offset).Order().ToArray();
            return fastest[fastest.Length / 2];
        }
    }

    /// <summary>Upper bound on the offset error: half the best round trip.</summary>
    public double UncertaintyMs => _samples.Count == 0 ? double.PositiveInfinity : _samples.Min(s => s.RoundTrip) / 2;

    public double ToPcTime(double phoneMs) => phoneMs - OffsetMs;
}
