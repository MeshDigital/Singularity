using System;
using System.Collections.Generic;
using System.Linq;

namespace Singularity.Configuration;

/// <summary>
/// Process-wide switches from the command line and environment, decided once at startup.
/// </summary>
public static class RuntimeOptions
{
    public const string OfflineFlag = "--offline";
    public const string OfflineEnvironmentVariable = "SINGULARITY_OFFLINE";

    /// <summary>
    /// No Soulseek connection (no auto-connect, no login, no reconnect) and no startup network
    /// checks: for development runs, where the stored credentials must not log in. Set with
    /// <c>--offline</c> or <c>SINGULARITY_OFFLINE=1</c>; enforced in ConnectionLifecycleService,
    /// the one place every connect goes through.
    /// </summary>
    public static bool Offline { get; internal set; }

    /// <summary>Development shortcut: page to open at startup (<c>--open-page Karaoke</c>).</summary>
    public static string? OpenPage { get; private set; }

    /// <summary>Development shortcut: song folder to start singing at startup (<c>--sing "D:\Songs\Artist - Title"</c>).</summary>
    public static string? SingFolder { get; private set; }

    /// <summary>Development shortcut: with --sing, start this many seconds into the song (<c>--sing-start 200</c>).</summary>
    public static double? SingStartSeconds { get; private set; }

    /// <summary>Development shortcut: <c>--players 2</c> adds a second singer on player 1's microphone for this run only.</summary>
    public static int? Players { get; private set; }

    /// <summary>
    /// Development shortcut: <c>--stage "x,y"</c> shows the stage on the display at that desktop position for
    /// this run; <c>--stage window</c> shows it in an ordinary window, to preview the projector on one screen.
    /// </summary>
    public static string? StageScreen { get; private set; }

    public static void Initialize(IEnumerable<string> args)
    {
        var list = args.ToList();
        string? ValueAfter(string flag)
        {
            int i = list.FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
        }

        Offline = list.Any(a => a.Equals(OfflineFlag, StringComparison.OrdinalIgnoreCase))
                  || Environment.GetEnvironmentVariable(OfflineEnvironmentVariable) is "1" or "true" or "TRUE" or "True";
        OpenPage = ValueAfter("--open-page");
        SingFolder = ValueAfter("--sing");
        StageScreen = ValueAfter("--stage");
        Players = int.TryParse(ValueAfter("--players"), out var players) ? players : null;
        SingStartSeconds = double.TryParse(ValueAfter("--sing-start"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var start) ? start : null;
    }
}
