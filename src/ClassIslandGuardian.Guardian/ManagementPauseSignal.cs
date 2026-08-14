using System.Security.AccessControl;
using System.Security.Principal;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public static class ManagementPauseSignal
{
    public const string EventName = "Global\\ClassIslandGuardian_ManagementActive";

    public static EventWaitHandle CreateForService(FileLog log)
    {
        try
        {
            return EventWaitHandleAcl.Create(
                initialState: false,
                EventResetMode.AutoReset,
                EventName,
                out _,
                CreateSecurity());
        }
        catch (Exception exception)
        {
            // Do not honor a signal that could not be opened with the expected ACL.
            log.Error("Failed to create the protected management event", exception);
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }
    }

    public static ManagementPauseLease Acquire()
    {
        EventWaitHandle signal;
        if (!EventWaitHandleAcl.TryOpenExisting(
                EventName,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                out var existing))
        {
            signal = EventWaitHandleAcl.Create(
                initialState: false,
                EventResetMode.AutoReset,
                EventName,
                out _,
                CreateSecurity());
        }
        else
        {
            signal = existing;
        }

        return new ManagementPauseLease(signal);
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
