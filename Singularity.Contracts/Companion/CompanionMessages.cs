using System.Text.Json;
using System.Text.Json.Serialization;
using Singularity.Contracts.Json;

namespace Singularity.Contracts.Companion;

// The phone companion protocol: JSON text frames over a WebSocket on the local network, one message
// per frame, discriminated by "type". Phones join by scanning a QR code with the PC's address and a
// session code. The phone detects pitch itself and sends compact readings stamped with its own clock;
// the PC maps them onto the song clock using ClockSync (ping/pong). Raw audio never crosses Wi-Fi.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(PitchBatchMessage), "pitch")]
[JsonDerivedType(typeof(RemoteMessage), "remote")]
public abstract record PhoneMessage;

/// <param name="SessionCode">From the QR code; the PC rejects phones that don't know it.</param>
public sealed record HelloMessage(int ProtocolVersion, string DeviceId, string Name, string SessionCode) : PhoneMessage;

/// <summary>Reply to <see cref="PingMessage"/>: when the phone received it and when it replied, on the phone's clock.</summary>
public sealed record PongMessage(int Id, double ReceivedMs, double SentMs) : PhoneMessage;

/// <summary>Pitch readings, batched (e.g. every 50 ms) to keep the frame rate down on busy Wi-Fi.</summary>
public sealed record PitchBatchMessage(long FirstSeq, IReadOnlyList<PitchSample> Samples) : PhoneMessage;

/// <param name="TimeMs">When the analysed audio was sung, on the phone's clock, latency already removed.</param>
/// <param name="Midi">Fractional MIDI note; null for silence.</param>
public sealed record PitchSample(double TimeMs, double? Midi, double Clarity);

public enum RemoteAction
{
    Up,
    Down,
    Left,
    Right,
    Select,
    Back,
    Pause,
}

public sealed record RemoteMessage(RemoteAction Action) : PhoneMessage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(SongStateMessage), "song_state")]
public abstract record PcMessage;

/// <param name="PlayerSlot">1-based singer slot this phone sings for.</param>
/// <param name="Color">The slot's colour as "#rrggbb", so the phone can match the screen.</param>
public sealed record WelcomeMessage(int ProtocolVersion, int PlayerSlot, string Color) : PcMessage;

/// <summary>Clock probe; the phone answers with <see cref="PongMessage"/> straight away.</summary>
public sealed record PingMessage(int Id) : PcMessage;

/// <summary>What the phone shows: the singer's own lyric line and whether a song is running.</summary>
public sealed record SongStateMessage(bool Playing, string? Title = null, string? Artist = null, string? Line = null, string? NextLine = null) : PcMessage;

public static class CompanionProtocol
{
    public const int Version = 1;

    public static string Encode(PcMessage message) => JsonSerializer.Serialize(message, ContractJson.Wire);

    public static string Encode(PhoneMessage message) => JsonSerializer.Serialize(message, ContractJson.Wire);

    /// <exception cref="JsonException">Not a known phone message.</exception>
    public static PhoneMessage DecodePhone(string json) =>
        JsonSerializer.Deserialize<PhoneMessage>(json, ContractJson.Wire) ?? throw new JsonException("Empty message.");

    /// <exception cref="JsonException">Not a known PC message.</exception>
    public static PcMessage DecodePc(string json) =>
        JsonSerializer.Deserialize<PcMessage>(json, ContractJson.Wire) ?? throw new JsonException("Empty message.");
}
