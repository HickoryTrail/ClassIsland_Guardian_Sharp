using System.IO.Compression;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public sealed class SnapshotManager
{
    private readonly GuardianPaths _paths;
    private readonly FileLog _log;
    private readonly ClassIslandProcessManager _processes;

    public SnapshotManager(GuardianPaths paths, FileLog log, ClassIslandProcessManager processes)
    {
        _paths = paths;
        _log = log;
        _processes = processes;
    }

    public IReadOnlyList<string> List()
    {
        var directory = SnapshotDirectory;
        return !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*.zip").Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().OrderByDescending(static name => name, StringComparer.Ordinal).ToArray();
    }

    public string? Create(GuardianConfiguration configuration, string? note = null)
    {
        if (!Directory.Exists(configuration.ClassIslandPath))
        {
            return null;
        }

        if (!_processes.Kill(configuration))
        {
            return null;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = string.IsNullOrWhiteSpace(note)
            ? $"snapshot_{timestamp}.zip"
            : $"snapshot_{timestamp}_({SanitizeFileName(note)}).zip";
        foreach (var directory in new[] { SnapshotDirectory, RecoverySnapshotDirectory })
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, fileName);
            using var stream = File.Create(target);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(configuration.ClassIslandPath, "*", SearchOption.AllDirectories))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(configuration.ClassIslandPath, file), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var sourceStream = File.OpenRead(file);
                sourceStream.CopyTo(entryStream);
            }
        }

        _log.Info($"Created snapshot: {fileName}");
        return fileName;
    }

    public bool Restore(GuardianConfiguration configuration, string fileName)
    {
        var snapshot = Path.Combine(SnapshotDirectory, Path.GetFileName(fileName));
        if (!File.Exists(snapshot))
        {
            return false;
        }

        try
        {
            if (!_processes.Kill(configuration))
            {
                return false;
            }
            FileTree.DeleteIfExists(configuration.ClassIslandPath);
            Directory.CreateDirectory(configuration.ClassIslandPath);
            ZipFile.ExtractToDirectory(snapshot, configuration.ClassIslandPath, overwriteFiles: true);
            _log.Info($"Restored snapshot: {fileName}");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to restore snapshot: {fileName}", exception);
            return false;
        }
    }

    public bool Delete(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var deleted = false;
        foreach (var directory in new[] { SnapshotDirectory, RecoverySnapshotDirectory })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
        }

        return deleted;
    }

    public string SnapshotDirectory => Path.Combine(_paths.GuardianDataDirectory, "snapshot");
    public string RecoverySnapshotDirectory => Path.Combine(_paths.RecoveryDataDirectory, "snapshot");

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
