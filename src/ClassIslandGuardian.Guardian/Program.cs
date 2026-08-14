using ClassIslandGuardian.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ClassIslandGuardian.Guardian;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ClassIsland Guardian only supports Windows.");
            return 1;
        }

        if (!WindowsServiceHelpers.IsWindowsService())
        {
            return await GuardianCommandLine.RunAsync(args);
        }

        var paths = new GuardianPaths();
        var log = new FileLog(Path.Combine(paths.GuardianDataDirectory, "guardian.log"));
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = GuardianPaths.ServiceName);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
        builder.Services.AddSingleton<BcdManager>();
        builder.Services.AddSingleton<GuardianDatabase>();
        builder.Services.AddSingleton<SnapshotManager>();
        builder.Services.AddSingleton<ClassIslandProcessManager>();
        builder.Services.AddHostedService<GuardianWorker>();

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}
