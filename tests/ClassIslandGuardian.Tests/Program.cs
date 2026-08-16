using ClassIslandGuardian.Common;
using ClassIslandGuardian.Guardian;
using ClassIslandGuardian.Recovery;

namespace ClassIslandGuardian.Tests;

public static class Program
{
    public static int Main()
    {
        try
        {
            DatabaseRoundTripPreservesLegacySchemaSemantics();
            FileLogPreservesLegacyLineLayout();
            PasswordVerificationPreservesLegacySha256Semantics();
            GuardianCommandRoutingIsExplicit();
            ManagementPauseEventRejectsUntrustedExistingEvent();
            SnapshotCreateRestoreAndDelete();
            ApplicationSelectionPrefersCurrentThenNewestVersion();
            ProcessIdentityRequiresConfiguredInstallationRoot();
            BcdManagerParsesEnglishAndChineseEntries();
            BcdManagerRoutesDefaultAndOneTimeCommands();
            RecoveryModeSelectionPrefersRollbackThenUpdate();
            RecoveryRepairRestoresStableProgramAndData();
            RecoveryUpdateAndRollbackPreserveData();
            Console.WriteLine("All ClassIsland Guardian self-tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void DatabaseRoundTripPreservesLegacySchemaSemantics()
    {
        using var fixture = new TemporaryDirectory();
        var paths = new GuardianPaths("C:", Path.Combine(fixture.Path, "Program Files"), Path.Combine(fixture.Path, "Windows"), Path.Combine(fixture.Path, "GuardianRecovery"));
        var database = new GuardianDatabase(paths, new FileLog(Path.Combine(fixture.Path, "guardian.log")));
        var expected = new GuardianConfiguration(@"D:\ClassIsland", "ClassIsland.Desktop.exe", "ClassIsland.exe", "legacy-hash");
        database.CreateOrReplace(expected);

        Assert(database.TryRead(out var actual), "The legacy database schema should be readable.");
        Assert(actual == expected, "Database values changed during the round trip.");
        Assert(File.Exists(database.DatabasePath), "guardian_config.db was not created.");
    }

    private static void SnapshotCreateRestoreAndDelete()
    {
        using var fixture = new TemporaryDirectory();
        var programFiles = Path.Combine(fixture.Path, "Program Files");
        var systemRoot = Path.Combine(fixture.Path, "Windows");
        var paths = new GuardianPaths("C:", programFiles, systemRoot, Path.Combine(fixture.Path, "GuardianRecovery"));
        var classIsland = Path.Combine(fixture.Path, "ClassIsland");
        Directory.CreateDirectory(Path.Combine(classIsland, "app-1.0.0.0"));
        File.WriteAllText(Path.Combine(classIsland, "app-1.0.0.0", "ClassIsland.Desktop.exe"), "original");
        var configuration = new GuardianConfiguration(classIsland, "ClassIsland.Desktop.exe", "ClassIsland.exe", string.Empty);
        var log = new FileLog(Path.Combine(fixture.Path, "guardian.log"));
        var snapshots = new SnapshotManager(paths, log, new ClassIslandProcessManager(log));

        var name = snapshots.Create(configuration, "test");
        Assert(name is not null, "Snapshot creation failed.");
        File.WriteAllText(Path.Combine(classIsland, "app-1.0.0.0", "ClassIsland.Desktop.exe"), "changed");
        Assert(snapshots.Restore(configuration, name!), "Snapshot restore failed.");
        Assert(File.ReadAllText(Path.Combine(classIsland, "app-1.0.0.0", "ClassIsland.Desktop.exe")) == "original", "Snapshot restore did not restore content.");
        Assert(snapshots.Delete(name!), "Snapshot deletion failed.");
    }

    private static void PasswordVerificationPreservesLegacySha256Semantics()
    {
        const string legacyPasswordHash = "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8";
        Assert(GuardianCommandLine.ComputePasswordHash("password") == legacyPasswordHash, "Password hashing changed from the legacy SHA-256 format.");
        Assert(GuardianCommandLine.PasswordMatches(legacyPasswordHash, "password"), "A valid legacy password hash was rejected.");
        Assert(!GuardianCommandLine.PasswordMatches(legacyPasswordHash, "wrong-password"), "An invalid password was accepted.");
        Assert(GuardianCommandLine.PasswordMatches(string.Empty, "anything"), "An empty password configuration should not require a password.");
    }

    private static void FileLogPreservesLegacyLineLayout()
    {
        using var fixture = new TemporaryDirectory();
        var path = Path.Combine(fixture.Path, "guardian.log");
        new FileLog(path).Info("compatibility check");
        var line = File.ReadAllText(path).TrimEnd();
        Assert(
            System.Text.RegularExpressions.Regex.IsMatch(
                line,
                @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] \[info\] \[guardian\] compatibility check$"),
            "Guardian log output no longer uses the legacy timestamp, level, and source layout.");
    }

    private static void GuardianCommandRoutingIsExplicit()
    {
        Assert(GuardianCommandLine.ParseCommand([]) == GuardianCommand.Help, "No command should show help.");
        Assert(GuardianCommandLine.ParseCommand(["INSTALL"]) == GuardianCommand.Install, "Install command routing failed.");
        Assert(GuardianCommandLine.ParseCommand(["manage"]) == GuardianCommand.Manage, "Manage command routing failed.");
        Assert(GuardianCommandLine.ParseCommand(["uninstall"]) == GuardianCommand.Uninstall, "Uninstall command routing failed.");
        Assert(GuardianCommandLine.ParseCommand(["cleanup-uninstall"]) == GuardianCommand.CleanupUninstall, "Cleanup command routing failed.");
        Assert(GuardianCommandLine.ParseCommand(["unexpected"]) == GuardianCommand.Unknown, "Unknown command routing failed.");
    }

    private static void ManagementPauseEventRejectsUntrustedExistingEvent()
    {
        using var fixture = new TemporaryDirectory();
        var eventName = $"ClassIslandGuardian_ManagementActive_Test_{Guid.NewGuid():N}";
        using var untrustedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out var createdNew);
        Assert(createdNew, "The test management event should be newly created.");

        using var serviceEvent = ManagementPauseSignal.CreateForService(new FileLog(Path.Combine(fixture.Path, "guardian.log")), eventName);
        serviceEvent.Set();
        Assert(!untrustedEvent.WaitOne(0), "The service must not accept a pre-existing event with an untrusted ACL.");

        var rejected = false;
        try
        {
            using var lease = ManagementPauseSignal.Acquire(eventName);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "Management must reject an existing event with an untrusted ACL.");
    }

    private static void BcdManagerParsesEnglishAndChineseEntries()
    {
        const string output = "Windows Boot Loader\r\nidentifier              {current}\r\ndescription             Windows 11\r\n\r\nWindows Boot Loader\r\n标识符                  {recovery}\r\n描述                    ClassIsland Guardian Recovery\r\n";
        var commands = new FakeCommandRunner(output);
        var paths = new GuardianPaths("C:", @"C:\Program Files", @"C:\Windows", @"C:\GuardianRecovery");
        var manager = new BcdManager(commands, paths, new FileLog(Path.Combine(Path.GetTempPath(), "guardian-bcd-test.log")));
        Assert(manager.FindRecoveryIdentifier() == "{recovery}", "Recovery BCD identifier parsing failed.");
        Assert(manager.FindWindowsIdentifier() == "{current}", "Windows BCD identifier parsing failed.");
    }

    private static void ApplicationSelectionPrefersCurrentThenNewestVersion()
    {
        using var fixture = new TemporaryDirectory();
        var classIsland = Path.Combine(fixture.Path, "ClassIsland");
        var current = Path.Combine(classIsland, "app-1.0.0.0");
        var newer = Path.Combine(classIsland, "app-2.0.0.0");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(current, ".current"), string.Empty);
        File.WriteAllText(Path.Combine(current, "ClassIsland.Desktop.exe"), string.Empty);
        File.WriteAllText(Path.Combine(newer, "ClassIsland.Desktop.exe"), string.Empty);
        var configuration = new GuardianConfiguration(classIsland, "ClassIsland.Desktop.exe", "ClassIsland.exe", string.Empty);

        Assert(ClassIslandProcessManager.FindApplicationExecutable(configuration) == Path.Combine(current, "ClassIsland.Desktop.exe"), "Current app directory should be preferred.");
        File.Delete(Path.Combine(current, ".current"));
        Assert(ClassIslandProcessManager.FindApplicationExecutable(configuration) == Path.Combine(newer, "ClassIsland.Desktop.exe"), "Newest app directory should be selected without a current marker.");
    }

    private static void ProcessIdentityRequiresConfiguredInstallationRoot()
    {
        using var fixture = new TemporaryDirectory();
        var classIsland = Path.Combine(fixture.Path, "ClassIsland");
        var trusted = Path.Combine(classIsland, "app-1.0.0.0", "ClassIsland.Desktop.exe");
        var impostor = Path.Combine(fixture.Path, "Other", "app-1.0.0.0", "ClassIsland.Desktop.exe");
        var nested = Path.Combine(classIsland, "app-1.0.0.0", "nested", "ClassIsland.Desktop.exe");

        Assert(ClassIslandProcessManager.IsExpectedClassIslandExecutable(trusted, classIsland), "A ClassIsland executable under an app directory should be trusted.");
        Assert(!ClassIslandProcessManager.IsExpectedClassIslandExecutable(impostor, classIsland), "A same-named executable outside the configured installation must not be trusted.");
        Assert(!ClassIslandProcessManager.IsExpectedClassIslandExecutable(nested, classIsland), "An executable outside an immediate app directory must not be trusted.");
        Assert(!ClassIslandProcessManager.IsExpectedClassIslandExecutable(null, classIsland), "A process with an unavailable executable path must not be trusted.");
    }

    private static void BcdManagerRoutesDefaultAndOneTimeCommands()
    {
        const string output = "Windows Boot Loader\r\nidentifier              {current}\r\ndescription             Windows 11\r\n\r\nWindows Boot Loader\r\nidentifier              {recovery}\r\ndescription             ClassIsland Guardian Recovery\r\n";
        var commands = new FakeCommandRunner(output);
        var paths = new GuardianPaths("C:", @"C:\Program Files", @"C:\Windows", @"C:\GuardianRecovery");
        var manager = new BcdManager(commands, paths, new FileLog(Path.Combine(Path.GetTempPath(), "guardian-bcd-test.log")));

        Assert(manager.SetRecoveryDefault(), "Setting the Recovery BCD default failed.");
        Assert(manager.SetWindowsOnce(), "Setting the Windows BCD one-time boot failed.");
        Assert(commands.Calls.Any(call => call.FileName == "bcdedit" && call.Arguments.SequenceEqual(["/default", "{recovery}"])), "Recovery default command was not routed to bcdedit.");
        Assert(commands.Calls.Any(call => call.FileName == "bcdedit" && call.Arguments.SequenceEqual(["/bootsequence", "{current}"])), "Windows one-time boot command was not routed to bcdedit.");
    }

    private static void RecoveryModeSelectionPrefersRollbackThenUpdate()
    {
        using var fixture = new TemporaryDirectory();
        var paths = new GuardianPaths("C:", Path.Combine(fixture.Path, "Program Files"), Path.Combine(fixture.Path, "Windows"), Path.Combine(fixture.Path, "GuardianRecovery"));
        Directory.CreateDirectory(paths.RecoveryDirectory);
        Assert(RecoveryEngine.SelectMode(paths) == RecoveryMode.Repair, "Recovery mode should default to repair.");
        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, ".update"), string.Empty);
        Assert(RecoveryEngine.SelectMode(paths) == RecoveryMode.Update, "Update marker was not recognized.");
        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, ".rollback"), string.Empty);
        Assert(RecoveryEngine.SelectMode(paths) == RecoveryMode.Rollback, "Rollback marker must take precedence over update.");
    }

    private static void RecoveryRepairRestoresStableProgramAndData()
    {
        using var fixture = new TemporaryDirectory();
        var paths = new GuardianPaths("C:", Path.Combine(fixture.Path, "Program Files"), Path.Combine(fixture.Path, "Windows"), Path.Combine(fixture.Path, "GuardianRecovery"));
        Directory.CreateDirectory(Path.Combine(paths.RecoveryDirectory, "stable", "appdata"));
        Directory.CreateDirectory(Path.Combine(paths.RecoveryDirectory, "stable", "drivers"));
        Directory.CreateDirectory(paths.RecoveryDataDirectory);
        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, "stable", "appdata", "guardian.exe"), "stable");
        File.WriteAllText(Path.Combine(paths.RecoveryDataDirectory, "guardian_config.db"), "data");
        foreach (var driver in new[] { "file.sys", "process.sys", "registry.sys" })
        {
            File.WriteAllText(Path.Combine(paths.RecoveryDirectory, "stable", "drivers", driver), driver);
        }
        Directory.CreateDirectory(paths.GuardianDirectory);
        File.WriteAllText(Path.Combine(paths.GuardianDirectory, "broken.txt"), "broken");

        RecoveryEngine.Repair(paths, new FileLog(Path.Combine(fixture.Path, "recovery.log")));

        Assert(File.ReadAllText(paths.GuardianExecutable) == "stable", "Recovery did not restore guardian.exe.");
        Assert(File.ReadAllText(Path.Combine(paths.GuardianDataDirectory, "guardian_config.db")) == "data", "Recovery did not restore Guardian data.");
        Assert(File.Exists(Path.Combine(paths.DriversDirectory, "file.sys")), "Recovery did not restore drivers.");
    }

    private static void RecoveryUpdateAndRollbackPreserveData()
    {
        using var fixture = new TemporaryDirectory();
        var paths = new GuardianPaths("C:", Path.Combine(fixture.Path, "Program Files"), Path.Combine(fixture.Path, "Windows"), Path.Combine(fixture.Path, "GuardianRecovery"));
        Directory.CreateDirectory(paths.GuardianDirectory);
        Directory.CreateDirectory(paths.GuardianDataDirectory);
        Directory.CreateDirectory(paths.DriversDirectory);
        Directory.CreateDirectory(paths.RecoveryDataDirectory);
        Directory.CreateDirectory(Path.Combine(paths.RecoveryDirectory, "update", "appdata"));
        Directory.CreateDirectory(Path.Combine(paths.RecoveryDirectory, "update", "drivers"));
        File.WriteAllText(paths.GuardianExecutable, "old");
        File.WriteAllText(Path.Combine(paths.GuardianDataDirectory, "guardian_config.db"), "preserved-data");
        File.WriteAllText(Path.Combine(paths.RecoveryDataDirectory, "guardian_config.db"), "preserved-data");
        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, "update", "appdata", "guardian.exe"), "new");
        foreach (var driver in new[] { "file.sys", "process.sys", "registry.sys" })
        {
            File.WriteAllText(Path.Combine(paths.DriversDirectory, driver), "old-" + driver);
            File.WriteAllText(Path.Combine(paths.RecoveryDirectory, "update", "drivers", driver), "new-" + driver);
        }

        File.WriteAllText(Path.Combine(paths.RecoveryDirectory, ".update"), string.Empty);
        var log = new FileLog(Path.Combine(fixture.Path, "recovery.log"));
        RecoveryEngine.Update(paths, log);

        Assert(File.ReadAllText(paths.GuardianExecutable) == "new", "Recovery update did not install the update program.");
        Assert(File.ReadAllText(Path.Combine(paths.RecoveryDirectory, "rollback", "appdata", "guardian.exe")) == "old", "Recovery update did not create rollback data.");
        Assert(File.ReadAllText(Path.Combine(paths.GuardianDataDirectory, "guardian_config.db")) == "preserved-data", "Recovery update did not preserve Guardian data.");
        Assert(File.Exists(Path.Combine(paths.RecoveryDirectory, ".rollback")), "Recovery update did not create the rollback marker.");

        RecoveryEngine.Rollback(paths, log);
        Assert(File.ReadAllText(paths.GuardianExecutable) == "old", "Recovery rollback did not restore the previous program.");
        Assert(!File.Exists(Path.Combine(paths.RecoveryDirectory, ".rollback")), "Recovery rollback did not clear the rollback marker.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly string _output;
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public FakeCommandRunner(string output)
        {
            _output = output;
        }

        public CommandResult Run(string fileName, IEnumerable<string> arguments, bool throwOnError = true)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return new CommandResult(0, _output, string.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClassIslandGuardianTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            FileTree.DeleteIfExists(Path);
        }
    }
}
