using System.Diagnostics;
using Microsoft.Win32;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public sealed class ClassIslandProcessManager
{
    private readonly FileLog _log;
    private string? _runtimeProcessName;

    public ClassIslandProcessManager(FileLog log)
    {
        _log = log;
    }

    public int Count(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        return Process.GetProcessesByName(name).Length;
    }

    public string GetRuntimeProcessName(GuardianConfiguration configuration) => _runtimeProcessName ?? configuration.ClassIslandProcessName;

    public bool Kill(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        foreach (var process in Process.GetProcessesByName(name))
        {
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
                _log.Warn($"Failed to end {processName}: {exception.Message}");
                return false;
            }
            finally
            {
                process.Dispose();
            }
        }

        return true;
    }

    public bool Start(GuardianConfiguration configuration, bool asActiveUser)
    {
        RemoveImageFileExecutionOptions(configuration.ClassIslandProcessName);
        var launcher = Path.Combine(configuration.ClassIslandPath, configuration.ClassIslandLauncherName);
        var application = FindApplicationExecutable(configuration);
        if (!File.Exists(launcher) || application is null)
        {
            _log.Warn("ClassIsland executable files are missing.");
            return false;
        }

        if (TryStart(launcher, asActiveUser) && WaitForSingleProcess(configuration.ClassIslandProcessName))
        {
            _runtimeProcessName = null;
            return true;
        }

        _log.Warn("ClassIsland launcher failed; trying the application executable.");
        if (TryStart(application, asActiveUser) && WaitForSingleProcess(configuration.ClassIslandProcessName))
        {
            _runtimeProcessName = null;
            return true;
        }

        return false;
    }

    public bool Restart(GuardianConfiguration configuration, bool asActiveUser)
    {
        if (!Kill(GetRuntimeProcessName(configuration)))
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
            if (TryStart(copiedLauncher, asActiveUser) && WaitForSingleProcess(configuration.ClassIslandProcessName))
            {
                _runtimeProcessName = null;
                started = true;
                return true;
            }
            if (TryStart(copiedApplication, asActiveUser) && WaitForSingleProcess(configuration.ClassIslandProcessName))
            {
                _runtimeProcessName = null;
                started = true;
                return true;
            }

            var applicationDirectory = Path.GetDirectoryName(copiedApplication)!;
            var renamedExe = Path.Combine(applicationDirectory, $"tmp_{Random.Shared.NextInt64():x}.exe");
            File.Copy(copiedApplication, renamedExe, overwrite: true);
            var renamedExeName = Path.GetFileName(renamedExe);
            if (TryStart(renamedExe, asActiveUser) && WaitForSingleProcess(renamedExeName))
            {
                _runtimeProcessName = renamedExeName;
                started = true;
                return true;
            }

            var renamedCom = Path.Combine(applicationDirectory, $"tmp_{Random.Shared.NextInt64():x}.com");
            File.Copy(copiedApplication, renamedCom, overwrite: true);
            var renamedComName = Path.GetFileName(renamedCom);
            if (TryStart(renamedCom, asActiveUser) && WaitForSingleProcess(renamedComName))
            {
                _runtimeProcessName = renamedComName;
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

    private bool WaitForSingleProcess(string processName)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            if (Count(processName) == 1)
            {
                return true;
            }
        }

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
}
