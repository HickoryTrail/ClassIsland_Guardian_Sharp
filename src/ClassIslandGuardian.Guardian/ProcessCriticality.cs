using System.Runtime.InteropServices;

namespace ClassIslandGuardian.Guardian;

internal static class ProcessCriticality
{
    public static bool TrySet(bool critical, out int status)
    {
        status = RtlSetProcessIsCritical(critical, IntPtr.Zero, false);
        return status == 0;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlSetProcessIsCritical([MarshalAs(UnmanagedType.Bool)] bool newValue, IntPtr oldValue, [MarshalAs(UnmanagedType.Bool)] bool needScb);
}
