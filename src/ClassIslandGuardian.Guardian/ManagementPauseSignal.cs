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
        var security = EventWaitHandleAcl.GetAccessControl(signal);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
            .OfType<EventWaitHandleAccessRule>()
            .ToArray();
        var expectedIdentities = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        var isExpected = security.AreAccessRulesProtected &&
                         rules.Length == expectedIdentities.Length &&
                         expectedIdentities.All(identity => rules.Any(rule =>
                             rule.AccessControlType == AccessControlType.Allow &&
                             rule.EventWaitHandleRights == EventWaitHandleRights.FullControl &&
                             rule.IdentityReference.Equals(identity)));
        if (!isExpected)
        {
            throw new InvalidOperationException("The management event does not have the required protected ACL.");
        }
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
