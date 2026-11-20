using System.Runtime.InteropServices;

namespace WPods.Service;

/// <summary>Local change: Windows' user sessions (Remote Desktop Services API).</summary>
internal static partial class Sessions
{
    private const int WtsUserName = 5;

    /// <summary>
    /// Some session has a user logged on (active, locked or disconnected). False while Windows shuts down with Fast
    /// Startup, which logs every user off before it hibernates. True when the sessions cannot be read.
    /// </summary>
    public static unsafe bool AnyUserLoggedOn()
    {
        if (!WTSEnumerateSessionsW(0, 0, 1, out nint list, out int count))
        {
            return true;
        }
        try
        {
            var sessions = (SessionInfo*)list;
            for (int i = 0; i < count; i++)
            {
                if (sessions[i].SessionId == 0
                    || !WTSQuerySessionInformationW(0, sessions[i].SessionId, WtsUserName, out nint name, out int bytes))
                {
                    continue;
                }
                try
                {
                    if (bytes > sizeof(char) && *(char*)name != '\0')
                    {
                        return true;
                    }
                }
                finally
                {
                    WTSFreeMemory(name);
                }
            }
            return false;
        }
        finally
        {
            WTSFreeMemory(list);
        }
    }

    // WTS_SESSION_INFOW
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public int SessionId;
        public nint WinStationName;
        public int State;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessions, out int count);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
