using System.Diagnostics;

namespace ClassIslandGuardian.Common;

public interface ICommandRunner
{
    CommandResult Run(string fileName, IEnumerable<string> arguments, bool throwOnError = true);
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class CommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, IEnumerable<string> arguments, bool throwOnError = true)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(standardOutputTask, standardErrorTask);
        process.WaitForExit();
        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        var result = new CommandResult(process.ExitCode, standardOutput, standardError);
        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with {result.ExitCode}: {standardError}");
        }

        return result;
    }
}
