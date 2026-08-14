using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Recovery;

public static class Program
{
    public static int Main(string[] args)
    {
        var engine = new RecoveryEngine(new CommandRunner());
        return engine.Run(reboot: !args.Contains("--no-reboot", StringComparer.OrdinalIgnoreCase));
    }
}
