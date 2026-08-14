using System.Security.Cryptography;
using System.Text;
using ClassIslandGuardian.Common;
using Microsoft.Win32;

namespace ClassIslandGuardian.Guardian;

public static class GuardianCommandLine
{
    public static Task<int> RunAsync(string[] args)
    {
        var command = ParseCommand(args);
        return command switch
        {
            GuardianCommand.Install => Task.FromResult(Install()),
            GuardianCommand.Manage => Task.FromResult(Manage()),
            GuardianCommand.Uninstall => Task.FromResult(PrepareUninstall()),
            GuardianCommand.CleanupUninstall => Task.FromResult(CleanupUninstall()),
            GuardianCommand.Help => Task.FromResult(ShowHelp(0)),
            _ => Task.FromResult(ShowHelp(1))
        };
    }

    public static GuardianCommand ParseCommand(IEnumerable<string> arguments)
    {
        var command = arguments.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(command) ||
            string.Equals(command, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "-h", StringComparison.OrdinalIgnoreCase))
        {
            return GuardianCommand.Help;
        }

        if (string.Equals(command, "install", StringComparison.OrdinalIgnoreCase))
        {
            return GuardianCommand.Install;
        }

        if (string.Equals(command, "manage", StringComparison.OrdinalIgnoreCase))
        {
            return GuardianCommand.Manage;
        }

        if (string.Equals(command, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return GuardianCommand.Uninstall;
        }

        return string.Equals(command, "cleanup-uninstall", StringComparison.OrdinalIgnoreCase)
            ? GuardianCommand.CleanupUninstall
            : GuardianCommand.Unknown;
    }

    public static string ComputePasswordHash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    public static bool PasswordMatches(string expectedHash, string password)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return true;
        }

        var actualHash = ComputePasswordHash(password);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actualHash),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    private static int Install()
    {
        if (!EnsureAdministrator())
        {
            return 1;
        }

        var paths = new GuardianPaths();
        var packageDirectory = AppContext.BaseDirectory;
        var log = new FileLog(Path.Combine(paths.GuardianDataDirectory, "guardian.log"));
        var database = new GuardianDatabase(paths, log);
        var commandRunner = new CommandRunner();
        var bcd = new BcdManager(commandRunner, paths, log);

        try
        {
            EnsureFreshInstall(paths);
            Console.Clear();
            Console.WriteLine("ClassIsland Guardian Installer");
            var discoveredPath = FindRunningClassIslandPath();
            Console.Write($"ClassIsland 安装目录{(discoveredPath is null ? string.Empty : $" [{discoveredPath}]")}: ");
            var classIslandPath = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrEmpty(classIslandPath))
            {
                classIslandPath = discoveredPath ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(classIslandPath) || !Directory.Exists(classIslandPath))
            {
                Console.Error.WriteLine("ClassIsland 安装目录不存在。");
                return 1;
            }
            classIslandPath = Path.GetFullPath(classIslandPath);
            var pathConfiguration = new GuardianConfiguration(
                classIslandPath,
                GuardianConfiguration.DefaultProcessName,
                GuardianConfiguration.DefaultLauncherName,
                string.Empty);
            if (!File.Exists(Path.Combine(classIslandPath, GuardianConfiguration.DefaultLauncherName)) ||
                ClassIslandProcessManager.FindApplicationExecutable(pathConfiguration) is null)
            {
                Console.Error.WriteLine("ClassIsland 安装目录缺少 ClassIsland.exe 或有效的 app-* 主程序目录。");
                return 1;
            }

            Console.Write("管理密码（留空表示不启用）: ");
            var password = ReadPassword();
            Console.WriteLine();
            if (!string.IsNullOrEmpty(password))
            {
                Console.Write("确认管理密码: ");
                var confirmation = ReadPassword();
                Console.WriteLine();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(confirmation)))
                {
                    Console.Error.WriteLine("两次密码不一致。");
                    return 1;
                }
            }

            var configuration = new GuardianConfiguration(
                classIslandPath,
                GuardianConfiguration.DefaultProcessName,
                GuardianConfiguration.DefaultLauncherName,
                string.IsNullOrEmpty(password) ? string.Empty : ComputePasswordHash(password));

            var sourceGuardian = Path.Combine(packageDirectory, "guardian.exe");
            var sourceRecovery = Path.Combine(packageDirectory, "recovery", "recovery.wim");
            var sourceDrivers = Path.Combine(packageDirectory, "drivers");
            ValidatePackage(sourceGuardian, sourceRecovery, sourceDrivers);

            var processManager = new ClassIslandProcessManager(log);
            processManager.Kill(configuration.ClassIslandProcessName);
            Directory.CreateDirectory(paths.GuardianDirectory);
            Directory.CreateDirectory(paths.RecoveryDirectory);
            Directory.CreateDirectory(paths.GuardianDataDirectory);
            Directory.CreateDirectory(paths.RecoveryDataDirectory);

            File.Copy(sourceGuardian, paths.GuardianExecutable, overwrite: true);
            database.CreateOrReplace(configuration);
            var recoveryDatabase = new GuardianDatabase(paths, new FileLog(Path.Combine(paths.RecoveryDirectory, "recovery.log")), paths.RecoveryDataDirectory);
            recoveryDatabase.CreateOrReplace(configuration);

            var stableAppData = Path.Combine(paths.RecoveryDirectory, "stable", "appdata");
            Directory.CreateDirectory(stableAppData);
            File.Copy(paths.GuardianExecutable, Path.Combine(stableAppData, "guardian.exe"), overwrite: true);
            FileTree.Copy(sourceDrivers, Path.Combine(paths.RecoveryDirectory, "stable", "drivers"));
            InstallDrivers(commandRunner, paths, sourceDrivers);
            File.Copy(sourceRecovery, paths.RecoveryWim, overwrite: true);
            CreateGuardianService(commandRunner, paths.GuardianExecutable);

            var snapshots = new SnapshotManager(paths, log, processManager);
            snapshots.Create(configuration, "安装时生成的初始快照");
            if (!bcd.CreateRecoveryEntry())
            {
                throw new InvalidOperationException("Recovery BCD entry could not be created.");
            }

            Console.WriteLine("安装完成，重启后生效。");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("安装失败", exception);
            Console.Error.WriteLine($"安装失败: {exception.Message}");
            return 1;
        }
    }

    private static int Manage()
    {
        if (!EnsureAdministrator())
        {
            return 1;
        }

        var paths = new GuardianPaths();
        var log = new FileLog(Path.Combine(paths.GuardianDataDirectory, "guardian.log"));
        var database = new GuardianDatabase(paths, log);
        if (!database.TryRead(out var configuration))
        {
            Console.Error.WriteLine("读取 Guardian 配置失败。");
            return 1;
        }

        if (!CheckPassword(configuration.PasswordHash))
        {
            return 1;
        }

        ManagementPauseLease managementLease;
        try
        {
            managementLease = ManagementPauseSignal.Acquire();
        }
        catch (Exception exception)
        {
            log.Error("无法创建 Guardian 管理通知事件", exception);
            Console.Error.WriteLine("无法通知 Guardian 暂停保护。");
            return 1;
        }

        using (managementLease)
        {
            var snapshots = new SnapshotManager(paths, log, new ClassIslandProcessManager(log));
            while (true)
            {
                Console.Clear();
                Console.WriteLine("ClassIsland Guardian 管理程序\n");
                Console.WriteLine("[1] 保护控制");
                Console.WriteLine("[2] ClassIsland 快照管理");
                Console.WriteLine("[3] 查看日志");
                Console.WriteLine("[4] 卸载 Guardian");
                Console.WriteLine("[0] 退出");
                Console.Write("> ");
                switch (Console.ReadKey(intercept: true).KeyChar)
                {
                    case '1':
                        ManageProtection(paths);
                        break;
                    case '2':
                        ManageSnapshots(snapshots, configuration);
                        break;
                    case '3':
                        OpenLogs(paths);
                        break;
                    case '4':
                        return PrepareUninstall(authenticated: true);
                    case '0':
                        return 0;
                }
            }
        }
    }

    private static int PrepareUninstall(bool authenticated = false)
    {
        if (!EnsureAdministrator())
        {
            return 1;
        }

        var paths = new GuardianPaths();
        var log = new FileLog(Path.Combine(paths.GuardianDataDirectory, "guardian.log"));
        if (!authenticated && !RequireInstalledPassword(paths, log))
        {
            return 1;
        }

        try
        {
            Console.WriteLine("正在准备卸载...");
            var commands = new CommandRunner();
            commands.Run("sc.exe", ["stop", GuardianPaths.ServiceName], throwOnError: false);
            foreach (var service in new[] { GuardianPaths.ServiceName, "file", "process", "registry" })
            {
                DeleteServiceRegistryTree(service);
            }

            File.WriteAllText(Path.Combine(paths.GuardianDirectory, ".uninstall"), string.Empty);
            var taskTarget = $"\"{paths.GuardianExecutable}\" cleanup-uninstall";
            commands.Run("schtasks", ["/Create", "/TN", GuardianPaths.UninstallTaskName, "/TR", taskTarget, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"], throwOnError: false);
            Console.WriteLine("卸载准备完成，重启后将自动清理。");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("准备卸载失败", exception);
            Console.Error.WriteLine($"准备卸载失败: {exception.Message}");
            return 1;
        }
    }

    private static int CleanupUninstall()
    {
        if (!EnsureAdministrator())
        {
            return 1;
        }

        var paths = new GuardianPaths();
        var marker = Path.Combine(paths.GuardianDirectory, ".uninstall");
        if (!File.Exists(marker))
        {
            Console.Error.WriteLine("未检测到卸载标识。请使用 guardian.exe uninstall。");
            return 1;
        }

        var log = new FileLog(Path.Combine(paths.GuardianDataDirectory, "guardian.log"));
        var bcd = new BcdManager(new CommandRunner(), paths, log);
        try
        {
            bcd.RemoveRecoveryEntry();
            FileTree.DeleteIfExists(paths.RecoveryDirectory);
            foreach (var driver in new[] { "file.sys", "process.sys", "registry.sys" })
            {
                var driverPath = Path.Combine(paths.DriversDirectory, driver);
                if (File.Exists(driverPath))
                {
                    File.Delete(driverPath);
                }
            }

            new CommandRunner().Run("schtasks", ["/Delete", "/TN", GuardianPaths.UninstallTaskName, "/F"], throwOnError: false);
            ScheduleSelfDelete(paths.GuardianDirectory);
            Console.WriteLine("卸载完成。");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("卸载失败", exception);
            Console.Error.WriteLine($"卸载失败: {exception.Message}");
            return 1;
        }
    }

    private static void ManageProtection(GuardianPaths paths)
    {
        var temporary = Path.Combine(paths.GuardianDirectory, ".tempstopprotect");
        var persistent = Path.Combine(paths.GuardianDirectory, ".stopprotect");
        Console.Clear();
        Console.WriteLine("[1] 暂时关闭保护（重启后恢复）");
        Console.WriteLine("[2] 关闭保护");
        Console.WriteLine("[3] 重新启动保护");
        Console.WriteLine("[0] 返回");
        Console.Write("> ");
        switch (Console.ReadKey(intercept: true).KeyChar)
        {
            case '1':
                File.WriteAllText(temporary, string.Empty);
                break;
            case '2':
                File.WriteAllText(persistent, string.Empty);
                break;
            case '3':
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(persistent)) File.Delete(persistent);
                break;
        }
    }

    private static void ManageSnapshots(SnapshotManager snapshots, GuardianConfiguration configuration)
    {
        Console.Clear();
        Console.WriteLine("[1] 查看/恢复/删除快照");
        Console.WriteLine("[2] 创建快照");
        Console.WriteLine("[0] 返回");
        Console.Write("> ");
        switch (Console.ReadKey(intercept: true).KeyChar)
        {
            case '1':
                var list = snapshots.List();
                Console.Clear();
                for (var index = 0; index < list.Count; index++) Console.WriteLine($"[{index + 1}] {list[index]}");
                Console.Write("选择快照序号（0 返回）: ");
                if (!int.TryParse(Console.ReadLine(), out var selected) || selected <= 0 || selected > list.Count) return;
                var name = list[selected - 1];
                Console.WriteLine("[1] 恢复 [2] 删除 [0] 返回");
                Console.Write("> ");
                switch (Console.ReadKey(intercept: true).KeyChar)
                {
                    case '1': snapshots.Restore(configuration, name); break;
                    case '2': snapshots.Delete(name); break;
                }
                break;
            case '2':
                snapshots.Create(configuration);
                break;
        }
    }

    private static void OpenLogs(GuardianPaths paths)
    {
        Console.Clear();
        Console.WriteLine("[1] Guardian 日志 [2] Recovery 日志 [0] 返回");
        Console.Write("> ");
        var logPath = Console.ReadKey(intercept: true).KeyChar switch
        {
            '1' => Path.Combine(paths.GuardianDataDirectory, "guardian.log"),
            '2' => Path.Combine(paths.RecoveryDirectory, "recovery.log"),
            _ => null
        };
        if (logPath is not null && File.Exists(logPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
        }
    }

    private static bool CheckPassword(string expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return true;
        }

        while (true)
        {
            Console.Write("请输入管理员密码: ");
            var value = ReadPassword();
            Console.WriteLine();
            if (PasswordMatches(expectedHash, value))
            {
                return true;
            }

            Console.Error.WriteLine("管理员密码不正确，请重试。");
        }
    }

    private static string ReadPassword()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return value.ToString();
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

    private static bool EnsureAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            return true;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("请以管理员身份运行 guardian.exe。");
            return false;
        }

        try
        {
            var arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("管理员权限请求已取消。");
        }
        return false;
    }

    private static void ValidatePackage(string guardian, string recovery, string drivers)
    {
        if (!File.Exists(guardian)) throw new FileNotFoundException("Package guardian.exe was not found.", guardian);
        if (!File.Exists(recovery)) throw new FileNotFoundException("Package recovery.wim was not found.", recovery);
        foreach (var driver in new[] { "file.sys", "process.sys", "registry.sys" })
        {
            if (!File.Exists(Path.Combine(drivers, driver))) throw new FileNotFoundException($"Package {driver} was not found.");
        }
    }

    private static void InstallDrivers(ICommandRunner commands, GuardianPaths paths, string sourceDirectory)
    {
        foreach (var driver in new[] { "file", "process", "registry" })
        {
            var source = Path.Combine(sourceDirectory, driver + ".sys");
            var target = Path.Combine(paths.DriversDirectory, driver + ".sys");
            File.Copy(source, target, overwrite: true);
            commands.Run("sc.exe", ["create", driver, "type=", "kernel", "start=", "boot", "binPath=", target]);
        }

        using var instances = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\file\Instances");
        instances!.SetValue("DefaultInstance", "file_Instance", RegistryValueKind.String);
        using var instance = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\file\Instances\file_Instance");
        instance!.SetValue("Altitude", "328000", RegistryValueKind.String);
        instance.SetValue("Flags", 0, RegistryValueKind.DWord);
    }

    private static void CreateGuardianService(ICommandRunner commands, string executable)
    {
        commands.Run("sc.exe", ["create", GuardianPaths.ServiceName, "type=", "own", "start=", "auto", "binPath=", $"\"{executable}\"", "error=", "critical"]);
        commands.Run("sc.exe", ["failure", GuardianPaths.ServiceName, "reset=", "0", "actions=", "reboot/0"]);
    }

    private static void DeleteServiceRegistryTree(string service)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: true);
        root?.DeleteSubKeyTree(service, throwOnMissingSubKey: false);
    }

    private static void ScheduleSelfDelete(string guardianDirectory)
    {
        var command = $"ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{guardianDirectory}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static int ShowHelp(int exitCode)
    {
        Console.WriteLine("ClassIsland Guardian");
        Console.WriteLine("  guardian.exe install");
        Console.WriteLine("  guardian.exe manage");
        Console.WriteLine("  guardian.exe uninstall");
        return exitCode;
    }

    private static bool RequireInstalledPassword(GuardianPaths paths, FileLog log)
    {
        var database = new GuardianDatabase(paths, log);
        if (!database.TryRead(out var configuration))
        {
            Console.Error.WriteLine("读取 Guardian 配置失败。");
            return false;
        }

        return CheckPassword(configuration.PasswordHash);
    }

    private static void EnsureFreshInstall(GuardianPaths paths)
    {
        if (Directory.Exists(paths.GuardianDirectory) || Directory.Exists(paths.RecoveryDirectory))
        {
            throw new InvalidOperationException("检测到现有 Guardian 安装。此版本仅支持全新安装，请先完整卸载旧版本。");
        }

        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var service in new[] { GuardianPaths.ServiceName, "file", "process", "registry" })
        {
            using var existing = services?.OpenSubKey(service);
            if (existing is not null)
            {
                throw new InvalidOperationException($"检测到现有 {service} 服务。请先完成旧安装的卸载并重启。");
            }
        }
    }

    private static string? FindRunningClassIslandPath()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GuardianConfiguration.DefaultProcessName)).FirstOrDefault();
            var executable = process?.MainModule?.FileName;
            return executable is null ? null : Directory.GetParent(Path.GetDirectoryName(executable)!)?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var quoted = new StringBuilder();
        quoted.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
