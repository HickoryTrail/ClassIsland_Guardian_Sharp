using System.Runtime.InteropServices;

namespace ClassIslandGuardian.Guardian;

public static class SessionProcessLauncher
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;
    private const uint TokenAllAccess = 0x000F01FF;

    public static bool TryStart(string executablePath, string workingDirectory, out string? error)
    {
        error = null;
        var impersonationToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var processInfo = new ProcessInformation();
        try
        {
            var sessionId = GetActiveSessionId();
            if (sessionId == InvalidSessionId)
            {
                error = "No active user session is available.";
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out impersonationToken))
            {
                error = $"WTSQueryUserToken failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!DuplicateTokenEx(impersonationToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                error = $"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                error = $"CreateEnvironmentBlock failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default",
                dwFlags = 0,
                wShowWindow = 1
            };
            if (!CreateProcessAsUser(
                    primaryToken,
                    executablePath,
                    $"\"{executablePath}\"",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNewConsole,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                error = $"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }
        finally
        {
            CloseIfValid(processInfo.hThread);
            CloseIfValid(processInfo.hProcess);
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }
            CloseIfValid(primaryToken);
            CloseIfValid(impersonationToken);
        }
    }

    public static bool HasActiveUserSession() => GetActiveSessionId() != InvalidSessionId;

    private static uint GetActiveSessionId()
    {
        var sessions = IntPtr.Zero;
        try
        {
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out sessions, out var count))
            {
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var index = 0; index < count; index++)
                {
                    var address = IntPtr.Add(sessions, index * size);
                    var session = Marshal.PtrToStructure<WtsSessionInfo>(address);
                    if (session.State == WtsActive)
                    {
                        return session.SessionId;
                    }
                }
            }
        }
        finally
        {
            if (sessions != IntPtr.Zero)
            {
                WTSFreeMemory(sessions);
            }
        }

        return WTSGetActiveConsoleSessionId();
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr StationName;
        public int State;
    }

    private const int WtsActive = 0;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr duplicateToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
