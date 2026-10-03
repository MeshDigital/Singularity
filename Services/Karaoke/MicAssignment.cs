using System;
using System.Collections.Generic;
using Singularity.Configuration;
using Singularity.Karaoke.Audio;

namespace Singularity.Services.Karaoke;

/// <summary>Which device and channel a singer uses. An empty device id means the Windows default microphone.</summary>
public sealed record MicAssignment(int Player, string DeviceId, MicChannel Channel)
{
    /// <summary>Identifies the capture device; players sharing one (a two-mic adapter) share one capture.</summary>
    public string DeviceKey => DeviceId ?? "";

    /// <summary>The configured singers: player 1 always, player 2 when enabled.</summary>
    public static IReadOnlyList<MicAssignment> FromConfig(AppConfig config)
    {
        var players = new List<MicAssignment> { new(1, config.KaraokeMicDeviceId ?? "", Parse(config.KaraokeMicChannel)) };
        if (config.KaraokeMic2Enabled)
            players.Add(new MicAssignment(2, config.KaraokeMic2DeviceId ?? "", Parse(config.KaraokeMic2Channel)));
        else if (RuntimeOptions.Players >= 2)
            players.Add(players[0] with { Player = 2 }); // dev: --players 2
        return players;
    }

    public static MicChannel Parse(string? value) =>
        Enum.TryParse<MicChannel>(value, ignoreCase: true, out var channel) ? channel : MicChannel.Mix;
}
