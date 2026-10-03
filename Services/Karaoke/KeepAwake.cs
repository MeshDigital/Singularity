using System;
using System.Runtime.InteropServices;

namespace Singularity.Services.Karaoke;

/// <summary>
/// Keeps Windows from sleeping while long background work runs (the screen may still turn off), via
/// a power request: the mechanism video players use, listed by "powercfg /requests" with its reason.
/// Disposing ends the request. A no-op off Windows or if the request can't be made.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private IntPtr _request;

    private KeepAwake(IntPtr request) => _request = request;

    public static KeepAwake Start(string reason)
    {
        if (!OperatingSystem.IsWindows()) return new KeepAwake(IntPtr.Zero);
        var reasonText = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = 0, Flags = PowerRequestContextSimpleString, SimpleReasonString = reasonText };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1)) return new KeepAwake(IntPtr.Zero);
            if (!PowerSetRequest(request, PowerRequestSystemRequired))
            {
                CloseHandle(request);
                return new KeepAwake(IntPtr.Zero);
            }
            return new KeepAwake(request);
        }
        finally
        {
            Marshal.FreeHGlobal(reasonText); // the request keeps its own copy
        }
    }

    public bool IsActive => _request != IntPtr.Zero;

    public void Dispose()
    {
        var request = _request;
        _request = IntPtr.Zero;
        if (request == IntPtr.Zero) return;
        PowerClearRequest(request, PowerRequestSystemRequired);
        CloseHandle(request);
    }

    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
