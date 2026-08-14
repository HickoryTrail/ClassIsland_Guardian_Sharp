using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Recovery;

public sealed class RecoveryEngine
{
    private readonly ICommandRunner _commands;

    public RecoveryEngine(ICommandRunner commands)
    {
        _commands = commands;
    }

    public int Run(bool reboot)
    {
        var recoveryDirectory = FindRecoveryDirectory();
        if (recoveryDirectory is null)
        {
            Console.Error.WriteLine("未找到可用的 GuardianRecovery 目录。");
            return 1;
        }

        var paths = GuardianPaths.ForRecoveryVolume(recoveryDirectory);
        var log = new FileLog(Path.Combine(paths.RecoveryDirectory, "recovery.log"));
        var bcd = new BcdManager(_commands, paths, log);
        try
        {
            Console.Clear();
            Console.WriteLine("ClassIsland Guardian Recovery");
            log.Info($"Recovery environment found at {paths.RecoveryDirectory}.");

            switch (SelectMode(paths))
            {
                case RecoveryMode.Rollback:
                    log.Info("Rolling back Guardian.");
                    Rollback(paths, log);
                    bcd.SetWindowsDefault();
                    break;
                case RecoveryMode.Update:
                    log.Info("Updating Guardian.");
                    Update(paths, log);
                    bcd.SetRecoveryDefault();
                    bcd.SetWindowsOnce();
                    break;
                default:
                    log.Info("Repairing Guardian from the stable copy.");
                    Repair(paths, log);
                    bcd.SetWindowsDefault();
                    break;
            }

            log.Info("Recovery operation completed.");
            if (reboot)
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                _commands.Run("wpeutil", ["reboot"]);
            }

            return 0;
        }
        catch (Exception exception)
        {
            log.Error("Recovery operation failed", exception);
            Console.Error.WriteLine($"Recovery operation failed: {exception.Message}");
            return 1;
        }
    }

    public static string? FindRecoveryDirectory()
    {
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var directory = $"{letter}:\\GuardianRecovery";
            if (Directory.Exists(directory))
            {
                return directory;
            }
        }

        return null;
    }

    public static RecoveryMode SelectMode(GuardianPaths paths)
    {
        if (File.Exists(Path.Combine(paths.RecoveryDirectory, ".rollback")))
        {
            return RecoveryMode.Rollback;
        }

        return File.Exists(Path.Combine(paths.RecoveryDirectory, ".update"))
            ? RecoveryMode.Update
            : RecoveryMode.Repair;
    }

    internal static void Repair(GuardianPaths paths, FileLog log)
    {
        BackupGuardianLog(paths, log);
        FileTree.DeleteIfExists(paths.GuardianDirectory);
        FileTree.Copy(Path.Combine(paths.RecoveryDirectory, "stable", "appdata"), paths.GuardianDirectory, LogCopy(log, "修复"));
        CopyDrivers(Path.Combine(paths.RecoveryDirectory, "stable", "drivers"), paths.DriversDirectory, log, "修复");
        RestoreGuardianData(paths, log);
        RestoreGuardianLog(paths, log);
    }

    internal static void Update(GuardianPaths paths, FileLog log)
    {
        BackupGuardianLog(paths, log);
        var rollback = Path.Combine(paths.RecoveryDirectory, "rollback");
        FileTree.DeleteIfExists(rollback);
        FileTree.DeleteIfExists(Path.Combine(paths.GuardianDirectory, "data"));
        FileTree.Copy(paths.GuardianDirectory, Path.Combine(rollback, "appdata"), LogCopy(log, "备份"));
        CopyDrivers(paths.DriversDirectory, Path.Combine(rollback, "drivers"), log, "备份");

        FileTree.DeleteIfExists(paths.GuardianDirectory);
        FileTree.Copy(Path.Combine(paths.RecoveryDirectory, "update", "appdata"), paths.GuardianDirectory, LogCopy(log, "更新"));
        RestoreGuardianData(paths, log);
        RestoreGuardianLog(paths, log);
        FileTree.DeleteIfExists(Path.Combine(paths.RecoveryDirectory, "update"));
        File.Delete(Path.Combine(paths.RecoveryDirectory, ".update"));
        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, ".rollback"), string.Empty);
    }

    internal static void Rollback(GuardianPaths paths, FileLog log)
    {
        BackupGuardianLog(paths, log);
        FileTree.DeleteIfExists(paths.GuardianDirectory);
        FileTree.Copy(Path.Combine(paths.RecoveryDirectory, "rollback", "appdata"), paths.GuardianDirectory, LogCopy(log, "回退"));
        CopyDrivers(Path.Combine(paths.RecoveryDirectory, "rollback", "drivers"), paths.DriversDirectory, log, "回退");
        RestoreGuardianData(paths, log);
        RestoreGuardianLog(paths, log);
        File.Delete(Path.Combine(paths.RecoveryDirectory, ".rollback"));
    }

    private static void BackupGuardianLog(GuardianPaths paths, FileLog log)
    {
        var source = Path.Combine(paths.GuardianDataDirectory, "guardian.log");
        var target = Path.Combine(paths.RecoveryDirectory, "guardian.log");
        if (!File.Exists(source))
        {
            return;
        }

        File.Copy(source, target, overwrite: true);
        log.Info("Backed up Guardian log.");
    }

    private static void RestoreGuardianLog(GuardianPaths paths, FileLog log)
    {
        var source = Path.Combine(paths.RecoveryDirectory, "guardian.log");
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(paths.GuardianDataDirectory);
        File.Copy(source, Path.Combine(paths.GuardianDataDirectory, "guardian.log"), overwrite: true);
        File.Delete(source);
        log.Info("Restored Guardian log.");
    }

    private static void RestoreGuardianData(GuardianPaths paths, FileLog log)
    {
        var source = paths.RecoveryDataDirectory;
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        FileTree.Copy(source, paths.GuardianDataDirectory, LogCopy(log, "恢复数据"));
    }

    private static void CopyDrivers(string sourceDirectory, string destinationDirectory, FileLog log, string verb)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var driver in new[] { "file.sys", "process.sys", "registry.sys" })
        {
            var source = Path.Combine(sourceDirectory, driver);
            var target = Path.Combine(destinationDirectory, driver);
            File.Copy(source, target, overwrite: true);
            log.Info($"{verb}驱动: {target}");
        }
    }

    private static Action<string, string> LogCopy(FileLog log, string verb)
    {
        return (_, destination) => log.Info($"{verb}: {destination}");
    }
}

public enum RecoveryMode
{
    Repair,
    Update,
    Rollback
}
