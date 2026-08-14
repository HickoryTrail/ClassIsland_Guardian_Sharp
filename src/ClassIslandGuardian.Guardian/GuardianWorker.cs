using ClassIslandGuardian.Common;
using Microsoft.Extensions.Hosting;

namespace ClassIslandGuardian.Guardian;

public sealed class GuardianWorker : BackgroundService
{
    public const string ManagementEventName = ManagementPauseSignal.EventName;
    private static readonly TimeSpan ManagementPauseGracePeriod = TimeSpan.FromSeconds(6);
    private readonly GuardianDatabase _database;
    private readonly BcdManager _bcd;
    private readonly SnapshotManager _snapshots;
    private readonly ClassIslandProcessManager _processes;
    private readonly GuardianPaths _paths;
    private readonly FileLog _log;
    private bool _isCritical;
    private EventWaitHandle? _managementSignal;
    private DateTime _managementPauseUntilUtc;

    public GuardianWorker(
        GuardianDatabase database,
        BcdManager bcd,
        SnapshotManager snapshots,
        ClassIslandProcessManager processes,
        GuardianPaths paths,
        FileLog log)
    {
        _database = database;
        _bcd = bcd;
        _snapshots = snapshots;
        _processes = processes;
        _paths = paths;
        _log = log;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _managementSignal = ManagementPauseSignal.CreateForService(_log);
        _isCritical = ProcessCriticality.TrySet(critical: true, out var status);
        if (_isCritical)
        {
            _log.Info("Guardian service has been marked as a critical process.");
        }
        else
        {
            _log.Warn($"Could not mark Guardian service as critical: NTSTATUS {status}.");
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            if (_isCritical)
            {
                ProcessCriticality.TrySet(critical: false, out _);
                _isCritical = false;
            }

            _managementSignal?.Dispose();
            _managementSignal = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (File.Exists(Path.Combine(_paths.GuardianDirectory, ".tempstopprotect")))
            {
                File.Delete(Path.Combine(_paths.GuardianDirectory, ".tempstopprotect"));
            }

            GuardianConfiguration? configuration = null;
            while (!stoppingToken.IsCancellationRequested && !_database.TryRead(out configuration))
            {
                _log.Error("Guardian configuration is unavailable; selecting the recovery boot entry.");
                _bcd.SetRecoveryDefault();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            if (configuration is null)
            {
                return;
            }

            _log.Info("ClassIsland Guardian service started.");
            var lastPoll = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!IsProtectionPaused())
                    {
                        var count = _processes.Count(_processes.GetRuntimeProcessName(configuration));
                        if (count == 0)
                        {
                            await RecoverMissingProcessAsync(configuration, stoppingToken);
                        }
                        else if (DateTime.UtcNow - lastPoll >= TimeSpan.FromSeconds(120))
                        {
                            lastPoll = DateTime.UtcNow;
                            if (count >= 2)
                            {
                                _log.Warn($"Detected {count} ClassIsland processes; restarting ClassIsland.");
                                _processes.Restart(configuration, asActiveUser: true);
                            }
                            else
                            {
                                _log.Info("ClassIsland process check passed.");
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    _log.Error("Unhandled guardian loop error", exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // SCM requested a normal service stop.
        }
        catch (Exception exception)
        {
            _log.Error("Guardian service terminated unexpectedly", exception);
            _bcd.SetRecoveryDefault();
            throw;
        }
    }

    private async Task RecoverMissingProcessAsync(GuardianConfiguration configuration, CancellationToken stoppingToken)
    {
        if (!SessionProcessLauncher.HasActiveUserSession())
        {
            _log.Info("No active user session is available; deferring ClassIsland recovery.");
            return;
        }

        _log.Warn("ClassIsland is not running; trying a normal launch.");
        if (_processes.Start(configuration, asActiveUser: true))
        {
            return;
        }

        _log.Warn("Normal launch failed; creating an automatic snapshot before restoration.");
        _processes.Kill(configuration.ClassIslandProcessName);
        _snapshots.Create(configuration, "自动回滚前生成的快照");
        var recoverySnapshot = _snapshots.List().FirstOrDefault(name => !name.Contains("自动回滚前生成的快照", StringComparison.Ordinal));
        if (recoverySnapshot is not null && _snapshots.Restore(configuration, recoverySnapshot) && _processes.Start(configuration, asActiveUser: true))
        {
            return;
        }

        _log.Warn("Snapshot restoration failed; trying escape launch.");
        if (!_processes.EscapeStart(configuration, asActiveUser: true))
        {
            _log.Error("All ClassIsland recovery strategies failed.");
        }

        await Task.CompletedTask;
    }

    private bool IsProtectionPaused()
    {
        if (File.Exists(Path.Combine(_paths.GuardianDirectory, ".stopprotect")) ||
            File.Exists(Path.Combine(_paths.GuardianDirectory, ".tempstopprotect")))
        {
            return true;
        }

        try
        {
            if (_managementSignal?.WaitOne(0) == true)
            {
                _managementPauseUntilUtc = DateTime.UtcNow + ManagementPauseGracePeriod;
            }

            return DateTime.UtcNow < _managementPauseUntilUtc;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
