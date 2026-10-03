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

    public static void Initialize(IEnumerable<string> args)
    {
        Offline = args.Any(a => a.Equals(OfflineFlag, StringComparison.OrdinalIgnoreCase))
                  || Environment.GetEnvironmentVariable(OfflineEnvironmentVariable) is "1" or "true" or "TRUE" or "True";
    }
}
