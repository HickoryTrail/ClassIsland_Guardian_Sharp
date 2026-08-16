using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public static class ManagementPauseSignal
{
    public const string EventName = "Global\\ClassIslandGuardian_ManagementActive";

    public static EventWaitHandle CreateForService(FileLog log) => CreateForService(log, EventName);

    internal static EventWaitHandle CreateForService(FileLog log, string eventName)
    {
        try
        {
            return CreateProtectedEvent(eventName);
        }
        catch (Exception exception)
        {
            // Do not honor a pre-existing signal unless it has the expected ACL.
            log.Error("Failed to create the protected management event", exception);
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }
    }

    public static ManagementPauseLease Acquire() => Acquire(EventName);

    internal static ManagementPauseLease Acquire(string eventName)
    {
        EventWaitHandle signal;
        if (!EventWaitHandleAcl.TryOpenExisting(
                eventName,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions,
                out var existing))
        {
            signal = CreateProtectedEvent(eventName);
        }
        else
        {
            signal = existing;
            try
            {
                VerifyExpectedSecurity(signal);
            }
            catch
            {
                signal.Dispose();
                throw;
            }
        }

        return new ManagementPauseLease(signal);
    }

    private static EventWaitHandle CreateProtectedEvent(string eventName)
    {
        var signal = EventWaitHandleAcl.Create(
            initialState: false,
            EventResetMode.AutoReset,
            eventName,
            out _,
            CreateSecurity());
        try
        {
            VerifyExpectedSecurity(signal);
            return signal;
        }
        catch
        {
            signal.Dispose();
            throw;
        }
    }

    private static void VerifyExpectedSecurity(EventWaitHandle signal)
    {
        var security = ReadSecurityDescriptor(signal.SafeWaitHandle);
        var accessControlList = security.DiscretionaryAcl;
        var expectedIdentities = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        var isExpected = (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 &&
                         accessControlList is not null &&
                         accessControlList.Count == expectedIdentities.Length &&
                         expectedIdentities.All(identity => Enumerable.Range(0, accessControlList.Count).Any(index =>
                         {
                             return accessControlList[index] is CommonAce accessRule &&
                                    accessRule.AceFlags == AceFlags.None &&
                                    accessRule.AceQualifier == AceQualifier.AccessAllowed &&
                                    accessRule.AccessMask == (int)EventWaitHandleRights.FullControl &&
                                    accessRule.SecurityIdentifier.Equals(identity);
                         }));
        if (!isExpected)
        {
            throw new InvalidOperationException("The management event does not have the required protected ACL.");
        }
    }

    private static RawSecurityDescriptor ReadSecurityDescriptor(Microsoft.Win32.SafeHandles.SafeWaitHandle handle)
    {
        if (!GetKernelObjectSecurity(handle, SecurityInformation.DiscretionaryAccessControlList, [], 0, out var length) &&
            Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the management event security descriptor.");
        }

        var descriptor = new byte[length];
        if (!GetKernelObjectSecurity(handle, SecurityInformation.DiscretionaryAccessControlList, descriptor, descriptor.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the management event security descriptor.");
        }

        return new RawSecurityDescriptor(descriptor, 0);
    }

    private static EventWaitHandleSecurity CreateSecurity()
    {
        var security = new EventWaitHandleSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            EventWaitHandleRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        Microsoft.Win32.SafeHandles.SafeWaitHandle handle,
        SecurityInformation requestedInformation,
        byte[] securityDescriptor,
        int descriptorLength,
        out int lengthNeeded);

    [Flags]
    private enum SecurityInformation : uint
    {
        DiscretionaryAccessControlList = 0x00000004
    }

    private const int ErrorInsufficientBuffer = 122;
}

public sealed class ManagementPauseLease : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly Timer _timer;
    private bool _disposed;

    internal ManagementPauseLease(EventWaitHandle signal)
    {
        _signal = signal;
        Signal();
        _timer = new Timer(static state => ((ManagementPauseLease)state!).Signal(), this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _signal.Dispose();
    }

    private void Signal()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The management process is exiting.
        }
    }
}
