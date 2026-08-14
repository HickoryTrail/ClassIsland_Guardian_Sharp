using System.Text.RegularExpressions;

namespace ClassIslandGuardian.Common;

public sealed class BcdManager
{
    private static readonly Regex IdentifierPattern = new(@"(?:identifier|标识符)\s+(\{[^}]+\})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ICommandRunner _commands;
    private readonly GuardianPaths _paths;
    private readonly FileLog _log;

    public BcdManager(ICommandRunner commands, GuardianPaths paths, FileLog log)
    {
        _commands = commands;
        _paths = paths;
        _log = log;
    }

    public string? FindRecoveryIdentifier() => FindIdentifier(GuardianPaths.RecoveryName);

    public string? FindWindowsIdentifier()
    {
        try
        {
            var result = _commands.Run("bcdedit", ["/enum"]);
            string? current = null;
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                var identifier = IdentifierPattern.Match(line.Trim());
                if (identifier.Success)
                {
                    current = identifier.Groups[1].Value;
                }

                var value = line.Trim();
                if (current is not null &&
                    (value.StartsWith("description", StringComparison.OrdinalIgnoreCase) || value.StartsWith("描述", StringComparison.Ordinal)) &&
                    value.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                    !value.Contains("Boot Manager", StringComparison.OrdinalIgnoreCase) &&
                    !value.Contains("Recovery", StringComparison.OrdinalIgnoreCase) &&
                    !value.Contains("To Go", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to enumerate Windows BCD entries", exception);
            return null;
        }
    }

    public bool CreateRecoveryEntry()
    {
        try
        {
            if (FindRecoveryIdentifier() is not null)
            {
                _log.Warn("Recovery BCD entry already exists.");
                return false;
            }

            var copy = _commands.Run("bcdedit", ["/copy", "{current}", "/d", GuardianPaths.RecoveryName]);
            var guid = Regex.Match(copy.StandardOutput, @"\{[^}]+\}").Value;
            if (string.IsNullOrWhiteSpace(guid))
            {
                throw new InvalidOperationException("bcdedit did not return a recovery identifier.");
            }

            var ramDisk = $"ramdisk=[{_paths.SystemDrive}]\\GuardianRecovery\\recovery.wim,{{ramdiskoptions}}";
            _commands.Run("bcdedit", ["/set", guid, "device", ramDisk]);
            _commands.Run("bcdedit", ["/set", guid, "osdevice", ramDisk]);
            _commands.Run("bcdedit", ["/set", guid, "winpe", "yes"]);
            _commands.Run("bcdedit", ["/set", guid, "systemroot", "\\Windows"]);
            _commands.Run("bcdedit", ["/set", guid, "detecthal", "yes"]);
            EnsureRamDiskOptions();
            _commands.Run("bcdedit", ["/displayorder", guid, "-addlast"]);
            _commands.Run("bcdedit", ["/timeout", "0"]);
            return true;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to create recovery BCD entry", exception);
            return false;
        }
    }

    public bool SetRecoveryDefault() => SetDefault(FindRecoveryIdentifier());

    public bool SetRecoveryOnce() => SetBootSequence(FindRecoveryIdentifier());

    public bool SetWindowsDefault() => SetDefault(FindWindowsIdentifier());

    public bool SetWindowsOnce() => SetBootSequence(FindWindowsIdentifier());

    public bool RemoveRecoveryEntry()
    {
        var identifier = FindRecoveryIdentifier();
        if (identifier is null)
        {
            return false;
        }

        try
        {
            _commands.Run("bcdedit", ["/delete", identifier]);
            return true;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to remove recovery BCD entry", exception);
            return false;
        }
    }

    private string? FindIdentifier(string description)
    {
        try
        {
            var result = _commands.Run("bcdedit", ["/enum"]);
            string? current = null;
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                var identifier = IdentifierPattern.Match(line.Trim());
                if (identifier.Success)
                {
                    current = identifier.Groups[1].Value;
                }

                if (current is not null && line.Contains(description, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to enumerate BCD entries", exception);
            return null;
        }
    }

    private bool SetDefault(string? identifier)
    {
        if (identifier is null)
        {
            _log.Error("Required BCD entry was not found.");
            return false;
        }

        try
        {
            _commands.Run("bcdedit", ["/default", identifier]);
            return true;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to set the default BCD entry", exception);
            return false;
        }
    }

    private bool SetBootSequence(string? identifier)
    {
        if (identifier is null)
        {
            _log.Error("Required BCD entry was not found.");
            return false;
        }

        try
        {
            _commands.Run("bcdedit", ["/bootsequence", identifier]);
            return true;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to set the one-time BCD entry", exception);
            return false;
        }
    }

    private void EnsureRamDiskOptions()
    {
        var check = _commands.Run("bcdedit", ["/enum", "{ramdiskoptions}"], throwOnError: false);
        var output = check.StandardOutput + check.StandardError;
        if (output.Contains("ramdisksdidevice", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("ramdisksdipath", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!output.Contains("{ramdiskoptions}", StringComparison.OrdinalIgnoreCase))
        {
            _commands.Run("bcdedit", ["/create", "{ramdiskoptions}", "/d", "Ramdisk options"]);
        }

        _commands.Run("bcdedit", ["/set", "{ramdiskoptions}", "ramdisksdidevice", $"partition={_paths.SystemDrive}"]);
        _commands.Run("bcdedit", ["/set", "{ramdiskoptions}", "ramdisksdipath", "\\GuardianRecovery\\boot.sdi"]);

        var primary = Path.Combine(_paths.SystemDrive + Path.DirectorySeparatorChar, "Windows", "Boot", "DVD", "PCAT", "boot.sdi");
        var fallback = Path.Combine(_paths.SystemDrive + Path.DirectorySeparatorChar, "Windows", "Boot", "PCAT", "boot.sdi");
        var source = File.Exists(primary) ? primary : fallback;
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Could not find boot.sdi.", source);
        }

        Directory.CreateDirectory(_paths.RecoveryDirectory);
        File.Copy(source, Path.Combine(_paths.RecoveryDirectory, "boot.sdi"), overwrite: true);
    }
}
