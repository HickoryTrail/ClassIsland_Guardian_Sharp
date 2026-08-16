using System.Diagnostics;
using Microsoft.Win32;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public sealed class ClassIslandProcessManager
{
    private readonly FileLog _log;
    private string? _runtimeProcessName;
    private string? _runtimeInstallationRoot;

    public ClassIslandProcessManager(FileLog log)
    {
        _log = log;
    }

    public int Count(GuardianConfiguration configuration) => Count(GetRuntimeProcess(configuration));

    public string GetRuntimeProcessName(GuardianConfiguration configuration) => _runtimeProcessName ?? configuration.ClassIslandProcessName;

    public bool Kill(GuardianConfiguration configuration) => Kill(GetRuntimeProcess(configuration));

    public static bool IsExpectedClassIslandExecutable(string? executablePath, string installationRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(installationRoot))
        {
            return false;
        }

        try
        {
            var applicationDirectory = Directory.GetParent(Path.GetFullPath(executablePath));
            var classIslandDirectory = applicationDirectory?.Parent;
            return classIslandDirectory is not null &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(classIslandDirectory.FullName),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot)),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int Count(ProcessIdentity expected)
    {
        var count = 0;
        var name = Path.GetFileNameWithoutExtension(expected.Name);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (IsExpectedProcess(process, expected))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private bool Kill(ProcessIdentity expected)
    {
        var name = Path.GetFileNameWithoutExtension(expected.Name);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (!IsExpectedProcess(process, expected))
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
                catch (Exception exception)
                {
                    _log.Warn($"Failed to end {expected.Name}: {exception.Message}");
                    return false;
                }
            }
        }

        return true;
    }

    public bool Start(GuardianConfiguration configuration, bool asActiveUser)
    {
        return StartCore(
            configuration,
            asActiveUser,
            new ProcessIdentity(configuration.ClassIslandProcessName, configuration.ClassIslandPath),
            recoverDuplicateInstances: true);
    }

    public bool Restart(GuardianConfiguration configuration, bool asActiveUser)
    {
        if (!Kill(GetRuntimeProcess(configuration)))
        {
            return false;
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));
        return Start(configuration, asActiveUser);
    }

    public bool EscapeStart(GuardianConfiguration configuration, bool asActiveUser)
    {
        RemoveImageFileExecutionOptions(configuration.ClassIslandProcessName);
        var application = FindApplicationExecutable(configuration);
        if (application is null)
        {
            return false;
        }

        string tempRoot;
        if (asActiveUser)
        {
            if (!SessionProcessLauncher.TryCreateActiveUserTemporaryDirectory("cig_", out tempRoot, out var error))
            {
                _log.Warn($"Could not create an escape directory for the active user: {error}");
                return false;
            }
        }
        else
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "cig_" + Guid.NewGuid().ToString("N"));
        }

        var started = false;
        try
        {
            CleanupOldEscapeDirectories(tempRoot);
            FileTree.Copy(configuration.ClassIslandPath, tempRoot);
            var relativeApplication = Path.GetRelativePath(configuration.ClassIslandPath, application);
            var copiedApplication = Path.Combine(tempRoot, relativeApplication);
            var copiedLauncher = Path.Combine(tempRoot, configuration.ClassIslandLauncherName);
            var copiedProcess = new ProcessIdentity(configuration.ClassIslandProcessName, tempRoot);
            if (TryStartAndVerify(copiedLauncher, configuration, copiedProcess, asActiveUser, recoverDuplicateInstances: false))
            {
                started = true;
                return true;
            }
            if (TryStartAndVerify(copiedApplication, configuration, copiedProcess, asActiveUser, recoverDuplicateInstances: false))
            {
                started = true;
                return true;
            }

            var applicationDirectory = Path.GetDirectoryName(copiedApplication)!;
            var renamedExe = Path.Combine(applicationDirectory, $"tmp_{Random.Shared.NextInt64():x}.exe");
            File.Copy(copiedApplication, renamedExe, overwrite: true);
            var renamedExeName = Path.GetFileName(renamedExe);
            if (TryStartAndVerify(
                renamedExe,
                configuration,
                new ProcessIdentity(renamedExeName, tempRoot),
                asActiveUser,
                recoverDuplicateInstances: false))
            {
                started = true;
                return true;
            }

            var renamedCom = Path.Combine(applicationDirectory, $"tmp_{Random.Shared.NextInt64():x}.com");
            File.Copy(copiedApplication, renamedCom, overwrite: true);
            var renamedComName = Path.GetFileName(renamedCom);
            if (TryStartAndVerify(
                renamedCom,
                configuration,
                new ProcessIdentity(renamedComName, tempRoot),
                asActiveUser,
                recoverDuplicateInstances: false))
            {
                started = true;
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            _log.Error("Escape launch failed", exception);
            return false;
        }
        finally
        {
            if (!started)
            {
                try
                {
                    FileTree.DeleteIfExists(tempRoot);
                }
                catch (Exception exception)
                {
                    _log.Warn($"Failed to remove the unsuccessful escape directory: {exception.Message}");
                }
            }
        }
    }

    public static string? FindApplicationExecutable(GuardianConfiguration configuration)
    {
        if (!Directory.Exists(configuration.ClassIslandPath))
        {
            return null;
        }

        var candidates = Directory.EnumerateDirectories(configuration.ClassIslandPath, "app-*")
            .Where(static directory => !File.Exists(Path.Combine(directory, ".partial")) && !File.Exists(Path.Combine(directory, ".destroy")))
            .Select(directory => new
            {
                Directory = directory,
                IsCurrent = File.Exists(Path.Combine(directory, ".current")),
                Version = ParseVersion(Path.GetFileName(directory))
            })
            .Where(candidate => File.Exists(Path.Combine(candidate.Directory, configuration.ClassIslandProcessName)))
            .OrderByDescending(candidate => candidate.IsCurrent)
            .ThenByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        return candidates is null ? null : Path.Combine(candidates.Directory, configuration.ClassIslandProcessName);
    }

    public static void RemoveImageFileExecutionOptions(string executableName)
    {
        const string root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(root, writable: true);
            key?.DeleteSubKeyTree(executableName, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // IFEO cleanup is best effort; normal process startup remains possible.
        }
    }

    private bool TryStart(string path, bool asActiveUser)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        RemoveImageFileExecutionOptions(Path.GetFileName(path));
        if (asActiveUser)
        {
            if (!SessionProcessLauncher.TryStart(path, Path.GetDirectoryName(path)!, out var error))
            {
                _log.Warn($"Could not start {path} in the active user session: {error}");
                return false;
            }

            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception)
        {
            _log.Warn($"Could not start {path}: {exception.Message}");
            return false;
        }
    }

    private bool StartCore(
        GuardianConfiguration configuration,
        bool asActiveUser,
        ProcessIdentity expected,
        bool recoverDuplicateInstances)
    {
        RemoveImageFileExecutionOptions(configuration.ClassIslandProcessName);
        var launcher = Path.Combine(configuration.ClassIslandPath, configuration.ClassIslandLauncherName);
        var application = FindApplicationExecutable(configuration);
        if (!File.Exists(launcher) || application is null)
        {
            _log.Warn("ClassIsland executable files are missing.");
            return false;
        }

        if (TryStartAndVerify(launcher, configuration, expected, asActiveUser, recoverDuplicateInstances))
        {
            return true;
        }

        _log.Warn("ClassIsland launcher failed; trying the application executable.");
        return TryStartAndVerify(application, configuration, expected, asActiveUser, recoverDuplicateInstances);
    }

    private bool TryStartAndVerify(
        string path,
        GuardianConfiguration configuration,
        ProcessIdentity expected,
        bool asActiveUser,
        bool recoverDuplicateInstances)
    {
        if (!TryStart(path, asActiveUser))
        {
            return false;
        }

        var count = WaitForProcessCount(expected);
        if (count == 1)
        {
            SetRuntimeProcess(configuration, expected);
            return true;
        }

        if (count >= 2 && recoverDuplicateInstances)
        {
            _log.Warn($"Detected {count} ClassIsland processes after startup; restarting ClassIsland.");
            if (Kill(expected))
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return StartCore(configuration, asActiveUser, expected, recoverDuplicateInstances: false);
            }
        }

        return false;
    }

    private int WaitForProcessCount(ProcessIdentity expected)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            var count = Count(expected);
            if (count == 1 || count >= 2)
            {
                return count;
            }
        }

        return 0;
    }

    private ProcessIdentity GetRuntimeProcess(GuardianConfiguration configuration)
    {
        return new ProcessIdentity(
            _runtimeProcessName ?? configuration.ClassIslandProcessName,
            _runtimeInstallationRoot ?? configuration.ClassIslandPath);
    }

    private void SetRuntimeProcess(GuardianConfiguration configuration, ProcessIdentity process)
    {
        _runtimeProcessName = string.Equals(process.Name, configuration.ClassIslandProcessName, StringComparison.OrdinalIgnoreCase)
            ? null
            : process.Name;
        _runtimeInstallationRoot = string.Equals(
            Path.TrimEndingDirectorySeparator(process.InstallationRoot),
            Path.TrimEndingDirectorySeparator(configuration.ClassIslandPath),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : process.InstallationRoot;
    }

    private bool IsExpectedProcess(Process process, ProcessIdentity expected)
    {
        string? executablePath;
        try
        {
            executablePath = process.MainModule?.FileName;
        }
        catch (Exception)
        {
            executablePath = null;
        }

        if (IsExpectedClassIslandExecutable(executablePath, expected.InstallationRoot))
        {
            return true;
        }

        _log.Warn($"Ignored an untrusted {expected.Name} process at {executablePath ?? "an unavailable executable path"}.");
        return false;
    }

    private static Version ParseVersion(string name)
    {
        return Version.TryParse(name[4..], out var version) ? version : new Version(0, 0);
    }

    private static void CleanupOldEscapeDirectories(string currentDirectory)
    {
        var parent = Path.GetDirectoryName(currentDirectory)!;
        foreach (var directory in Directory.EnumerateDirectories(parent, "cig_*"))
        {
            if (!string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                FileTree.DeleteIfExists(directory);
            }
        }
    }

    private sealed record ProcessIdentity(string Name, string InstallationRoot);
}
