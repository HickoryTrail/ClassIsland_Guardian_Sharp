namespace ClassIslandGuardian.Common;

public sealed class GuardianPaths
{
    public const string ServiceName = "guardian";
    public const string ProductName = "ClassIsland Guardian";
    public const string RecoveryName = "ClassIsland Guardian Recovery";
    public const string UninstallTaskName = "ClassIslandGuardianUninstall";

    public GuardianPaths(string? systemDrive = null, string? programFiles = null, string? systemRoot = null, string? recoveryDirectory = null)
    {
        SystemDrive = systemDrive ?? Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        ProgramFiles = programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        SystemRoot = systemRoot ?? Environment.GetEnvironmentVariable("SystemRoot") ?? Path.Combine(SystemDrive, "Windows");
        RecoveryDirectory = recoveryDirectory ?? Path.Combine(SystemDrive + Path.DirectorySeparatorChar, "GuardianRecovery");
    }

    public string SystemDrive { get; }
    public string ProgramFiles { get; }
    public string SystemRoot { get; }
    public string GuardianDirectory => Path.Combine(ProgramFiles, "Guardian");
    public string GuardianDataDirectory => Path.Combine(GuardianDirectory, "data");
    public string GuardianExecutable => Path.Combine(GuardianDirectory, "guardian.exe");
    public string RecoveryDirectory { get; }
    public string RecoveryDataDirectory => Path.Combine(RecoveryDirectory, "data");
    public string RecoveryWim => Path.Combine(RecoveryDirectory, "recovery.wim");
    public string DriversDirectory => Path.Combine(SystemRoot, "System32", "drivers");

    public static GuardianPaths ForRecoveryVolume(string volumeRoot)
    {
        var recoveryDirectory = Path.GetFullPath(volumeRoot);
        var drive = Path.GetPathRoot(recoveryDirectory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    ?? throw new ArgumentException("A volume root is required.", nameof(volumeRoot));
        return new GuardianPaths(drive, Path.Combine(drive, "Program Files"), Path.Combine(drive, "Windows"), recoveryDirectory);
    }
}
